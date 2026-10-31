using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;

namespace Pishkhan.Infrastructure.Messaging;

/// <summary>Handles one integration event type. Registered through <see cref="MessagingBuilder.AddConsumer{TEvent, THandler}"/>.</summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Base for handlers that change a module's database: records the message in the inbox, runs the handler and writes outbox rows,
/// all in one transaction. A redelivered message is skipped (backend plan 6.4).
/// </summary>
public abstract class IdempotentHandler<TEvent, TDbContext>(TDbContext db) : IIntegrationEventHandler<TEvent>
    where TEvent : IIntegrationEvent
    where TDbContext : ModuleDbContext
{
    protected TDbContext Db { get; } = db;

    /// <summary>Inbox key. Defaults to the handler type, so two handlers of one event are independent.</summary>
    protected virtual string ConsumerName => GetType().FullName!;

    public async Task HandleAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var strategy = Db.Database.CreateExecutionStrategy();
        var committed = await strategy.ExecuteAsync(async ct =>
        {
            Db.ChangeTracker.Clear();
            await using var transaction = await Db.Database.BeginTransactionAsync(ct);
            var inserted = await Db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO platform.processed_messages (consumer, message_id, processed_at) VALUES ({ConsumerName}, {envelope.MessageId}, now()) ON CONFLICT DO NOTHING",
                ct);
            if (inserted == 0)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            await HandleOnceAsync(message, envelope, ct);
            await Db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);

        if (committed)
        {
            await AfterCommitAsync(message, envelope, cancellationToken);
        }
    }

    /// <summary>Runs at most once per message. Do not call SaveChanges; the base class saves and commits.</summary>
    protected abstract Task HandleOnceAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Side effects outside the database (an SMS), after the commit. At most once: a crash between the commit and this call skips it,
    /// and a redelivered message does not run it again.
    /// </summary>
    protected virtual Task AfterCommitAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
