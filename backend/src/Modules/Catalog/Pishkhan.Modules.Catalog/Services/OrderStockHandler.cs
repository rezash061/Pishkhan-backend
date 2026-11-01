using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Services;

/// <summary>
/// Commerce group: an order entering a stocked status (paid) takes its counted stock; one leaving them for cancelled, refunded or
/// returned puts it back. Moves between stocked statuses (paid to preparing to shipped ...) change nothing. Deleted products are skipped.
/// </summary>
internal sealed class OrderStockHandler(CatalogDbContext db, ICacheInvalidator invalidator, IClock clock) : IdempotentHandler<OrderStatusChanged, CatalogDbContext>(db)
{
    public static int Direction(string? from, string to)
    {
        var was = from is not null && OrderStatuses.Stocked.Contains(from);
        var isNow = OrderStatuses.Stocked.Contains(to);
        return (was, isNow) switch
        {
            (false, true) => -1,
            (true, false) when to is OrderStatuses.Cancelled or OrderStatuses.Refunded or OrderStatuses.Returned => 1,
            _ => 0,
        };
    }

    protected override async Task HandleOnceAsync(OrderStatusChanged message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var direction = Direction(message.FromStatus, message.ToStatus);
        var lines = message.Lines.Where(l => l.ProductId is not null && l.VariantId is not null).ToList();
        if (direction == 0 || lines.Count == 0)
        {
            return;
        }

        var deltas = lines.GroupBy(l => l.ProductId!.Value).ToDictionary(
            g => g.Key, g => (IReadOnlyDictionary<Guid, int>)g.GroupBy(l => l.VariantId!.Value).ToDictionary(v => v.Key, v => direction * v.Sum(l => l.Quantity)));
        var ids = deltas.Keys.ToList();
        var products = await Db.Products.Where(p => p.WorkspaceId == message.WorkspaceId && ids.Contains(p.Id)).ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        foreach (var product in products)
        {
            if (product.AdjustStock(deltas[product.Id], now))
            {
                CatalogChanges.Product(Db, invalidator, product, ProductChangeKinds.Updated);
            }
        }
    }
}
