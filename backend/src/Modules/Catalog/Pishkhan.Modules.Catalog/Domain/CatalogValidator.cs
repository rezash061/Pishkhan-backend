using Pishkhan.Modules.Catalog.Contracts;

namespace Pishkhan.Modules.Catalog.Domain;

/// <summary>Field rules for products and card sets. Errors are (field, message key) pairs.</summary>
public static class CatalogValidator
{
    public const int MaxTitle = 200;
    public const int MaxDescription = 4000;
    public const int MaxTags = 20;
    public const int MaxTag = 40;
    public const int MaxImages = 10;
    public const int MaxVariants = 100;
    public const int MaxSku = 64;
    public const int MaxAttributes = 5;
    public const int MaxAttributeKey = 40;
    public const int MaxAttributeValue = 100;
    public const int MaxStock = 1_000_000;

    /// <summary>10 trillion Rial (one trillion Toman): far above any real price, far below overflow.</summary>
    public const long MaxPrice = 10_000_000_000_000;

    public static IEnumerable<(string Field, string Code)> Validate(ProductDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Title))
        {
            yield return ("title", "validation.required");
        }
        else if (d.Title.Trim().Length > MaxTitle)
        {
            yield return ("title", "validation.too_long");
        }

        if (d.Description is { Length: > MaxDescription })
        {
            yield return ("description", "validation.too_long");
        }

        if (!ProductStatuses.All.Contains(d.Status))
        {
            yield return ("status", "catalog.status_invalid");
        }

        if (d.Tags.Count > MaxTags || d.Tags.Any(t => t.Trim().Length > MaxTag))
        {
            yield return ("tags", "catalog.tags_invalid");
        }

        if (d.Price is < 0 or > MaxPrice)
        {
            yield return ("price", "catalog.price_invalid");
        }

        if (d.ImageFileIds.Count > MaxImages)
        {
            yield return ("imageFileIds", "catalog.too_many_images");
        }

        if (d.LinkUrl is { } link && !string.IsNullOrWhiteSpace(link)
            && (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || link.Length > 500))
        {
            yield return ("linkUrl", "catalog.link_invalid");
        }

        if (d.Variants.Count > MaxVariants)
        {
            yield return ("variants", "catalog.too_many_variants");
            yield break;
        }

        // A product needs a price somewhere: on itself, or on every variant.
        if (d.Status == ProductStatuses.Active && d.Price is null && (d.Variants.Count == 0 || d.Variants.Any(v => v.Price is null)))
        {
            yield return ("price", "catalog.price_required");
        }

        var skus = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in d.Variants)
        {
            if (v.Sku is { } sku && !string.IsNullOrWhiteSpace(sku) && (sku.Trim().Length > MaxSku || !skus.Add(sku.Trim())))
            {
                yield return ("variants.sku", sku.Trim().Length > MaxSku ? "validation.too_long" : "catalog.sku_duplicate");
            }

            if (v.Attributes.Count > MaxAttributes
                || v.Attributes.Any(a => string.IsNullOrWhiteSpace(a.Key) || a.Key.Trim().Length > MaxAttributeKey || string.IsNullOrWhiteSpace(a.Value) || a.Value.Trim().Length > MaxAttributeValue))
            {
                yield return ("variants.attributes", "catalog.attributes_invalid");
            }

            var label = string.Join('|', v.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key.Trim()}={a.Value.Trim()}"));
            if (v.Attributes.Count > 0 && !labels.Add(label))
            {
                yield return ("variants.attributes", "catalog.variant_duplicate");
            }

            if (v.Price is < 0 or > MaxPrice)
            {
                yield return ("variants.price", "catalog.price_invalid");
            }

            if (!StockPolicies.All.Contains(v.StockPolicy))
            {
                yield return ("variants.stockPolicy", "catalog.stock_policy_invalid");
            }
            else if (v.StockPolicy == StockPolicies.Track && v.Stock is < 0 or > MaxStock)
            {
                yield return ("variants.stock", "catalog.stock_invalid");
            }
        }

        if (d.Variants.Count > 1 && d.Variants.Any(v => v.Attributes.Count == 0))
        {
            yield return ("variants.attributes", "catalog.variant_needs_attributes");
        }
    }

    public static IEnumerable<(string Field, string Code)> ValidateCardSet(string? name, IReadOnlyList<CardItem> items)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return ("name", "validation.required");
        }
        else if (name.Trim().Length > 100)
        {
            yield return ("name", "validation.too_long");
        }

        if (items.Count == 0)
        {
            yield return ("items", "validation.required");
        }
        else if (items.Count > StorefrontCardSet.MaxCards)
        {
            yield return ("items", "catalog.too_many_cards");
        }

        if (items.Any(i => i.ButtonTitle is { Length: > 20 }))
        {
            yield return ("items.buttonTitle", "validation.too_long");
        }
    }

    public static IEnumerable<(string Field, string Code)> ValidateCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return ("name", "validation.required");
        }
        else if (name.Trim().Length > 100)
        {
            yield return ("name", "validation.too_long");
        }
    }
}
