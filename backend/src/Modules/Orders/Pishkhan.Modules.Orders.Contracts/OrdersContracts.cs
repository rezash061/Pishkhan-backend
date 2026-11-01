using Pishkhan.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Orders.Contracts;

/// <summary>
/// Order statuses (backend plan 9.9): <c>draft → awaiting_payment → paid → preparing → shipped → delivered → completed</c>, plus
/// <c>cancelled</c>, <c>refunded</c> and <c>returned</c>. <see cref="Next"/> lists the moves allowed from each status.
/// </summary>
public static class OrderStatuses
{
    public const string Draft = "draft";
    public const string AwaitingPayment = "awaiting_payment";
    public const string Paid = "paid";
    public const string Preparing = "preparing";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Refunded = "refunded";
    public const string Returned = "returned";

    public static readonly IReadOnlyList<string> All = [Draft, AwaitingPayment, Paid, Preparing, Shipped, Delivered, Completed, Cancelled, Refunded, Returned];

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Next = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        [Draft] = Set(AwaitingPayment, Cancelled),
        [AwaitingPayment] = Set(Draft, Paid, Cancelled),
        [Paid] = Set(Preparing, Shipped, Refunded),
        [Preparing] = Set(Shipped, Refunded),
        [Shipped] = Set(Delivered, Returned),
        [Delivered] = Set(Completed, Returned),
        [Completed] = Set(Returned),
        [Returned] = Set(Refunded),
        [Cancelled] = Set(),
        [Refunded] = Set(),
    };

    /// <summary>Statuses in which the goods are taken from stock: entering one takes stock, leaving them for cancelled, refunded or returned puts it back.</summary>
    public static readonly IReadOnlySet<string> Stocked = Set(Paid, Preparing, Shipped, Delivered, Completed);

    /// <summary>No further changes except the allowed moves; items, prices and the address are fixed outside these.</summary>
    public static readonly IReadOnlySet<string> Editable = Set(Draft);

    public static bool CanMove(string from, string to) => Next.TryGetValue(from, out var next) && next.Contains(to);

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}

public static class OrderSources
{
    /// <summary>The AI agent's <c>create_order</c> tool.</summary>
    public const string Ai = "ai";
    public const string Flow = "flow";
    public const string Panel = "panel";
}

/// <summary>One order line as other modules need it (stock changes).</summary>
public sealed record OrderLineSnapshot(Guid? ProductId, Guid? VariantId, int Quantity);

/// <summary>
/// An order was created (<see cref="FromStatus"/> null) or changed status. Catalog takes and returns stock (<see cref="OrderStatuses.Stocked"/>);
/// automation rules (<c>order_status_changed</c>) and notifications follow.
/// </summary>
[IntegrationEvent(Topics.OrderStatusChanged)]
public sealed record OrderStatusChanged(
    Guid WorkspaceId, Guid OrderId, long Number, Guid? ContactId, Guid? ConversationId, string? FromStatus, string ToStatus, Money Total,
    IReadOnlyList<OrderLineSnapshot> Lines, DateTimeOffset OccurredAt) : IIntegrationEvent;

/// <summary>A line the AI asks for. <see cref="VariantId"/> may be left out when the product has one variant or none.</summary>
public sealed record DraftOrderItem(Guid ProductId, Guid? VariantId, int Quantity);

/// <summary>What the AI collected. <see cref="Address"/> is free text; it is split into province, city and postal code.</summary>
public sealed record DraftOrderRequest(
    Guid ConversationId, Guid ContactId, IReadOnlyList<DraftOrderItem> Items, string? CustomerName, string? Phone, string? Address, string? Note);

/// <summary>A priced line: the price always comes from the catalog.</summary>
public sealed record PricedLine(Guid ProductId, Guid? VariantId, string Title, string? VariantLabel, Money UnitPrice, int Quantity, Money LineTotal);

/// <summary>
/// The outcome of <see cref="IOrderCommands"/>. <see cref="Error"/> is a code when nothing was priced or saved (<c>orders.product_not_found</c>,
/// <c>orders.variant_required</c>, <c>orders.out_of_stock</c>, ...), with <see cref="ErrorDetail"/> naming the line.
/// </summary>
public sealed record DraftOrderResult(
    string? Error, string? ErrorDetail, Guid? OrderId, long? Number, IReadOnlyList<PricedLine> Lines, Money Total, IReadOnlyList<string> Missing)
{
    public bool Ok => Error is null;
}

/// <summary>In-process API of the Orders module, scoped to the current workspace (the AI agent's order tool).</summary>
public interface IOrderCommands
{
    /// <summary>Prices the lines from the catalog without saving anything (test chat and demo).</summary>
    Task<DraftOrderResult> QuoteAsync(IReadOnlyList<DraftOrderItem> items, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the conversation's AI draft order, or replaces the lines and details of the one it already has, with catalog prices only.
    /// <see cref="DraftOrderResult.Missing"/> lists what the seller still needs (address, postal code, phone) before asking for payment.
    /// </summary>
    Task<DraftOrderResult> SaveDraftAsync(DraftOrderRequest request, CancellationToken cancellationToken);
}
