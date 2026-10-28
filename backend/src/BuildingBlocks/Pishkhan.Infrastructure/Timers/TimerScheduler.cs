using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Telemetry;
using Pishkhan.SharedKernel;

namespace Pishkhan.Infrastructure.Timers;

/// <summary>Per-entity delays stored in <c>platform.timers</c> (backend plan 7.2). Writes join the caller's DbContext transaction.</summary>
public static class TimerScheduler
{
    public static TimerEntry Schedule<TPayload>(ModuleDbContext db, string kind, DateTimeOffset dueAt, TPayload payload, string? dedupeKey = null, Guid? workspaceId = null)
    {
        var timer = new TimerEntry
        {
            Id = Uuid.NewV7(),
            WorkspaceId = workspaceId ?? db.Tenant.WorkspaceId,
            DueAt = dueAt,
            Kind = kind,
            Payload = JsonSerializer.SerializeToDocument(payload, EnvelopeJson.Options),
            Status = TimerStatus.Pending,
            DedupeKey = dedupeKey,
        };
        db.Timers.Add(timer);
        return timer;
    }

    /// <summary>Cancels the pending timer with this dedupe key, for example when the customer replies or buys.</summary>
    public static Task<int> CancelAsync(ModuleDbContext db, string dedupeKey, CancellationToken cancellationToken = default) =>
        db.Timers
            .Where(t => t.DedupeKey == dedupeKey && t.Status == TimerStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TimerStatus.Cancelled).SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
}

/// <summary>
/// Fires due timers: locks a batch with <c>FOR UPDATE SKIP LOCKED</c> (safe with many replicas), writes a <see cref="TimerFired"/>
/// outbox row for each and marks them fired, in one transaction. Handlers re-check their conditions at fire time.
/// </summary>
public sealed class TimerDispatcher(NpgsqlDataSource dataSource, ILogger<TimerDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var fired = await DispatchDueAsync(500, stoppingToken);
                if (fired == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Timer dispatch failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public async Task<int> DispatchDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var due = (await connection.QueryAsync<DueTimer>(new CommandDefinition(
            """
            SELECT id, workspace_id AS workspaceid, kind, payload::text AS payload, due_at AS dueatutc
            FROM platform.timers
            WHERE status = 'Pending' AND due_at <= now()
            ORDER BY due_at
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """,
            new { batchSize },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        foreach (var timer in due)
        {
            var evt = new TimerFired(timer.Id, timer.Kind, timer.WorkspaceId, JsonDocument.Parse(timer.Payload).RootElement.Clone(), timer.DueAt);
            var envelope = new EventEnvelope
            {
                MessageId = Uuid.NewV7(),
                Type = Topics.TypeName(typeof(TimerFired)),
                OccurredAt = DateTimeOffset.UtcNow,
                WorkspaceId = timer.WorkspaceId,
                CorrelationId = timer.Id,
                Payload = EnvelopeJson.SerializePayload(evt),
            };
            var key = timer.Id.ToString();
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO platform.outbox_messages (id, topic, key, type, envelope, headers, occurred_at, shard, attempts)
                VALUES (@id, @topic, @key, @type, @envelope::jsonb, @headers::jsonb, now(), @shard, 0)
                """,
                new
                {
                    id = envelope.MessageId,
                    topic = Topics.TimerFired,
                    key,
                    type = envelope.Type,
                    envelope = JsonSerializer.Serialize(envelope, EnvelopeJson.Options),
                    headers = JsonSerializer.Serialize(Messaging.KafkaProducer.HeadersFor(envelope)),
                    shard = OutboxMessage.ShardOf(key),
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        if (due.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE platform.timers SET status = 'Fired', attempts = attempts + 1, updated_at = now() WHERE id = ANY(@ids)",
                new { ids = due.Select(t => t.Id).ToArray() },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        PishkhanTelemetry.TimersFired.Add(due.Count);
        return due.Count;
    }

    /// <summary>Dapper row. Npgsql reads timestamptz as a UTC <see cref="DateTime"/>, hence the conversion.</summary>
    private sealed class DueTimer
    {
        public Guid Id { get; init; }

        public Guid? WorkspaceId { get; init; }

        public string Kind { get; init; } = string.Empty;

        public string Payload { get; init; } = "{}";

        public DateTime DueAtUtc { get; init; }

        public DateTimeOffset DueAt => new(DateTime.SpecifyKind(DueAtUtc, DateTimeKind.Utc));
    }
}
