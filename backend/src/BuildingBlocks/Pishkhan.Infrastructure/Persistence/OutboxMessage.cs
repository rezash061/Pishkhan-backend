using System.Text.Json;

namespace Pishkhan.Infrastructure.Persistence;

/// <summary>A row of <c>platform.outbox_messages</c>. Written in the same transaction as the business change; relayed to Kafka.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public required string Topic { get; set; }

    public required string Key { get; set; }

    public required string Type { get; set; }

    /// <summary>The full <see cref="Contracts.EventEnvelope"/> as JSON.</summary>
    public required JsonDocument Envelope { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];

    public DateTimeOffset OccurredAt { get; set; }

    public short Shard { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public const int ShardCount = 16;

    /// <summary>Stable shard of a key, so all events with one key go through one relay in order.</summary>
    public static short ShardOf(string key)
    {
        // FNV-1a: stable across processes and .NET versions, unlike string.GetHashCode.
        var hash = 2166136261u;
        foreach (var c in key)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return (short)(hash % ShardCount);
    }
}

/// <summary>A row of <c>platform.processed_messages</c>: the inbox that makes consumers idempotent.</summary>
public sealed class ProcessedMessage
{
    public required string Consumer { get; set; }

    public Guid MessageId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}

public enum TimerStatus
{
    Pending,
    Fired,
    Cancelled,
}

/// <summary>A row of <c>platform.timers</c>: a per-entity delay (follow-ups, flow delays, expiries).</summary>
public sealed class TimerEntry
{
    public Guid Id { get; set; }

    public Guid? WorkspaceId { get; set; }

    public DateTimeOffset DueAt { get; set; }

    public required string Kind { get; set; }

    public required JsonDocument Payload { get; set; }

    public TimerStatus Status { get; set; }

    public string? DedupeKey { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A row of <c>platform.dead_letters</c>: the back office's copy of a message a consumer gave up on (also in the group's Kafka DLQ topic).
/// <see cref="Status"/>: open, replayed or discarded.
/// </summary>
public sealed class DeadLetter
{
    public const string Open = "open";
    public const string Replayed = "replayed";
    public const string Discarded = "discarded";

    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public required string Topic { get; set; }

    public required string ConsumerGroup { get; set; }

    /// <summary>Full type name of the handler that failed.</summary>
    public required string Consumer { get; set; }

    public required string Key { get; set; }

    public required string Type { get; set; }

    public Guid? WorkspaceId { get; set; }

    /// <summary>The envelope as it went to the DLQ, with its <c>deadLetter</c> block.</summary>
    public required JsonDocument Envelope { get; set; }

    public required string ExceptionType { get; set; }

    public required string ExceptionMessage { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset FailedAt { get; set; }

    public string Status { get; set; } = Open;

    public DateTimeOffset? ResolvedAt { get; set; }

    public Guid? ResolvedBy { get; set; }

    public string? ResolutionReason { get; set; }

    /// <summary>The message id the replay was published with.</summary>
    public Guid? ReplayMessageId { get; set; }
}
