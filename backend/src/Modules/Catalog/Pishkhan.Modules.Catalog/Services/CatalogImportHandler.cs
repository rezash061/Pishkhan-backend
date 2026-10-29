using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Domain;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Services;

/// <summary>
/// Commerce group: applies an Excel import in one transaction. A product is matched by the sheet's product id, then by a variant's SKU;
/// otherwise it is new. Categories are matched by name and created when missing. Groups with an invalid row are skipped whole.
/// </summary>
internal sealed class CatalogImportHandler(
    CatalogDbContext db, IFileService files, IObjectStorage storage, ICacheInvalidator invalidator, IClock clock, ILogger<CatalogImportHandler> logger)
    : IdempotentHandler<CatalogImportRequested, CatalogDbContext>(db)
{
    protected override async Task HandleOnceAsync(CatalogImportRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var import = await Db.Imports.FirstOrDefaultAsync(i => i.Id == message.ImportId, cancellationToken);
        var now = clock.UtcNow;
        if (import is null || !import.Start(now))
        {
            return;
        }

        var file = await files.GetCleanAsync(import.FileId, cancellationToken);
        if (file is null)
        {
            import.Fail("catalog.import_file_not_clean", now);
            return;
        }

        SheetReadResult sheet;
        await using (var stream = await storage.OpenReadAsync(file.Bucket, file.StorageKey, cancellationToken))
        {
            // ClosedXML needs a seekable stream.
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            sheet = CatalogSheet.Read(buffer);
        }

        if (sheet.Error is { } error)
        {
            import.Fail(error, now);
            return;
        }

        var errors = sheet.Errors.ToList();
        var groups = CatalogSheet.Group(sheet.Rows, errors);
        var counts = await ApplyAsync(import.WorkspaceId, groups, errors, now, cancellationToken);
        var rows = sheet.Rows.Count + sheet.Errors.Select(e => e.Row).Distinct().Count();
        import.Complete(rows, counts.Created, counts.Updated, rows - counts.Applied, errors.OrderBy(e => e.Row), now);
        logger.LogInformation("Catalog import {ImportId}: {Created} created, {Updated} updated, {Errors} errors", import.Id, counts.Created, counts.Updated, errors.Count);
    }

    private async Task<(int Created, int Updated, int Applied)> ApplyAsync(
        Guid workspaceId, IReadOnlyList<IReadOnlyList<SheetRow>> groups, List<ImportRowError> errors, DateTimeOffset now, CancellationToken ct)
    {
        var ids = groups.SelectMany(g => g).Where(r => r.ProductId is not null).Select(r => r.ProductId!.Value).Distinct().ToList();
        var skus = groups.SelectMany(g => g).Where(r => r.Sku is not null).Select(r => r.Sku!).Distinct(StringComparer.Ordinal).ToList();
        var owners = await Db.Variants.AsNoTracking().Where(v => v.Sku != null && skus.Contains(v.Sku)).Select(v => new { v.Sku, v.ProductId }).ToListAsync(ct);
        var skuOwner = owners.ToDictionary(o => o.Sku!, o => o.ProductId, StringComparer.Ordinal);
        var productIds = ids.Concat(owners.Select(o => o.ProductId)).Distinct().ToList();
        var products = await Db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var categories = await Db.Categories.ToListAsync(ct);
        var categoryByName = categories.GroupBy(c => PersianNormalizer.Normalize(c.Name)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var claimedSkus = new Dictionary<string, Guid>(StringComparer.Ordinal);

        int created = 0, updated = 0, applied = 0;
        foreach (var group in groups)
        {
            var first = group[0];
            Product? existing = null;
            if (first.ProductId is { } id && !products.TryGetValue(id, out existing))
            {
                errors.Add(new ImportRowError(first.Row, CatalogSheet.Columns.ProductId, "catalog.import_product_not_found"));
                continue;
            }

            existing ??= group.Where(r => r.Sku is not null).Select(r => skuOwner.TryGetValue(r.Sku!, out var owner) ? products.GetValueOrDefault(owner) : null).FirstOrDefault(p => p is not null);
            var targetId = existing?.Id ?? Guid.Empty;
            var skuRow = group.FirstOrDefault(r => r.Sku is not null
                && ((skuOwner.TryGetValue(r.Sku, out var owner) && owner != targetId) || (claimedSkus.TryGetValue(r.Sku, out var claimer) && claimer != targetId)));
            if (skuRow is not null)
            {
                errors.Add(new ImportRowError(skuRow.Row, CatalogSheet.Columns.Sku, "catalog.sku_taken"));
                continue;
            }

            Guid? categoryId = existing?.CategoryId;
            if (first.Category is { } categoryName)
            {
                var key = PersianNormalizer.Normalize(categoryName);
                if (!categoryByName.TryGetValue(key, out var category))
                {
                    if (categoryName.Length > 100)
                    {
                        errors.Add(new ImportRowError(first.Row, CatalogSheet.Columns.Category, "validation.too_long"));
                        continue;
                    }

                    category = Category.Create(workspaceId, categoryName, null, categoryByName.Count, now);
                    Db.Categories.Add(category);
                    categoryByName[key] = category;
                }

                categoryId = category.Id;
            }

            var definition = CatalogSheet.ToDefinition(group, existing, categoryId);
            if (CatalogValidator.Validate(definition).FirstOrDefault() is { Code: not null } invalid)
            {
                errors.Add(new ImportRowError(first.Row, ColumnFor(invalid.Field), invalid.Code));
                continue;
            }

            Product product;
            if (existing is null)
            {
                product = Product.Create(workspaceId, definition, now);
                Db.Products.Add(product);
                products[product.Id] = product;
                created++;
                CatalogChanges.Product(Db, invalidator, product, ProductChangeKinds.Created);
            }
            else
            {
                product = existing;
                product.Update(definition, now);
                updated++;
                CatalogChanges.Product(Db, invalidator, product, ProductChangeKinds.Updated);
            }

            foreach (var sku in group.Where(r => r.Sku is not null).Select(r => r.Sku!))
            {
                claimedSkus[sku] = product.Id;
            }

            applied += group.Count;
        }

        return (created, updated, applied);
    }

    private static string ColumnFor(string field) => field switch
    {
        "title" => CatalogSheet.Columns.Title,
        "description" => CatalogSheet.Columns.Description,
        "tags" => CatalogSheet.Columns.Tags,
        "status" => CatalogSheet.Columns.Status,
        "linkUrl" => CatalogSheet.Columns.Link,
        "price" or "variants.price" => CatalogSheet.Columns.Price,
        "variants.sku" => CatalogSheet.Columns.Sku,
        "variants.stock" => CatalogSheet.Columns.Stock,
        _ => CatalogSheet.Columns.Attributes,
    };
}
