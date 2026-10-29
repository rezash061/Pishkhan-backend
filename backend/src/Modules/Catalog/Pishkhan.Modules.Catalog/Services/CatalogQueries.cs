using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Domain;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Services;

public static class CatalogCacheTags
{
    public static string Workspace(Guid workspaceId) => $"catalog:{workspaceId:N}";
}

/// <summary>Card texts within Instagram's generic template limits (title and subtitle: 80 characters).</summary>
public static class CardRenderer
{
    public const int MaxText = 80;
    public const string DefaultButton = "اطلاعات بیشتر";
    public const string OutOfStock = "ناموجود";

    public static string PriceText(Money? min, Money? max) => (min, max) switch
    {
        ({ } a, { } b) when a.Amount == b.Amount => PersianMoney.FormatToman(a),
        ({ } a, _) => "از " + PersianMoney.FormatToman(a),
        _ => string.Empty,
    };

    public static StorefrontCard Render(Product product, CardItem item, Uri? imageUrl)
    {
        var (min, max) = product.PriceRange();
        var subtitle = PriceText(min, max);
        if (!product.InStock)
        {
            subtitle = subtitle.Length == 0 ? OutOfStock : $"{subtitle} · {OutOfStock}";
        }

        Uri? link = product.LinkUrl is { } l && Uri.TryCreate(l, UriKind.Absolute, out var u) ? u : null;
        return new StorefrontCard(
            product.Id, Truncate(product.Title), Truncate(subtitle), imageUrl, link, item.ButtonTitle is { Length: > 0 } b ? b : DefaultButton,
            CatalogPayloads.Product(product.Id));
    }

    private static string Truncate(string text) => text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";
}

