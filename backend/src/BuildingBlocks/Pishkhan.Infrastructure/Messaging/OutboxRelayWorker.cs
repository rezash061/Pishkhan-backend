using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Telemetry;

namespace Pishkhan.Infrastructure.Messaging;

public sealed class OutboxRelayOptions
{
    public const string Section = "Outbox";

    public int BatchSize { get; set; } = 500;

    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan LockRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Relays <c>platform.outbox_messages</c> to Kafka (backend plan 6.4). One loop per shard; a PostgreSQL session advisory lock
/// makes each shard single-owner across replicas, so per-key order is kept. Within a batch, different keys are produced
/// concurrently and each key's messages sequentially; a failed key stops at its first failure so later events never overtake it.
/// </summary>
public sealed class OutboxRelayWorker(NpgsqlDataSource dataSource, IKafkaProducer producer, IOptions<OutboxRelayOptions> options, ILogger<OutboxRelayWorker> logger) : BackgroundService
{
    private const long LockNamespace = 0x50_69_73_68; // "Pish"

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, OutboxMessage.ShardCount).Select(shard => RunShardAsync((short)shard, stoppingToken)));

    private async Task RunShardAsync(short shard, CancellationToken stoppingToken)
    {
        var o = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var lockConnection = await dataSource.OpenConnectionAsync(stoppingToken);
                var acquired = await lockConnection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@ns::int, @shard::int)", new { ns = (int)LockNamespace, shard = (int)shard });
                if (!acquired)
                {
                    await Task.Delay(o.LockRetryDelay, stoppingToken);
                    continue;
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    var relayed = await RelayBatchAsync(shard, o.BatchSize, stoppingToken);
                    if (relayed == 0)
                    {
                        await Task.Delay(o.IdleDelay, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox relay for shard {Shard} failed; restarting", shard);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    /// <summary>Relays one batch of a shard. Public for tests.</summary>
    public async Task<int> RelayBatchAsync(short shard, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            """
            SELECT id, topic, key, envelope::text AS envelope, headers::text AS headers
            FROM platform.outbox_messages
            WHERE shard = @shard AND published_at IS NULL
            ORDER BY id
            LIMIT @batchSize
            """,
            new { shard, batchSize },
            cancellationToken: cancellationToken))).ToList();

        if (rows.Count == 0)
        {
            return 0;
        }

        var published = new List<Guid>(rows.Count);
        var failed = new List<(Guid Id, string Error)>();
        var gate = new Lock();

        await Task.WhenAll(rows.GroupBy(r => r.Key).Select(async group =>
        {
            foreach (var row in group)
            {
                try
                {
                    var headers = row.Headers is null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers);
                    await producer.ProduceAsync(row.Topic, row.Key, System.Text.Encoding.UTF8.GetBytes(row.Envelope), headers, cancellationToken);
                    lock (gate)
                    {
                        published.Add(row.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (gate)
                    {
                        failed.Add((row.Id, ex.Message));
                    }

                    break; // keep per-key order: later events of this key wait for the next batch
                }
            }
        }));

        if (published.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE platform.outbox_messages SET published_at = now() WHERE id = ANY(@ids)", new { ids = published.ToArray() }, cancellationToken: CancellationToken.None));
            PishkhanTelemetry.OutboxRelayed.Add(published.Count);
        }

        foreach (var (id, error) in failed)
        {
            logger.LogWarning("Outbox message {Id} could not be produced: {Error}", id, error);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE platform.outbox_messages SET attempts = attempts + 1, last_error = @error WHERE id = @id", new { id, error }, cancellationToken: CancellationToken.None));
        }

        Activity.Current?.SetTag("outbox.relayed", published.Count);
        return published.Count;
    }

    private sealed record OutboxRow(Guid Id, string Topic, string Key, string Envelope, string? Headers);
}
