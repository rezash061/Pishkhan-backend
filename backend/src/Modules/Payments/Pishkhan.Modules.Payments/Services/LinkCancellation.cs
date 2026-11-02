using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Payments.Domain;
using Pishkhan.Modules.Payments.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Payments.Services;

/// <summary>
/// An order that leaves <c>awaiting_payment</c> (back to draft for an edit, cancelled, or marked paid by hand) cancels its active links,
/// so the customer cannot pay an old amount or pay twice. A payment already under way at the provider still counts if it is verified.
/// </summary>
internal sealed class OrderStatusChangedHandler(PaymentsDbContext db, IClock clock) : IdempotentHandler<OrderStatusChanged, PaymentsDbContext>(db)
{
    protected override async Task HandleOnceAsync(OrderStatusChanged message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (message.FromStatus != OrderStatuses.AwaitingPayment)
        {
            return;
        }

        var now = clock.UtcNow;
        var links = await Db.Links
            .Where(l => l.WorkspaceId == message.WorkspaceId && l.OrderId == message.OrderId && l.Status == PaymentLinkStatuses.Active)
            .ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            link.Cancel(now);
        }
    }
}
