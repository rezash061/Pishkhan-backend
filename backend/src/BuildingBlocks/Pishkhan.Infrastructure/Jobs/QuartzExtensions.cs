using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pishkhan.Infrastructure.Persistence;
using Quartz;

namespace Pishkhan.Infrastructure.Jobs;

/// <summary>A recurring job owned by a module: (key, cron) plus the job type.</summary>
public sealed record RecurringJob(Type JobType, string Name, string Cron);

public static class QuartzExtensions
{
    /// <summary>
    /// Quartz.NET clustered on PostgreSQL (tables <c>platform.qrtz_*</c>, created by the Migrator), so each recurring job runs on
    /// exactly one replica. Use <paramref name="inMemory"/> only in tests.
    /// </summary>
    public static IServiceCollection AddPishkhanJobs(this IServiceCollection services, IConfiguration configuration, IEnumerable<RecurringJob> jobs, bool inMemory = false)
    {
        services.AddQuartz(q =>
        {
            q.SchedulerName = "pishkhan";
            q.SchedulerId = "AUTO";
            q.UseDefaultThreadPool(tp => tp.MaxConcurrency = 8);
            if (!inMemory)
            {
                q.UsePersistentStore(store =>
                {
                    store.UseProperties = true;
                    store.PerformSchemaValidation = false;
                    store.UsePostgres(pg =>
                    {
                        pg.ConnectionString = configuration.GetConnectionString(PersistenceExtensions.ConnectionStringName)!;
                        pg.TablePrefix = "platform.qrtz_";
                    });
                    store.UseSystemTextJsonSerializer();
                    store.UseClustering(c =>
                    {
                        c.CheckinInterval = TimeSpan.FromSeconds(10);
                        c.CheckinMisfireThreshold = TimeSpan.FromSeconds(30);
                    });
                });
            }

            foreach (var job in jobs)
            {
                var key = new JobKey(job.Name);
                q.AddJob(job.JobType, key, j => j.StoreDurably());
                q.AddTrigger(t => t.ForJob(key).WithIdentity(job.Name + ".trigger").WithCronSchedule(job.Cron, c => c.InTimeZone(TimeZoneInfo.Utc).WithMisfireHandlingInstructionDoNothing()));
            }
        });
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        return services;
    }

    public static readonly IReadOnlyList<RecurringJob> PlatformJobs =
    [
        new(typeof(OutboxCleanupJob), "platform.outbox-cleanup", "0 7 * * * ?"),
        new(typeof(PartitionMaintenanceJob), "platform.partition-maintenance", "0 30 0 * * ?"),
    ];
}

/// <summary>Deletes relayed outbox rows older than 3 days and inbox rows older than 14 days (backend plan 6.4).</summary>
[DisallowConcurrentExecution]
public sealed class OutboxCleanupJob(NpgsqlDataSource dataSource) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        await using var connection = await dataSource.OpenConnectionAsync(context.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM platform.outbox_messages WHERE published_at < now() - interval '3 days'", cancellationToken: context.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM platform.processed_messages WHERE processed_at < now() - interval '14 days'", cancellationToken: context.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM platform.timers WHERE status <> 'Pending' AND updated_at < now() - interval '30 days'", cancellationToken: context.CancellationToken));
    }
}

/// <summary>Creates monthly partitions three months ahead for every partitioned table.</summary>
[DisallowConcurrentExecution]
public sealed class PartitionMaintenanceJob(NpgsqlDataSource dataSource) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        await using var connection = await dataSource.OpenConnectionAsync(context.CancellationToken);
        foreach (var table in Partitioning.PartitionedTables)
        {
            await Partitioning.EnsureMonthlyPartitionsAsync(connection, table, DateTimeOffset.UtcNow, 3, context.CancellationToken);
        }
    }
}
