using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;

namespace Pishkhan.Infrastructure.Messaging;

/// <summary>One partition: <c>start</c> and <c>end</c> offsets, so <c>end - start</c> messages are retained.</summary>
public sealed record KafkaPartitionInfo(int Partition, long Start, long End);

/// <summary>A topic of <see cref="Topics.All"/>. <c>exists</c> is false when the broker does not have it.</summary>
public sealed record KafkaTopicInfo(string Name, bool Exists, int Partitions, long Messages, int RetentionDays, string KeyDescription, bool IsDlq);

/// <summary>A consumer group's committed position on one topic. <c>lag</c> is the sum over partitions (partitions without a commit count from the start).</summary>
public sealed record KafkaGroupTopicLag(string Topic, long Lag, IReadOnlyList<KafkaPartitionLag> Partitions);

public sealed record KafkaPartitionLag(int Partition, long? Committed, long End, long Lag);

/// <summary>A worker group (<see cref="Topics.ConsumerGroups"/>) and its lag per topic; <c>state</c> as Kafka reports it (Stable, Empty, Dead, ...).</summary>
public sealed record KafkaGroupInfo(string Group, string State, int Members, long Lag, IReadOnlyList<KafkaGroupTopicLag> Topics);

public sealed record KafkaOverview(DateTimeOffset At, IReadOnlyList<KafkaTopicInfo> Topics, IReadOnlyList<KafkaGroupInfo> Groups);

/// <summary>
/// Reads topics, offsets and consumer group lag from the brokers for the back office (admin plan 4.10). Read-only; one admin client per
/// call. Results are cached for 15 seconds because a full scan touches every partition.
/// </summary>
public sealed class KafkaInspector(IOptions<KafkaOptions> options, ILogger<KafkaInspector> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KafkaOverview? _cached;

    public async Task<KafkaOverview> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } hit && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            return hit;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } again && DateTimeOffset.UtcNow - again.At < CacheFor)
            {
                return again;
            }

            _cached = await ReadAsync();
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<KafkaOverview> ReadAsync()
    {
        var o = options.Value;
        var config = new AdminClientConfig { BootstrapServers = o.BootstrapServers, ClientId = o.ClientIdPrefix + "-inspector", SocketTimeoutMs = (int)Timeout.TotalMilliseconds };
        KafkaClientConfig.ApplySecurity(config, o);
        using var admin = new AdminClientBuilder(config)
            .SetLogHandler((_, m) => logger.LogDebug("Kafka admin: {Message}", m.Message))
            .Build();

        var metadata = admin.GetMetadata(Timeout);
        var partitionsByTopic = metadata.Topics
            .Where(t => t.Error.Code == ErrorCode.NoError)
            .ToDictionary(t => t.Topic, t => t.Partitions.Select(p => p.PartitionId).OrderBy(p => p).ToList(), StringComparer.Ordinal);
        var known = Topics.All.Where(t => partitionsByTopic.ContainsKey(t.Name)).ToList();
        var partitions = known.SelectMany(t => partitionsByTopic[t.Name].Select(p => new TopicPartition(t.Name, p))).ToList();

        var starts = new Dictionary<TopicPartition, long>();
        var ends = new Dictionary<TopicPartition, long>();
        if (partitions.Count > 0)
        {
            var listOptions = new ListOffsetsOptions { RequestTimeout = Timeout };
            var earliest = await admin.ListOffsetsAsync(partitions.Select(tp => new TopicPartitionOffsetSpec { TopicPartition = tp, OffsetSpec = OffsetSpec.Earliest() }), listOptions);
            var latest = await admin.ListOffsetsAsync(partitions.Select(tp => new TopicPartitionOffsetSpec { TopicPartition = tp, OffsetSpec = OffsetSpec.Latest() }), listOptions);
            foreach (var r in earliest.ResultInfos)
            {
                starts[r.TopicPartitionOffsetError.TopicPartition] = r.TopicPartitionOffsetError.Offset.Value;
            }

            foreach (var r in latest.ResultInfos)
            {
                ends[r.TopicPartitionOffsetError.TopicPartition] = r.TopicPartitionOffsetError.Offset.Value;
            }
        }

        var topics = Topics.All.Select(t =>
        {
            if (!partitionsByTopic.TryGetValue(t.Name, out var ids))
            {
                return new KafkaTopicInfo(t.Name, false, 0, 0, (int)t.Retention.TotalDays, t.KeyDescription, t.Name.EndsWith(".dlq", StringComparison.Ordinal));
            }

            var messages = ids.Sum(p => Math.Max(0, ends.GetValueOrDefault(new TopicPartition(t.Name, p)) - starts.GetValueOrDefault(new TopicPartition(t.Name, p))));
            return new KafkaTopicInfo(t.Name, true, ids.Count, messages, (int)t.Retention.TotalDays, t.KeyDescription, t.Name.EndsWith(".dlq", StringComparison.Ordinal));
        }).ToList();

        var groups = new List<KafkaGroupInfo>();
        var descriptions = new Dictionary<string, ConsumerGroupDescription>(StringComparer.Ordinal);
        try
        {
            var described = await admin.DescribeConsumerGroupsAsync(Topics.ConsumerGroups, new DescribeConsumerGroupsOptions { RequestTimeout = Timeout });
            foreach (var d in described.ConsumerGroupDescriptions)
            {
                descriptions[d.GroupId] = d;
            }
        }
        catch (DescribeConsumerGroupsException ex)
        {
            foreach (var d in ex.Results.ConsumerGroupDescriptions.Where(d => d.Error.Code == ErrorCode.NoError))
            {
                descriptions[d.GroupId] = d;
            }
        }

        foreach (var group in Topics.ConsumerGroups)
        {
            IReadOnlyList<TopicPartitionOffsetError> committed;
            try
            {
                var result = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, null)], new ListConsumerGroupOffsetsOptions { RequestTimeout = Timeout });
                committed = result.FirstOrDefault()?.Partitions ?? [];
            }
            catch (ListConsumerGroupOffsetsException ex)
            {
                logger.LogDebug(ex, "No offsets for consumer group {Group}", group);
                committed = [];
            }

            var byTopic = committed
                .Where(c => partitionsByTopic.ContainsKey(c.Topic))
                .GroupBy(c => c.Topic, StringComparer.Ordinal)
                .Select(g =>
                {
                    var commits = g.ToDictionary(c => c.Partition.Value, c => c.Offset.Value);
                    var rows = partitionsByTopic[g.Key].Select(p =>
                    {
                        var tp = new TopicPartition(g.Key, p);
                        var end = ends.GetValueOrDefault(tp);
                        long? at = commits.TryGetValue(p, out var c) && c >= 0 ? c : null;
                        var lag = Math.Max(0, end - (at ?? starts.GetValueOrDefault(tp)));
                        return new KafkaPartitionLag(p, at, end, lag);
                    }).ToList();
                    return new KafkaGroupTopicLag(g.Key, rows.Sum(r => r.Lag), rows);
                })
                .OrderByDescending(t => t.Lag)
                .ThenBy(t => t.Topic, StringComparer.Ordinal)
                .ToList();

            var description = descriptions.GetValueOrDefault(group);
            groups.Add(new KafkaGroupInfo(group, description?.State.ToString() ?? "Unknown", description?.Members.Count ?? 0, byTopic.Sum(t => t.Lag), byTopic));
        }

        return new KafkaOverview(DateTimeOffset.UtcNow, topics, groups);
    }
}
