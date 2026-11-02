using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Domain;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Services;

/// <summary>A price change worked out against the catalog as it is now: the products it matched, the prices it changes, and a fingerprint of both.</summary>
internal sealed record PricePlan(IReadOnlyList<Product> Products, IReadOnlyList<PriceChangeItem> Items, IReadOnlyList<PriceChangeItem> Invalid, string Fingerprint);

/// <summary>
/// Bulk price changes (backend plan 9.8). Preview and apply share <see cref="PlanAsync"/>, so an apply is exactly the preview it follows;
/// the preview's fingerprint covers every matched product's revision, so an apply after any catalog change in between is refused.
/// </summary>
internal sealed class PriceChanges(CatalogDbContext db, ICacheInvalidator invalidator)
{
    public async Task<PricePlan> PlanAsync(PriceChangeScope scope, PriceRule rule, CancellationToken ct)
    {
        var query = db.Products.Where(p => p.PricingMode == PricingModes.Fixed);
        if (scope.ProductIds is { Count: > 0 } ids)
        {
            query = query.Where(p => ids.Contains(p.Id));
        }

        if (scope.CategoryId is { } category)
        {
            var branch = await CategoryBranchAsync(category, ct);
            query = query.Where(p => p.CategoryId != null && branch.Contains(p.CategoryId.Value));
        }

        if (scope.Tag is { } tag)
        {
            query = query.Where(p => p.Tags.Contains(tag));
        }

        if (scope.Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        // One more than allowed, to tell "too many" from "exactly the limit".
        var products = await query.OrderBy(p => p.Id).Take(PriceChangeBatch.MaxItems + 1).ToListAsync(ct);
        var items = new List<PriceChangeItem>();
        var invalid = new List<PriceChangeItem>();
        foreach (var product in products)
        {
            foreach (var (variantId, old) in OwnPrices(product))
            {
                var next = rule.Apply(old);
                if (next is null)
                {
                    invalid.Add(new PriceChangeItem(product.Id, variantId, old, 0));
                }
                else if (next != old)
                {
                    items.Add(new PriceChangeItem(product.Id, variantId, old, next.Value));
                }
            }
        }

        return new PricePlan(products, items, invalid, Fingerprint(products, items));
    }

    /// <summary>Applies a plan as one batch: prices, a <see cref="ProductChanged"/> per product, <see cref="PricesUpdated"/> and cache invalidation, in one transaction.</summary>
    public PriceChangeBatch Apply(Guid workspaceId, PriceChangeScope scope, PriceRule rule, PricePlan plan, Guid? userId, DateTimeOffset now)
    {
        var products = plan.Products.ToDictionary(p => p.Id);
        foreach (var item in plan.Items)
        {
            products[item.ProductId].SetPrice(item.VariantId, item.NewPrice, now);
        }

        var batch = PriceChangeBatch.Create(workspaceId, scope, rule, plan.Items, userId, now);
        db.PriceChanges.Add(batch);
        Publish(workspaceId, batch.Id, [.. plan.Items.Select(i => products[i.ProductId]).Distinct()], undo: false, now);
        return batch;
    }

    /// <summary>Puts back every price that still has the batch's new value; the count of prices changed since (left alone) is returned.</summary>
    public async Task<int> UndoAsync(PriceChangeBatch batch, Guid? userId, DateTimeOffset now, CancellationToken ct)
    {
        var ids = batch.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = (await db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct)).ToDictionary(p => p.Id);
        var touched = new HashSet<Product>();
        var skipped = 0;
        foreach (var item in batch.Items)
        {
            if (products.TryGetValue(item.ProductId, out var product) && product.OwnPrice(item.VariantId) == item.NewPrice && product.SetPrice(item.VariantId, item.OldPrice, now))
            {
                touched.Add(product);
            }
            else
            {
                skipped++;
            }
        }

        batch.MarkUndone(userId, skipped, now);
        Publish(batch.WorkspaceId, batch.Id, [.. touched], undo: true, now);
        return skipped;
    }

    internal static IEnumerable<(Guid? VariantId, long Price)> OwnPrices(Product product)
    {
        if (product.Price is { } price)
        {
            yield return (null, price);
        }

        foreach (var variant in product.Variants.Where(v => v.DeletedAt is null && v.Price is not null).OrderBy(v => v.SortOrder))
        {
            yield return (variant.Id, variant.Price!.Value);
        }
    }

    private void Publish(Guid workspaceId, Guid batchId, IReadOnlyList<Product> products, bool undo, DateTimeOffset now)
    {
        foreach (var product in products)
        {
            db.Publish(new ProductChanged(workspaceId, product.Id, ProductChangeKinds.PricesChanged, product.Revision), workspaceId.ToString());
        }

        db.Publish(new PricesUpdated(workspaceId, batchId, [.. products.Select(p => p.Id)], undo, now), workspaceId.ToString());
        CatalogChanges.Cards(db, invalidator, workspaceId);
    }

    private async Task<HashSet<Guid>> CategoryBranchAsync(Guid root, CancellationToken ct)
    {
        var all = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(ct);
        var branch = new HashSet<Guid> { root };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var c in all.Where(c => c.ParentId is { } parent && branch.Contains(parent) && !branch.Contains(c.Id)))
            {
                grew |= branch.Add(c.Id);
            }
        }

        return branch;
    }

    private static string Fingerprint(IReadOnlyList<Product> products, IReadOnlyList<PriceChangeItem> items)
    {
        var text = new StringBuilder();
        foreach (var p in products)
        {
            text.Append(p.Id.ToString("N")).Append(':').Append(p.Revision).Append(';');
        }

        foreach (var i in items)
        {
            text.Append(i.ProductId.ToString("N")).Append('/').Append(i.VariantId?.ToString("N")).Append('=').Append(i.NewPrice).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
