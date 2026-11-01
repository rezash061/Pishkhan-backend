using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Orders.Domain;
using Pishkhan.Modules.Orders.Persistence;
using Pishkhan.Modules.Orders.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Orders.Features;

/// <summary>A catalog line (<c>productId</c>, optional <c>variantId</c>) priced from the catalog, or a custom line with <c>title</c> and <c>unitPrice</c>.</summary>
public sealed record OrderLineRequest(Guid? ProductId, Guid? VariantId, int Quantity, string? Title = null, Money? UnitPrice = null);

/// <summary>
/// The structured fields, or <c>text</c> (an address as the customer typed it) to split into province, city and postal code.
/// Structured fields win over what is parsed from the text. <c>province</c> is a code or a name from <c>GET /orders/provinces</c>.
/// </summary>
public sealed record AddressRequest(string? Recipient, string? Province, string? City, string? Line, string? PostalCode, string? Text = null);

/// <summary>
/// A new order (always a draft) or the new contents of a draft. Give <c>conversationId</c> to link the order to a conversation (its
/// contact is used), or <c>contactId</c>. Amounts are <c>{ amount, currency }</c> in Rial.
/// </summary>
public sealed record OrderRequest(
    Guid? ContactId, Guid? ConversationId, IReadOnlyList<OrderLineRequest>? Items, Money? ShippingFee, Money? Discount, string? CustomerName, string? Phone,
    AddressRequest? Address, string? Note);

public sealed record OrderStatusRequest(string Status, string? Note);

public sealed record OrderNoteRequest(string Note);

public sealed record ParseAddressRequest(string Text);

public sealed record OrderLineResponse(Guid? ProductId, Guid? VariantId, string Title, string? VariantLabel, string? Sku, Money UnitPrice, int Quantity, Money LineTotal);

/// <summary>
/// <c>nextStatuses</c> are the moves allowed now. <c>missing</c> lists what the order still lacks for shipping (<c>address</c>,
/// <c>city</c>, <c>postalCode</c>, <c>phone</c>); payment only needs items and a total above zero.
/// </summary>
public sealed record OrderResponse(
    Guid Id, long Number, string Source, string Status, IReadOnlyList<string> NextStatuses, Guid? ContactId, Guid? ConversationId, string? CustomerName,
    string? Phone, OrderAddress? Address, IReadOnlyList<OrderLineResponse> Items, Money Subtotal, Money ShippingFee, Money Discount, Money Total, string? Note,
    IReadOnlyList<string> Missing, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PaidAt, DateTimeOffset? ClosedAt);

public sealed record OrderEventResponse(Guid Id, string Kind, string? FromStatus, string? ToStatus, string ActorType, Guid? ActorId, string? Note, DateTimeOffset At);

public sealed record InvoiceLine(string Title, string? VariantLabel, string Quantity, string UnitPrice, string LineTotal);

/// <summary>An order ready to print or send: Persian digits, Jalali dates, amounts in Toman.</summary>
public sealed record InvoiceResponse(
    Guid OrderId, long Number, string NumberText, string Date, string DateLong, string Status, string StatusText, string? CustomerName, string? Phone,
    string? Address, string? PostalCode, IReadOnlyList<InvoiceLine> Lines, string Subtotal, string ShippingFee, string Discount, string Total, Money TotalAmount,
    string? PaidDate);

public sealed record ProvinceResponse(string Code, string Name, IReadOnlyList<string> Cities);

internal static class OrderEndpoints
{
    public static readonly IReadOnlyDictionary<string, string> StatusText = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [OrderStatuses.Draft] = "پیش‌نویس",
        [OrderStatuses.AwaitingPayment] = "در انتظار پرداخت",
        [OrderStatuses.Paid] = "پرداخت‌شده",
        [OrderStatuses.Preparing] = "در حال آماده‌سازی",
        [OrderStatuses.Shipped] = "ارسال‌شده",
        [OrderStatuses.Delivered] = "تحویل‌شده",
        [OrderStatuses.Completed] = "تکمیل‌شده",
        [OrderStatuses.Cancelled] = "لغوشده",
        [OrderStatuses.Refunded] = "مسترد‌شده",
        [OrderStatuses.Returned] = "مرجوعی",
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var root = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/orders").WithTags("Orders");

