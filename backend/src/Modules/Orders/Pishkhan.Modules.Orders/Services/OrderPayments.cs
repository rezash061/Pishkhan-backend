using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Orders.Domain;
using Pishkhan.Modules.Orders.Persistence;
using Pishkhan.Modules.Payments.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Orders.Services;

internal sealed class OrderQueries(OrdersDbContext db) : IOrderQueries
{
    public async Task<OrderForPayment?> GetForPaymentAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken) is { } order ? ForPayment(order) : null;

    public async Task<OrderForPayment?> FindAwaitingPaymentAsync(Guid contactId, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking().Where(o => o.ContactId == contactId && o.Status == OrderStatuses.AwaitingPayment)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(cancellationToken) is { } order ? ForPayment(order) : null;

    public static OrderForPayment ForPayment(Order o) => new(
        o.Id, o.WorkspaceId, o.Number, o.Status, o.TotalMoney,
        [.. o.Items.Select(i => new OrderForPaymentLine(i.Title, i.VariantLabel, i.Quantity, Money.Rial(i.LineTotal)))], o.CreatedAt, o.ContactId, o.ConversationId);
}

/// <summary>
/// Commerce group: a verified payment moves its order from <c>awaiting_payment</c> to <c>paid</c> (which takes stock) when the amount
/// equals the order's total. Any other case (the order was edited, cancelled or already paid) only adds a note to the timeline, so the
/// seller sees money that arrived and decides.
/// </summary>
internal sealed class PaymentConfirmedHandler(OrdersDbContext db, IClock clock) : IdempotentHandler<PaymentConfirmed, OrdersDbContext>(db)
{
    protected override async Task HandleOnceAsync(PaymentConfirmed message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var order = await Db.Orders.FirstOrDefaultAsync(o => o.WorkspaceId == message.WorkspaceId && o.Id == message.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var reference = message.Provider == PaymentProviders.CardToCard
            ? $"کارت به کارت (رسید تأیید شده)، کد پیگیری {message.ReferenceId}"
            : $"پرداخت آنلاین ({message.Provider})، کد پیگیری {message.ReferenceId}";
        var from = order.Status;
        if (order.Status == OrderStatuses.AwaitingPayment && order.Total == message.Amount.Amount && order.MoveTo(OrderStatuses.Paid, now))
        {
            Db.Events.Add(OrderEvent.For(order, OrderEventKinds.StatusChanged, OrderActor.System, now, from, reference));
            Db.Publish(order.Changed(from, now), order.Id.ToString(), order.WorkspaceId);
            return;
        }

        Db.Events.Add(OrderEvent.For(
            order, OrderEventKinds.Note, OrderActor.System, now,
            note: $"{reference}: مبلغ {message.Amount.Amount / 10:N0} تومان دریافت شد ولی سفارش در وضعیت «{order.Status}» با جمع {order.Total / 10:N0} تومان بود؛ وضعیت تغییر نکرد."));
    }
}
