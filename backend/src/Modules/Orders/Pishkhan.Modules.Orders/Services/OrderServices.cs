using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Orders.Domain;
using Pishkhan.Modules.Orders.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Orders.Services;

/// <summary>A line to price: a catalog product, or (panel only) a custom line with its own title and price.</summary>
internal sealed record LineRequest(Guid? ProductId, Guid? VariantId, int Quantity, string? Title = null, long? UnitPrice = null);

/// <summary>Why a line could not be priced: the code and the line's index.</summary>
internal sealed record PricingError(string Code, int Line);

/// <summary>
/// Prices order lines from the catalog. The catalog is the only source of prices for catalog lines: the product must be active and
/// the variant in stock (a tracked variant needs at least the quantity).
/// </summary>
internal sealed class OrderPricing(ICatalogQueries catalog)
{
    public async Task<(List<OrderItem> Items, PricingError? Error)> PriceAsync(IReadOnlyList<LineRequest> lines, bool allowCustom, CancellationToken ct)
    {
        var items = new List<OrderItem>();
        if (lines.Count > Order.MaxItems)
        {
            return (items, new PricingError("orders.too_many_items", Order.MaxItems));
        }

        var products = new Dictionary<Guid, ProductSummary?>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Quantity is < 1 or > Order.MaxQuantity)
            {
                return (items, new PricingError("orders.quantity_invalid", i));
            }

            if (line.ProductId is not { } productId)
            {
                if (!allowCustom)
                {
                    return (items, new PricingError("orders.product_required", i));
                }

                var title = line.Title?.Trim();
                if (string.IsNullOrEmpty(title) || title.Length > Order.MaxTitle)
                {
                    return (items, new PricingError("orders.title_invalid", i));
                }

                if (line.UnitPrice is not (>= 0 and <= Order.MaxAmount))
                {
                    return (items, new PricingError("orders.price_invalid", i));
                }

                items.Add(new OrderItem(null, null, title, null, null, line.UnitPrice.Value, line.Quantity));
                continue;
            }

            if (!products.TryGetValue(productId, out var product))
            {
                product = products[productId] = await catalog.GetProductAsync(productId, ct);
            }

            if (product is not { Status: ProductStatuses.Active })
            {
                return (items, new PricingError("orders.product_not_found", i));
            }

            if (product.Variants.Count == 0)
            {
                if (line.VariantId is not null)
                {
                    return (items, new PricingError("orders.variant_not_found", i));
                }

                if (product.MinPrice is not { } price)
                {
                    return (items, new PricingError("orders.price_missing", i));
                }

                items.Add(new OrderItem(product.Id, null, product.Title, null, null, price.Amount, line.Quantity));
                continue;
            }

            var variant = line.VariantId is { } variantId ? product.Variants.FirstOrDefault(v => v.Id == variantId)
                : product.Variants.Count == 1 ? product.Variants[0]
                : null;
            if (variant is null)
            {
                return (items, new PricingError(line.VariantId is null ? "orders.variant_required" : "orders.variant_not_found", i));
            }

            // A variant counted in stock needs the whole quantity; one with unlimited stock has no count.
            if (!variant.InStock || variant.Stock is { } stock && stock < line.Quantity + Reserved(items, variant.Id))
            {
                return (items, new PricingError("orders.out_of_stock", i));
            }

            var label = string.Join("، ", variant.Attributes.Select(a => $"{a.Key}: {a.Value}"));
            items.Add(new OrderItem(product.Id, variant.Id, product.Title, label.Length == 0 ? null : label, variant.Sku, variant.Price.Amount, line.Quantity));
        }

        return (items, null);
    }

    private static int Reserved(List<OrderItem> items, Guid variantId) => items.Where(i => i.VariantId == variantId).Sum(i => i.Quantity);
}

/// <summary>Customer mobiles on orders: encrypted, with a blind index for exact search (backend plan 3.4).</summary>
internal sealed class OrderPhones(ISecretProtector protector, IBlindIndexer indexer)
{
    private const string Purpose = "orders.phone";

    public (string Enc, string Bidx)? Protect(string? input) =>
        IranianMobile.ToE164(input) is { } e164 ? (protector.Protect(e164, Purpose), indexer.Compute(e164, Purpose)) : null;

    public string? Reveal(string? enc) => enc is null ? null : protector.Unprotect(enc, Purpose);

