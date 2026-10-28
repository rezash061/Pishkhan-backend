using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;

namespace Pishkhan.Infrastructure.Messaging;

/// <summary>Creates the topics of <see cref="Topics.All"/> with their partition counts and retention (backend plan 6.2).</summary>
public sealed class TopicBootstrapper(IOptions<KafkaOptions> options, ILogger<TopicBootstrapper> logger)
{
    public async Task EnsureTopicsAsync(CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var config = new AdminClientConfig { BootstrapServers = o.BootstrapServers };
        KafkaClientConfig.ApplySecurity(config, o);
        using var admin = new AdminClientBuilder(config).Build();

        var existing = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
        var specs = Topics.All
            .Where(t => !existing.Contains(t.Name))
            .Select(t => new TopicSpecification
            {
                Name = t.Name,
                NumPartitions = o.PartitionsOverride ?? t.Partitions,
                ReplicationFactor = o.ReplicationFactor,
                Configs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["retention.ms"] = ((long)t.Retention.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                    ["cleanup.policy"] = "delete",
                    ["min.insync.replicas"] = o.ReplicationFactor >= 3 ? "2" : "1",
                },
            })
            .ToList();

        if (specs.Count == 0)
        {
            return;
        }

        try
        {
            await admin.CreateTopicsAsync(specs);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // Another process created them concurrently.
        }

        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Created {Count} Kafka topics", specs.Count);
    }
}

internal sealed class TopicBootstrapHostedService(TopicBootstrapper bootstrapper) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => bootstrapper.EnsureTopicsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
