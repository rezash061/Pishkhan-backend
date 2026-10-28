using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Pishkhan.Infrastructure.Antivirus;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Storage;
using StackExchange.Redis;

namespace Pishkhan.Infrastructure.Health;

public static class HealthTags
{
    public const string Ready = "ready";
}

public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable", ex);
        }
    }
}

public sealed class RedisHealthCheck(IServiceProvider services, string instance) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var redis = services.GetRequiredKeyedService<IConnectionMultiplexer>(instance);
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"{latency.TotalMilliseconds:0} ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Redis {instance} unreachable", ex);
        }
    }
}

public sealed class KafkaHealthCheck(IOptions<KafkaOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers, SocketTimeoutMs = 3000 };
            KafkaClientConfig.ApplySecurity(config, options.Value);
            using var admin = new AdminClientBuilder(config).Build();
            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(3));
            return Task.FromResult(metadata.Brokers.Count > 0 ? HealthCheckResult.Healthy($"{metadata.Brokers.Count} broker(s)") : HealthCheckResult.Unhealthy("No Kafka brokers"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka unreachable", ex));
        }
    }
}

public sealed class StorageHealthCheck(IObjectStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await storage.PingAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Object storage unreachable");
}

/// <summary>ClamAV down is <c>Degraded</c>, not unhealthy: uploads keep working and wait in <c>pending_scan</c> (backend plan 8.2).</summary>
public sealed class ClamAvHealthCheck(IAntivirusScanner scanner) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!await scanner.PingAsync(cancellationToken))
        {
            return HealthCheckResult.Degraded("ClamAV unreachable; files stay pending_scan");
        }

        var version = await scanner.GetVersionAsync(cancellationToken);
        return HealthCheckResult.Healthy(version);
    }
}

public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddPostgresCheck(this IHealthChecksBuilder builder) => builder.AddCheck<PostgresHealthCheck>("postgres", tags: [HealthTags.Ready]);

    public static IHealthChecksBuilder AddRedisChecks(this IHealthChecksBuilder builder) => builder
        .AddTypeActivatedCheck<RedisHealthCheck>("redis-cache", HealthStatus.Degraded, [HealthTags.Ready], RedisInstances.Cache)
        .AddTypeActivatedCheck<RedisHealthCheck>("redis-state", HealthStatus.Unhealthy, [HealthTags.Ready], RedisInstances.State);

    public static IHealthChecksBuilder AddKafkaCheck(this IHealthChecksBuilder builder) => builder.AddCheck<KafkaHealthCheck>("kafka", tags: [HealthTags.Ready]);

    public static IHealthChecksBuilder AddStorageCheck(this IHealthChecksBuilder builder) => builder.AddCheck<StorageHealthCheck>("storage", tags: [HealthTags.Ready]);

    public static IHealthChecksBuilder AddClamAvCheck(this IHealthChecksBuilder builder) => builder.AddCheck<ClamAvHealthCheck>("clamav", HealthStatus.Degraded, [HealthTags.Ready]);
}