    public string? Index(string? input) => IranianMobile.ToE164(input) is { } e164 ? indexer.Compute(e164, Purpose) : null;
}

internal static class OrderAddresses
{
    /// <summary>A structured address from free text, or null when there is no text.</summary>
    public static OrderAddress? FromText(string? text, string? recipient)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parsed = AddressParser.Parse(text);
        var line = parsed.Line.Length > 0 ? parsed.Line : text.Trim();
        return new OrderAddress(Clip(recipient, Order.MaxName), parsed.ProvinceCode, parsed.Province, parsed.City, Clip(line, Order.MaxAddressLine)!, parsed.PostalCode);
    }

    public static string? Clip(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var t && t.Length > max ? t[..max] : text.Trim();
}

/// <summary>
/// <see cref="IOrderCommands"/> for the AI agent: one AI draft per conversation, created or replaced, with catalog prices only. A
/// draft the seller has moved on (awaiting payment or later) is never touched; the next call starts a new draft.
/// </summary>
internal sealed class OrderCommands(OrdersDbContext db, OrderPricing pricing, OrderPhones phones, ITenantContext tenant, IClock clock) : IOrderCommands
{
    public async Task<DraftOrderResult> QuoteAsync(IReadOnlyList<DraftOrderItem> items, CancellationToken cancellationToken)
    {
        var (priced, error) = await pricing.PriceAsync(Lines(items), allowCustom: false, cancellationToken);
        return Result(error, null, priced, [], items);
    }

    public async Task<DraftOrderResult> SaveDraftAsync(DraftOrderRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = tenant.WorkspaceId ?? throw new InvalidOperationException("No workspace in scope");
        var (priced, error) = await pricing.PriceAsync(Lines(request.Items), allowCustom: false, cancellationToken);
        if (error is not null || priced.Count == 0)
        {
            return Result(error ?? new PricingError("orders.items_required", 0), null, priced, [], request.Items);
        }

        var now = clock.UtcNow;
        var order = await db.Orders.FirstOrDefaultAsync(
            o => o.ConversationId == request.ConversationId && o.Source == OrderSources.Ai && o.Status == OrderStatuses.Draft, cancellationToken);
        var created = order is null;
        if (order is null)
        {
            order = Order.Create(workspaceId, await db.NextNumberAsync(workspaceId, cancellationToken), OrderSources.Ai, request.ContactId, request.ConversationId, now);
            db.Orders.Add(order);
        }

        // Details the customer gave earlier stay when this call leaves them out.
        var phone = phones.Protect(request.Phone);
        var address = OrderAddresses.FromText(request.Address, request.CustomerName ?? order.Address?.Recipient) ?? order.Address;
        order.Edit(
            priced, order.ShippingFee, order.Discount, OrderAddresses.Clip(request.CustomerName, Order.MaxName) ?? order.CustomerName,
            phone ?? (order.PhoneEnc is { } enc ? (enc, order.PhoneBidx!) : null), address, OrderAddresses.Clip(request.Note, Order.MaxNote) ?? order.Note, now);
        db.Events.Add(OrderEvent.For(order, created ? OrderEventKinds.Created : OrderEventKinds.Edited, OrderActor.Ai, now));
        if (created)
        {
            db.Publish(order.Changed(null, now), order.Id.ToString(), workspaceId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result(null, order, priced, [.. order.MissingForShipping()], request.Items);
    }

    private static List<LineRequest> Lines(IReadOnlyList<DraftOrderItem> items) => [.. items.Select(i => new LineRequest(i.ProductId, i.VariantId, i.Quantity))];

    private static DraftOrderResult Result(PricingError? error, Order? order, List<OrderItem> items, IReadOnlyList<string> missing, IReadOnlyList<DraftOrderItem> asked)
    {
        var lines = items.Select(i => new PricedLine(i.ProductId!.Value, i.VariantId, i.Title, i.VariantLabel, Money.Rial(i.UnitPrice), i.Quantity, Money.Rial(i.LineTotal))).ToList();
        var total = Money.Rial(order?.Total ?? items.Sum(i => i.LineTotal));
        return error is null
            ? new DraftOrderResult(null, null, order?.Id, order?.Number, lines, total, missing)
            : new DraftOrderResult(error.Code, error.Line < asked.Count ? asked[error.Line].ProductId.ToString() : null, null, null, [], Money.Rial(0), []);
    }
}
