using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Orders.Domain;

/// <summary>
/// One order line. Catalog lines keep the product's title and price at the time they were added (later catalog changes do not
/// change the order). A custom line (no product) is only possible from the panel. Prices are Rial.
/// </summary>
public sealed record OrderItem(Guid? ProductId, Guid? VariantId, string Title, string? VariantLabel, string? Sku, long UnitPrice, int Quantity)
{
    public long LineTotal => checked(UnitPrice * Quantity);
}

/// <summary>A delivery address. <see cref="ProvinceCode"/> is from <c>IranianProvinces</c>; the city may be any town.</summary>
public sealed record OrderAddress(string? Recipient, string? ProvinceCode, string? Province, string? City, string Line, string? PostalCode);

public static class OrderEventKinds
{
    public const string Created = "created";
    public const string StatusChanged = "status_changed";
    public const string Edited = "edited";
    public const string Note = "note";
}

public static class OrderActorTypes
{
    public const string User = "user";
    public const string Ai = "ai";
    public const string System = "system";
}

/// <summary>Who did something to an order, for the timeline.</summary>
public sealed record OrderActor(string Type, Guid? Id)
{
    public static readonly OrderActor Ai = new(OrderActorTypes.Ai, null);
    public static readonly OrderActor System = new(OrderActorTypes.System, null);

    public static OrderActor User(Guid userId) => new(OrderActorTypes.User, userId);
}

/// <summary>An order (backend plan 9.9). <see cref="Number"/> is the per-workspace sequence shown to sellers and customers.</summary>
public sealed class Order : AggregateRoot<Guid>, ITenantOwned
{
    public const int MaxItems = 50;
    public const int MaxQuantity = 1_000;
    public const int MaxTitle = 200;
    public const int MaxNote = 2_000;
    public const int MaxName = 100;
    public const int MaxAddressLine = 500;
    public const long MaxAmount = 1_000_000_000_000; // 100 billion Toman

    private Order()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public long Number { get; private set; }

    public string Source { get; private set; } = OrderSources.Panel;

    public string Status { get; private set; } = OrderStatuses.Draft;

    public Guid? ContactId { get; private set; }

    public Guid? ConversationId { get; private set; }

    public string? CustomerName { get; private set; }

    /// <summary>The customer's mobile, envelope-encrypted (E.164 inside).</summary>
    public string? PhoneEnc { get; private set; }

    /// <summary>Blind index of the mobile, for an exact search.</summary>
    public string? PhoneBidx { get; private set; }

    public OrderAddress? Address { get; private set; }

    public List<OrderItem> Items { get; private set; } = [];

    public long Subtotal { get; private set; }

    public long ShippingFee { get; private set; }

    public long Discount { get; private set; }

    public long Total { get; private set; }

    public string? Note { get; private set; }

    public int Revision { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Money TotalMoney => Money.Rial(Total);

    public static Order Create(Guid workspaceId, long number, string source, Guid? contactId, Guid? conversationId, DateTimeOffset now) =>
        new()
        {
            Id = Uuid.NewV7(now), WorkspaceId = workspaceId, Number = number, Source = source, ContactId = contactId, ConversationId = conversationId,
            CreatedAt = now, UpdatedAt = now,
        };

    public bool IsEditable => OrderStatuses.Editable.Contains(Status);

    /// <summary>Replaces the lines, fees and details. Only a draft can be edited.</summary>
    public void Edit(IReadOnlyList<OrderItem> items, long shippingFee, long discount, string? customerName, (string Enc, string Bidx)? phone, OrderAddress? address, string? note, DateTimeOffset now)
    {
        if (!IsEditable)
        {
            throw new DomainException("orders.not_editable", "Only a draft order can be edited");
        }

        Items = [.. items];
        ShippingFee = shippingFee;
        Discount = discount;
        Subtotal = items.Sum(i => i.LineTotal);
        Total = Math.Max(0, checked(Subtotal + shippingFee - discount));
        CustomerName = string.IsNullOrWhiteSpace(customerName) ? null : customerName.Trim();
        (PhoneEnc, PhoneBidx) = phone is { } p ? (p.Enc, p.Bidx) : ((string?)null, (string?)null);
        Address = address;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Touch(now);
    }

    /// <summary>What must be filled in before the order can ask for payment.</summary>
    public IReadOnlyList<string> MissingForPayment()
    {
        var missing = new List<string>();
        if (Items.Count == 0)
        {
            missing.Add("items");
        }

        if (Total <= 0)
        {
            missing.Add("total");
        }

        return missing;
    }

    /// <summary>What a shipped order still lacks (shown to the seller, not enforced before payment).</summary>
    public IReadOnlyList<string> MissingForShipping()
    {
        var missing = new List<string>();
        if (Address is null || string.IsNullOrWhiteSpace(Address.Line))
        {
            missing.Add("address");
        }
        else
        {
            if (Address.City is null)
            {
                missing.Add("city");
            }

            if (Address.PostalCode is null)
            {
                missing.Add("postalCode");
            }
        }

        if (PhoneEnc is null)
        {
            missing.Add("phone");
        }

        return missing;
    }

    /// <summary>Moves the order; false when the move is not allowed from the current status.</summary>
    public bool MoveTo(string status, DateTimeOffset now)
    {
        if (!OrderStatuses.CanMove(Status, status))
        {
            return false;
        }

        Status = status;
        if (status == OrderStatuses.Paid)
        {
            PaidAt ??= now;
        }

        ClosedAt = status is OrderStatuses.Completed or OrderStatuses.Cancelled or OrderStatuses.Refunded ? now : null;
        Touch(now);
        return true;
    }

    public OrderStatusChanged Changed(string? from, DateTimeOffset now) =>
        new(WorkspaceId, Id, Number, ContactId, ConversationId, from, Status, TotalMoney,
            [.. Items.Select(i => new OrderLineSnapshot(i.ProductId, i.VariantId, i.Quantity))], now);

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Revision++;
    }
}

/// <summary>One entry of an order's timeline: created, a status change, an edit or a note.</summary>
public sealed class OrderEvent : Entity<Guid>, ITenantOwned
{
    private OrderEvent()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid OrderId { get; private set; }

    public string Kind { get; private set; } = OrderEventKinds.Created;

    public string? FromStatus { get; private set; }

    public string? ToStatus { get; private set; }

    public string ActorType { get; private set; } = OrderActorTypes.System;

    public Guid? ActorId { get; private set; }

    public string? Note { get; private set; }

    public static OrderEvent For(Order order, string kind, OrderActor actor, DateTimeOffset now, string? from = null, string? note = null) =>
        new()
        {
            Id = Uuid.NewV7(now), WorkspaceId = order.WorkspaceId, OrderId = order.Id, Kind = kind, FromStatus = from,
            ToStatus = kind is OrderEventKinds.Created or OrderEventKinds.StatusChanged ? order.Status : null,
            ActorType = actor.Type, ActorId = actor.Id, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), CreatedAt = now, UpdatedAt = now,
        };
}
