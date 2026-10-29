using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Domain;

public sealed class Category : AggregateRoot<Guid>, ITenantOwned, ISoftDeletable
{
    private Category()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid? ParentId { get; private set; }

    public int SortOrder { get; private set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public static Category Create(Guid workspaceId, string name, Guid? parentId, int sortOrder, DateTimeOffset now) =>
        new() { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, Name = name.Trim(), ParentId = parentId, SortOrder = sortOrder, CreatedAt = now, UpdatedAt = now };

    public void Update(string name, Guid? parentId, int sortOrder, DateTimeOffset now)
    {
        Name = name.Trim();
        ParentId = parentId;
        SortOrder = sortOrder;
        UpdatedAt = now;
    }
}

/// <summary>What a product request or an import row group asks for. Prices are Rial.</summary>
public sealed record ProductDefinition(
    string Title,
    string? Description,
    Guid? CategoryId,
    IReadOnlyList<string> Tags,
    string Status,
    long? Price,
    IReadOnlyList<Guid> ImageFileIds,
    string? LinkUrl,
    IReadOnlyList<VariantDefinition> Variants);

/// <summary><see cref="Id"/> keeps an existing variant; <see cref="Price"/> null uses the product's price.</summary>
public sealed record VariantDefinition(Guid? Id, string? Sku, IReadOnlyDictionary<string, string> Attributes, long? Price, int? Stock, string StockPolicy);

public sealed class Product : AggregateRoot<Guid>, ITenantOwned, ISoftDeletable
{
    private Product()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>Title, description and tags through <see cref="PersianNormalizer"/>, for search.</summary>
    public string SearchText { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public Guid? CategoryId { get; private set; }

    public List<string> Tags { get; private set; } = [];

    public string Status { get; private set; } = ProductStatuses.Draft;

    public string PricingMode { get; private set; } = PricingModes.Fixed;

    /// <summary>Rial. Variants without their own price use it.</summary>
    public long? Price { get; private set; }

    /// <summary>Product images in display order. Only clean files are ever shown.</summary>
    public List<Guid> ImageFileIds { get; private set; } = [];

    /// <summary>The product on the seller's own site, if any (a card's link button).</summary>
    public string? LinkUrl { get; private set; }

    public List<ProductVariant> Variants { get; private set; } = [];

    /// <summary>Bumped on every change; part of <see cref="ProductChanged"/>.</summary>
    public int Revision { get; private set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public static Product Create(Guid workspaceId, ProductDefinition definition, DateTimeOffset now)
    {
        var product = new Product { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, CreatedAt = now };
        product.Apply(definition, now);
        return product;
    }

    public void Update(ProductDefinition definition, DateTimeOffset now) => Apply(definition, now);

    public void Delete(DateTimeOffset now)
    {
        DeletedAt = now;
        foreach (var variant in Variants)
        {
            variant.DeletedAt = now;
        }

        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Appends an image (a no-op if it is already there). False when the product already has <see cref="CatalogValidator.MaxImages"/>.</summary>
    public bool AddImage(Guid fileId, DateTimeOffset now)
    {
        if (ImageFileIds.Contains(fileId))
        {
            return true;
        }

        if (ImageFileIds.Count >= CatalogValidator.MaxImages)
        {
            return false;
        }

        ImageFileIds = [.. ImageFileIds, fileId];
        Touch(now);
        return true;
    }

    public bool RemoveImage(Guid fileId, DateTimeOffset now)
    {
        if (!ImageFileIds.Contains(fileId))
        {
            return false;
        }

        ImageFileIds = [.. ImageFileIds.Where(f => f != fileId)];
        Touch(now);
        return true;
    }

    /// <summary>Puts the images in the given order. False unless <paramref name="fileIds"/> holds exactly the product's images.</summary>
    public bool ReorderImages(IReadOnlyList<Guid> fileIds, DateTimeOffset now)
    {
        if (fileIds.Count != ImageFileIds.Count || fileIds.Distinct().Count() != fileIds.Count || !fileIds.All(ImageFileIds.Contains))
        {
            return false;
        }

        ImageFileIds = [.. fileIds];
        Touch(now);
        return true;
    }

    public Money? PriceOf(ProductVariant variant) => (variant.Price ?? Price) is { } rial ? Money.Rial(rial) : null;

    /// <summary>Lowest and highest price over the variants that are for sale (all variants when none is in stock).</summary>
    public (Money? Min, Money? Max) PriceRange()
    {
        var live = Variants.Where(v => v.DeletedAt is null).ToList();
        if (live.Count == 0)
        {
            return Price is { } p ? (Money.Rial(p), Money.Rial(p)) : (null, null);
        }

        var pool = live.Where(v => v.InStock).ToList() is { Count: > 0 } inStock ? inStock : live;
        var prices = pool.Select(PriceOf).Where(m => m is not null).Select(m => m!.Value.Amount).ToList();
        return prices.Count == 0 ? (null, null) : (Money.Rial(prices.Min()), Money.Rial(prices.Max()));
    }

    /// <summary>A product without variants is in stock; with variants, when any variant is.</summary>
    public bool InStock => Variants.Where(v => v.DeletedAt is null).ToList() is not { Count: > 0 } live || live.Any(v => v.InStock);

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Revision++;
    }

    private void Apply(ProductDefinition d, DateTimeOffset now)
    {
        Title = d.Title.Trim();
        Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim();
        CategoryId = d.CategoryId;
        Tags = [.. d.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)];
        Status = d.Status;
        Price = d.Price;
        ImageFileIds = [.. d.ImageFileIds.Distinct()];
        LinkUrl = string.IsNullOrWhiteSpace(d.LinkUrl) ? null : d.LinkUrl.Trim();
        SearchText = PersianNormalizer.Normalize(string.Join(' ', [Title, Description ?? string.Empty, .. Tags]));

        // Variants are matched by id, then by SKU, then by the same attributes; the rest are new. Variants left out are removed.
        var kept = new List<ProductVariant>();
        var order = 0;
        foreach (var vd in d.Variants)
        {
            var open = Variants.Where(v => v.DeletedAt is null && !kept.Contains(v)).ToList();
            var sku = string.IsNullOrWhiteSpace(vd.Sku) ? null : vd.Sku.Trim();
            var existing = open.FirstOrDefault(v => vd.Id is { } id && v.Id == id)
                ?? open.FirstOrDefault(v => sku is not null && v.Sku == sku)
                ?? open.FirstOrDefault(v => v.Sku is null && ProductVariant.SameAttributes(v.Attributes, vd.Attributes));
            if (existing is null)
            {
                existing = ProductVariant.Create(WorkspaceId, Id, now);
                Variants.Add(existing);
            }

            existing.Apply(vd, order++, now);
            kept.Add(existing);
        }

        foreach (var removed in Variants.Where(v => v.DeletedAt is null && !kept.Contains(v)))
        {
            removed.DeletedAt = now;
        }

        UpdatedAt = now;
        Revision++;
    }
}

public sealed class ProductVariant : Entity<Guid>, ITenantOwned, ISoftDeletable
{
    private ProductVariant()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>Unique among the workspace's live variants.</summary>
    public string? Sku { get; private set; }