        root.MapGet(string.Empty, ListAsync)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .WithSummary("Orders, newest first")
            .WithDescription("Filters: `status` (repeatable), `contactId`, `conversationId`, `number`, `phone` (exact mobile). Cursor pagination.")
            .Produces<Page<OrderResponse>>();

        root.MapPost(string.Empty, CreateAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithIdempotency()
            .WithSummary("Create a draft order")
            .WithDescription("Catalog lines are priced from the catalog (active products, in stock). Custom lines need `title` and `unitPrice`.")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
            .WithStandardProblems();

        root.MapGet("/provinces", Provinces)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .WithSummary("The provinces of Iran with their larger cities (the capital first)")
            .Produces<IReadOnlyList<ProvinceResponse>>();

        root.MapPost("/parse-address", ParseAddress)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .WithSummary("Split a pasted address into province, city, postal code and mobile")
            .Produces<ParsedAddress>()
            .WithStandardProblems();

        root.MapGet("/{orderId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPut("/{orderId:guid}", UpdateAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithSummary("Replace a draft order's lines, fees and details")
            .WithDescription("Only a draft can be edited (409 `orders.not_editable`); move an order awaiting payment back to `draft` to change it.")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        root.MapPost("/{orderId:guid}/status", ChangeStatusAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithSummary("Move an order to another status")
            .WithDescription("Allowed moves are the order's `nextStatuses` (409 `orders.status_move_invalid` otherwise). `awaiting_payment` needs items and a total above zero. Paying takes stock; cancelling, refunding or returning a paid order puts it back.")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        root.MapPost("/{orderId:guid}/notes", AddNoteAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithSummary("Add a note to the order's timeline")
            .Produces<OrderEventResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        root.MapGet("/{orderId:guid}/timeline", TimelineAsync)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .WithSummary("Everything that happened to the order, oldest first")
            .Produces<IReadOnlyList<OrderEventResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapGet("/{orderId:guid}/invoice", InvoiceAsync)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .WithSummary("The order as an invoice: Jalali dates, Persian digits, amounts in Toman")
            .Produces<InvoiceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static OrderResponse ToResponse(Order o, OrderPhones phones) => new(
        o.Id, o.Number, o.Source, o.Status, [.. OrderStatuses.Next[o.Status]], o.ContactId, o.ConversationId, o.CustomerName, phones.Reveal(o.PhoneEnc), o.Address,
        [.. o.Items.Select(i => new OrderLineResponse(i.ProductId, i.VariantId, i.Title, i.VariantLabel, i.Sku, Money.Rial(i.UnitPrice), i.Quantity, Money.Rial(i.LineTotal)))],
        Money.Rial(o.Subtotal), Money.Rial(o.ShippingFee), Money.Rial(o.Discount), o.TotalMoney, o.Note, o.MissingForShipping(), o.Revision,
        o.CreatedAt, o.UpdatedAt, o.PaidAt, o.ClosedAt);

    private static OrderEventResponse ToResponse(OrderEvent e) => new(e.Id, e.Kind, e.FromStatus, e.ToStatus, e.ActorType, e.ActorId, e.Note, e.CreatedAt);

    private static async Task<IResult> ListAsync(
        Guid workspaceId, string[]? status, Guid? contactId, Guid? conversationId, long? number, string? phone, string? cursor, int? limit, OrdersDbContext db,
        OrderPhones phones, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.Orders.AsNoTracking();
        if (status is { Length: > 0 })
        {
            var statuses = status.Take(OrderStatuses.All.Count).ToList();
            query = query.Where(o => statuses.Contains(o.Status));
        }

        if (contactId is { } contact)
        {
            query = query.Where(o => o.ContactId == contact);
        }

        if (conversationId is { } conversation)
        {
            query = query.Where(o => o.ConversationId == conversation);
        }

        if (number is { } n)
        {
            query = query.Where(o => o.Number == n);
        }

        if (!string.IsNullOrWhiteSpace(phone))
        {
            var bidx = phones.Index(phone);
            query = bidx is null ? query.Where(_ => false) : query.Where(o => o.PhoneBidx == bidx);
        }

        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(o => EF.Functions.LessThan(ValueTuple.Create(o.CreatedAt, o.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].CreatedAt, rows[take - 1].Id) : null;
        return Results.Ok(new Page<OrderResponse>([.. rows.Take(take).Select(o => ToResponse(o, phones))], next));
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid orderId, OrdersDbContext db, OrderPhones phones, CancellationToken ct) =>
        await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct) is { } order ? Results.Ok(ToResponse(order, phones)) : Error.NotFound().ToProblem();

    private static async Task<IResult> CreateAsync(
        Guid workspaceId, OrderRequest request, OrdersDbContext db, OrderPricing pricing, OrderPhones phones, IConversationQueries conversations, ICurrentUser caller,
        IClock clock, CancellationToken ct)
    {
        Guid? contactId = request.ContactId;
        string? contactName = null;
        if (request.ConversationId is { } conversationId)
        {
            var conversation = await conversations.GetAsync(conversationId, ct);
            if (conversation is null || request.ContactId is { } c && c != conversation.ContactId)
            {
                return Invalid([("conversationId", "orders.conversation_not_found")]);
            }

            contactId = conversation.ContactId;
            contactName = conversation.ContactName;
        }
        else if (request.ContactId is { } id)
        {
            var contact = await conversations.GetContactAsync(id, ct);
            if (contact is null)
            {
                return Invalid([("contactId", "orders.contact_not_found")]);
            }

            contactName = contact.DisplayName;
        }

        var contents = await ContentsAsync(request, pricing, phones, ct);
        if (contents.Problem is { } problem)
        {
            return problem;
        }

        var now = clock.UtcNow;
        var order = Order.Create(workspaceId, await db.NextNumberAsync(workspaceId, ct), OrderSources.Panel, contactId, request.ConversationId, now);
        order.Edit(contents.Items, contents.Shipping, contents.Discount, request.CustomerName ?? contactName, contents.Phone, contents.Address, request.Note, now);
        db.Orders.Add(order);
        db.Events.Add(OrderEvent.For(order, OrderEventKinds.Created, OrderActor.User(caller.UserId), now));
        db.Publish(order.Changed(null, now), order.Id.ToString(), workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(order, phones), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        Guid workspaceId, Guid orderId, OrderRequest request, OrdersDbContext db, OrderPricing pricing, OrderPhones phones, ICurrentUser caller, IClock clock,
        CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (!order.IsEditable)
        {
            return Error.Conflict("orders.not_editable").ToProblem();
        }

        var contents = await ContentsAsync(request, pricing, phones, ct);
        if (contents.Problem is { } problem)
        {
            return problem;
        }

        var now = clock.UtcNow;
        order.Edit(contents.Items, contents.Shipping, contents.Discount, request.CustomerName, contents.Phone, contents.Address, request.Note, now);
        db.Events.Add(OrderEvent.For(order, OrderEventKinds.Edited, OrderActor.User(caller.UserId), now));
        return await SaveAsync(db, ct) ?? Results.Ok(ToResponse(order, phones));
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid workspaceId, Guid orderId, OrderStatusRequest request, OrdersDbContext db, OrderPhones phones, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (string.IsNullOrWhiteSpace(request.Status) || !OrderStatuses.Next.ContainsKey(request.Status))
        {
            return Invalid([("status", "orders.status_unknown")]);
        }

        if (request.Note is { Length: > Order.MaxNote })
        {
            return Invalid([("note", "orders.note_too_long")]);
        }

        if (request.Status == OrderStatuses.AwaitingPayment && order.MissingForPayment() is { Count: > 0 } missing)
        {
            return Invalid([.. missing.Select(m => (m, "orders.incomplete"))]);
        }

        var from = order.Status;
        var now = clock.UtcNow;
        if (!order.MoveTo(request.Status, now))
        {
            return Error.Conflict("orders.status_move_invalid").ToProblem();
        }

        db.Events.Add(OrderEvent.For(order, OrderEventKinds.StatusChanged, OrderActor.User(caller.UserId), now, from, request.Note));
        db.Publish(order.Changed(from, now), order.Id.ToString(), workspaceId);
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), "order.status_changed", "order", order.Id.ToString(), workspaceId, now,
            before: new { status = from }, after: new { status = order.Status, number = order.Number, total = order.Total }, reason: request.Note);
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        return await SaveAsync(db, ct) ?? Results.Ok(ToResponse(order, phones));
    }

    private static async Task<IResult> AddNoteAsync(Guid workspaceId, Guid orderId, OrderNoteRequest request, OrdersDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > Order.MaxNote)
        {
            return Invalid([("note", "orders.note_invalid")]);
        }

        var note = OrderEvent.For(order, OrderEventKinds.Note, OrderActor.User(caller.UserId), clock.UtcNow, note: request.Note);
        db.Events.Add(note);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(note), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> TimelineAsync(Guid workspaceId, Guid orderId, OrdersDbContext db, CancellationToken ct)
    {
        if (!await db.Orders.AnyAsync(o => o.Id == orderId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        var events = await db.Events.AsNoTracking().Where(e => e.OrderId == orderId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Take(1000).ToListAsync(ct);
        return Results.Ok(events.Select(ToResponse).ToList());
    }

    private static async Task<IResult> InvoiceAsync(Guid workspaceId, Guid orderId, OrdersDbContext db, OrderPhones phones, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId, ct);
        if (o is null)
        {
            return Error.NotFound().ToProblem();
        }

        var phone = phones.Reveal(o.PhoneEnc);
        var address = o.Address is { } a
            ? string.Join("، ", new[] { a.Province, a.City is { } city && city != a.Province ? city : null, a.Line }.Where(p => !string.IsNullOrWhiteSpace(p)))
            : null;
        return Results.Ok(new InvoiceResponse(
            o.Id, o.Number, PersianDigits.ToPersian(o.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)), Jalali.FormatShort(o.CreatedAt),
            Jalali.FormatLong(o.CreatedAt), o.Status, StatusText[o.Status], o.CustomerName,
            phone is null ? null : PersianDigits.ToPersian("0" + phone[3..]), address,
            o.Address?.PostalCode is { } postal ? PersianDigits.ToPersian(postal) : null,
            [.. o.Items.Select(i => new InvoiceLine(
                i.Title, i.VariantLabel, PersianDigits.ToPersian(i.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                PersianMoney.FormatToman(Money.Rial(i.UnitPrice)), PersianMoney.FormatToman(Money.Rial(i.LineTotal))))],
            PersianMoney.FormatToman(Money.Rial(o.Subtotal)), PersianMoney.FormatToman(Money.Rial(o.ShippingFee)), PersianMoney.FormatToman(Money.Rial(o.Discount)),
            PersianMoney.FormatToman(o.TotalMoney), o.TotalMoney, o.PaidAt is { } paid ? Jalali.FormatShort(paid) : null));
    }

    private static IResult Provinces(Guid workspaceId) =>
        Results.Ok(IranianProvinces.All.Select(p => new ProvinceResponse(p.Code, p.Name, p.Cities)).ToList());

    private static IResult ParseAddress(Guid workspaceId, ParseAddressRequest request) =>
        string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > Order.MaxAddressLine * 2
            ? Invalid([("text", "orders.address_invalid")])
            : Results.Ok(AddressParser.Parse(request.Text));

    private sealed record Contents(List<OrderItem> Items, long Shipping, long Discount, (string Enc, string Bidx)? Phone, OrderAddress? Address, IResult? Problem);

    /// <summary>Validates and prices a request's lines, fees, phone and address.</summary>
    private static async Task<Contents> ContentsAsync(OrderRequest request, OrderPricing pricing, OrderPhones phones, CancellationToken ct)
    {
        Contents Fail(params (string Field, string Code)[] errors) => new([], 0, 0, null, null, Invalid(errors));

        if (request.ShippingFee is { Amount: < 0 or > Order.MaxAmount } || request.ShippingFee?.Currency is { } sc && sc != Currency.Irr)
        {
            return Fail(("shippingFee", "orders.amount_invalid"));
        }

        if (request.Discount is { Amount: < 0 or > Order.MaxAmount } || request.Discount?.Currency is { } dc && dc != Currency.Irr)
        {
            return Fail(("discount", "orders.amount_invalid"));
        }

        if (request.CustomerName is { Length: > Order.MaxName })
        {
            return Fail(("customerName", "orders.name_too_long"));
        }

        if (request.Note is { Length: > Order.MaxNote })
        {
            return Fail(("note", "orders.note_too_long"));
        }

        (string Enc, string Bidx)? phone = null;
        if (!string.IsNullOrWhiteSpace(request.Phone) && (phone = phones.Protect(request.Phone)) is null)
        {
            return Fail(("phone", "orders.phone_invalid"));
        }

        var (address, addressError) = Address(request.Address);
        if (addressError is { } ae)
        {
            return Fail(ae);
        }

        var lines = (request.Items ?? []).Select(i => new LineRequest(i.ProductId, i.VariantId, i.Quantity, i.Title, i.UnitPrice?.Amount)).ToList();
        if ((request.Items ?? []).Any(i => i.UnitPrice is { } p && p.Currency != Currency.Irr))
        {
            return Fail(("items", "orders.amount_invalid"));
        }

        var (items, error) = await pricing.PriceAsync(lines, allowCustom: true, ct);
        if (error is not null)
        {
            return Fail(($"items[{error.Line}]", error.Code));
        }

        return new Contents(items, request.ShippingFee?.Amount ?? 0, request.Discount?.Amount ?? 0, phone, address, null);
    }

    private static (OrderAddress? Address, (string, string)? Error) Address(AddressRequest? request)
    {
        if (request is null)
        {
            return (null, null);
        }

        var parsed = string.IsNullOrWhiteSpace(request.Text) ? null : AddressParser.Parse(request.Text);
        var line = OrderAddresses.Clip(request.Line, Order.MaxAddressLine) ?? (parsed is { Line.Length: > 0 } ? OrderAddresses.Clip(parsed.Line, Order.MaxAddressLine) : null);
        if (line is null)
        {
            return (null, ("address.line", "orders.address_line_required"));
        }

        IranianProvince? province = null;
        if (!string.IsNullOrWhiteSpace(request.Province))
        {
            province = IranianProvinces.Find(request.Province);
            if (province is null)
            {
                return (null, ("address.province", "orders.province_unknown"));
            }
        }
        else if (parsed?.ProvinceCode is { } code)
        {
            province = IranianProvinces.Find(code);
        }

        var city = OrderAddresses.Clip(request.City, Order.MaxName) ?? parsed?.City;
        province ??= IranianProvinces.OfCity(city);

        string? postal = null;
        if (!string.IsNullOrWhiteSpace(request.PostalCode))
        {
            if (!PostalCode.IsValid(request.PostalCode))
            {
                return (null, ("address.postalCode", "orders.postal_code_invalid"));
            }

            postal = PersianDigits.DigitsOnly(request.PostalCode);
        }
        else
        {
            postal = parsed?.PostalCode;
        }

        return (new OrderAddress(OrderAddresses.Clip(request.Recipient, Order.MaxName), province?.Code, province?.Name, city, line, postal), null);
    }

    /// <summary>Saves; a concurrent change of the same order is a 409.</summary>
    private static async Task<IResult?> SaveAsync(OrdersDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("orders.changed_concurrently").ToProblem();
        }
    }

    private static IResult Invalid(IReadOnlyList<(string Field, string Code)> errors) =>
        Error.Validation(errors[0].Code, errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).Distinct().ToArray(), StringComparer.Ordinal)).ToProblem();
}
