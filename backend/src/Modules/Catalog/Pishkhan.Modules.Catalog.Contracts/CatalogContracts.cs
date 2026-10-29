using Pishkhan.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Contracts;

public static class ProductStatuses
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Archived = "archived";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Draft, Active, Archived };
}

/// <summary><see cref="Track"/>: <c>stock</c> counts units and 0 is out of stock. <see cref="Unlimited"/>: always available (made to order, digital).</summary>
public static class StockPolicies
{
    public const string Track = "track";
    public const string Unlimited = "unlimited";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Track, Unlimited };
}

public static class PricingModes
{
    public const string Fixed = "fixed";

    /// <summary>Gold or currency rate formula (Phase 3).</summary>
    public const string Formula = "formula";
}

public static class ProductChangeKinds
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
}

/// <summary>A product was created, changed or deleted. Knowledge re-indexes its catalog chunks; caches of cards and prices are invalidated.</summary>
[IntegrationEvent(Topics.ProductChanged)]
public sealed record ProductChanged(Guid WorkspaceId, Guid ProductId, string Change, int Revision) : IIntegrationEvent;

/// <summary>Commerce group: reads an uploaded Excel sheet and upserts its products.</summary>
[IntegrationEvent(Topics.CatalogImportRequested)]
public sealed record CatalogImportRequested(Guid ImportId) : IIntegrationEvent;

/// <summary>Payloads of card buttons. A tap comes back as a postback with this payload.</summary>
public static class CatalogPayloads
{
    public const string ProductPrefix = "catalog.product:";

    public static string Product(Guid productId) => ProductPrefix + productId.ToString("N");

    public static bool TryParseProduct(string? payload, out Guid productId)
    {
        productId = Guid.Empty;
        return payload is not null && payload.StartsWith(ProductPrefix, StringComparison.Ordinal) && Guid.TryParseExact(payload[ProductPrefix.Length..], "N", out productId);
    }
}

/// <summary>One card of a carousel, ready for a channel: texts already within Instagram's limits (80 characters).</summary>
public sealed record StorefrontCard(Guid ProductId, string Title, string Subtitle, Uri? ImageUrl, Uri? LinkUrl, string ButtonTitle, string Payload);

public sealed record StorefrontCardSetContent(Guid Id, string Name, IReadOnlyList<StorefrontCard> Cards);

public sealed record VariantSummary(Guid Id, string? Sku, IReadOnlyDictionary<string, string> Attributes, Money Price, bool InStock, int? Stock);

/// <summary>A product as other modules see it (the AI's catalog tool, knowledge chunks, product detail replies).</summary>
public sealed record ProductSummary(
    Guid Id, string Title, string? Description, string? Category, IReadOnlyList<string> Tags, string Status, Money? MinPrice, Money? MaxPrice, bool InStock,
    IReadOnlyList<VariantSummary> Variants, Uri? LinkUrl, int Revision, DateTimeOffset UpdatedAt);

/// <summary>In-process API of the Catalog module, scoped to the current workspace.</summary>
public interface ICatalogQueries
{
    /// <summary>The card set's active products as cards, at most 10 (Instagram's carousel limit). Null when the set does not exist.</summary>
    Task<StorefrontCardSetContent?> GetCardSetAsync(Guid cardSetId, CancellationToken cancellationToken);

    Task<ProductSummary?> GetProductAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Active products whose title, description or tags contain the text (Persian normalized), best matches first.</summary>
    Task<IReadOnlyList<ProductSummary>> SearchAsync(string text, int limit, CancellationToken cancellationToken);
}