    /// <summary>For example <c>{"رنگ": "قرمز", "سایز": "L"}</c>.</summary>
    public Dictionary<string, string> Attributes { get; private set; } = [];

    /// <summary>Rial; null uses the product's price.</summary>
    public long? Price { get; private set; }

    public int? Stock { get; private set; }

    public string StockPolicy { get; private set; } = StockPolicies.Track;

    public int SortOrder { get; private set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public bool InStock => StockPolicy == StockPolicies.Unlimited || Stock is > 0;

    internal static ProductVariant Create(Guid workspaceId, Guid productId, DateTimeOffset now) =>
        new() { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, ProductId = productId, CreatedAt = now };

    internal void Apply(VariantDefinition d, int order, DateTimeOffset now)
    {
        Sku = string.IsNullOrWhiteSpace(d.Sku) ? null : d.Sku.Trim();
        Attributes = d.Attributes.ToDictionary(a => a.Key.Trim(), a => a.Value.Trim(), StringComparer.Ordinal);
        Price = d.Price;
        StockPolicy = d.StockPolicy;
        Stock = d.StockPolicy == StockPolicies.Unlimited ? null : d.Stock ?? 0;
        SortOrder = order;
        UpdatedAt = now;
    }

    internal static bool SameAttributes(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(x => b.Any(y => y.Key.Trim() == x.Key && y.Value.Trim() == x.Value));

    /// <summary>"رنگ: قرمز، سایز: L".</summary>
    public string Label => string.Join("، ", Attributes.Select(a => $"{a.Key}: {a.Value}"));
}

/// <summary>An item of a card set: a product and, optionally, the text of its button.</summary>
public sealed record CardItem(Guid ProductId, string? ButtonTitle = null);

/// <summary>A named carousel of products that rules and flows send in DMs.</summary>
public sealed class StorefrontCardSet : AggregateRoot<Guid>, ITenantOwned, ISoftDeletable
{
    public const int MaxCards = 10;

    private StorefrontCardSet()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public List<CardItem> Items { get; private set; } = [];

    public DateTimeOffset? DeletedAt { get; set; }

    public static StorefrontCardSet Create(Guid workspaceId, string name, IEnumerable<CardItem> items, DateTimeOffset now)
    {
        var set = new StorefrontCardSet { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, CreatedAt = now };
        set.Update(name, items, now);
        return set;
    }

    public void Update(string name, IEnumerable<CardItem> items, DateTimeOffset now)
    {
        Name = name.Trim();
        Items = [.. items.DistinctBy(i => i.ProductId)];
        UpdatedAt = now;
    }
}
