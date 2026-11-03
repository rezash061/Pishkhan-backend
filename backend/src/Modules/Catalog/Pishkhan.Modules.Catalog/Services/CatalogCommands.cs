using Microsoft.Extensions.Caching.Hybrid;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Domain;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Services;

internal sealed class CatalogCommands(CatalogDbContext db, ITenantContext tenant, ICacheInvalidator invalidator, HybridCache cache, IClock clock) : ICatalogCommands
{
    public async Task<(Guid? ProductId, IReadOnlyList<string> Errors)> CreateProductAsync(NewProduct product, CancellationToken cancellationToken)
    {
        if (tenant.WorkspaceId is not { } workspaceId)
        {
            throw new InvalidOperationException("No workspace in context.");
        }

        var status = product.Activate && product.Price is > 0 ? ProductStatuses.Active : ProductStatuses.Draft;
        var definition = new ProductDefinition(product.Title.Trim(), product.Description, null, product.Tags, status, product.Price, [], product.LinkUrl, []);
        var errors = CatalogValidator.Validate(definition).Select(e => $"{e.Field}: {e.Code}").ToList();
        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var created = Product.Create(workspaceId, definition, clock.UtcNow);
        db.Products.Add(created);
        CatalogChanges.Product(db, invalidator, created, ProductChangeKinds.Created);
        await db.SaveChangesAsync(cancellationToken);
        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, cancellationToken);
        return (created.Id, []);
    }
}