/// <summary>
/// <see cref="ICatalogQueries"/>. Card sets are cached per workspace for 30 minutes (image URLs are signed for 3 days) and invalidated
/// by every product or card set change. A miss loads through its own scope: HybridCache may run the factory on another task.
/// </summary>
internal sealed class CatalogQueries(CatalogDbContext db, ITenantContext tenant, HybridCache cache, IServiceScopeFactory scopes) : ICatalogQueries
{
    public static readonly TimeSpan ImageUrlLifetime = TimeSpan.FromDays(3);

    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromMinutes(30), LocalCacheExpiration = TimeSpan.FromMinutes(5) };

    public async Task<StorefrontCardSetContent?> GetCardSetAsync(Guid cardSetId, CancellationToken cancellationToken)
    {
        if (tenant.WorkspaceId is not { } workspaceId)
        {
            return null;
        }

        var cached = await cache.GetOrCreateAsync(
            $"catalog:{workspaceId:N}:cards:{cardSetId:N}",
            (scopes, workspaceId, cardSetId),
            static async (s, token) =>
            {
                await using var scope = s.scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(s.workspaceId);
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var files = scope.ServiceProvider.GetRequiredService<IFileService>();
                return await LoadCardSetAsync(db, files, s.cardSetId, token);
            },
            Entry,
            [CatalogCacheTags.Workspace(workspaceId)],
            cancellationToken);
        return cached.Found ? cached.Content : null;
    }

    public async Task<ProductSummary?> GetProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        return product is null ? null : await SummarizeAsync([product], cancellationToken) is [var summary] ? summary : null;
    }

    public async Task<IReadOnlyList<ProductSummary>> SearchAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var words = PersianNormalizer.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).Take(8).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var query = db.Products.AsNoTracking().Where(p => p.Status == ProductStatuses.Active);
        var any = PredicateAny(words);
        var candidates = await query.Where(any).OrderByDescending(p => p.UpdatedAt).Take(200).ToListAsync(cancellationToken);
        var ranked = candidates
            .Select(p => (Product: p, Score: words.Count(w => p.SearchText.Contains(w, StringComparison.Ordinal)) * 2 + words.Count(w => PersianNormalizer.Normalize(p.Title).Contains(w, StringComparison.Ordinal))))
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Product.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(x => x.Product)
            .ToList();
        return await SummarizeAsync(ranked, cancellationToken);
    }

    internal static async Task<CachedCardSet> LoadCardSetAsync(CatalogDbContext db, IFileService files, Guid cardSetId, CancellationToken ct)
    {
        var set = await db.CardSets.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cardSetId, ct);
        if (set is null)
        {
            return new CachedCardSet(false, null);
        }

        var ids = set.Items.Select(i => i.ProductId).ToList();
        var products = (await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id) && p.Status == ProductStatuses.Active).ToListAsync(ct)).ToDictionary(p => p.Id);
        var images = products.Values.Select(p => p.ImageFileIds).Where(i => i.Count > 0).SelectMany(i => i).ToList();
        var urls = await files.GetCleanUrlsAsync(images, ImageUrlLifetime, ct);
        var cards = set.Items
            .Where(i => products.ContainsKey(i.ProductId))
            .Take(StorefrontCardSet.MaxCards)
            .Select(i =>
            {
                var product = products[i.ProductId];
                var image = product.ImageFileIds.Select(f => urls.GetValueOrDefault(f)).FirstOrDefault(u => u is not null);
                return CardRenderer.Render(product, i, image);
            })
            .ToList();
        return new CachedCardSet(true, new StorefrontCardSetContent(set.Id, set.Name, cards));
    }

    private async Task<IReadOnlyList<ProductSummary>> SummarizeAsync(IReadOnlyList<Product> products, CancellationToken ct)
    {
        var categoryIds = products.Where(p => p.CategoryId is not null).Select(p => p.CategoryId!.Value).Distinct().ToList();
        var categories = categoryIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return [.. products.Select(p => Summarize(p, p.CategoryId is { } c ? categories.GetValueOrDefault(c) : null))];
    }

    internal static ProductSummary Summarize(Product p, string? category)
    {
        var (min, max) = p.PriceRange();
        var variants = p.Variants.Where(v => v.DeletedAt is null).OrderBy(v => v.SortOrder)
            .Select(v => new VariantSummary(v.Id, v.Sku, v.Attributes, p.PriceOf(v) ?? Money.Zero(Currency.Irr), v.InStock, v.Stock))
            .ToList();
        Uri? link = p.LinkUrl is { } l && Uri.TryCreate(l, UriKind.Absolute, out var u) ? u : null;
        return new ProductSummary(p.Id, p.Title, p.Description, category, p.Tags, p.Status, min, max, p.InStock, variants, link, p.Revision, p.UpdatedAt);
    }

    private static System.Linq.Expressions.Expression<Func<Product, bool>> PredicateAny(IReadOnlyList<string> words)
    {
        var p = System.Linq.Expressions.Expression.Parameter(typeof(Product), "p");
        var text = System.Linq.Expressions.Expression.Property(p, nameof(Product.SearchText));
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        System.Linq.Expressions.Expression? body = null;
        foreach (var word in words)
        {
            var call = System.Linq.Expressions.Expression.Call(text, contains, System.Linq.Expressions.Expression.Constant(word));
            body = body is null ? call : System.Linq.Expressions.Expression.OrElse(body, call);
        }

        return System.Linq.Expressions.Expression.Lambda<Func<Product, bool>>(body!, p);
    }
}

/// <summary>Cached result of a card set lookup; <see cref="Found"/> false caches a missing set too.</summary>
public sealed record CachedCardSet(bool Found, StorefrontCardSetContent? Content);

/// <summary>Publishes <see cref="ProductChanged"/> and invalidates the workspace's catalog caches, in the same transaction as the change.</summary>
internal static class CatalogChanges
{
    public static void Product(CatalogDbContext db, ICacheInvalidator invalidator, Product product, string change)
    {
        db.Publish(new ProductChanged(product.WorkspaceId, product.Id, change, product.Revision), product.WorkspaceId.ToString());
        invalidator.Enqueue(db, CatalogCacheTags.Workspace(product.WorkspaceId));
    }

    public static void Cards(CatalogDbContext db, ICacheInvalidator invalidator, Guid workspaceId) =>
        invalidator.Enqueue(db, CatalogCacheTags.Workspace(workspaceId));

    public static ValueTask InvalidateLocalAsync(HybridCache cache, Guid workspaceId, CancellationToken ct) =>
        cache.RemoveByTagAsync(CatalogCacheTags.Workspace(workspaceId), ct);
}
