namespace Pishkhan.Modules.Audit.Domain;

/// <summary>
/// A stored audit entry. Append-only: rows are inserted by the sink and never updated. Not tenant-filtered, because staff read
/// across workspaces and platform entries have no workspace; queries filter by <see cref="WorkspaceId"/> explicitly.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; init; }

    /// <summary>When the action happened (the partition key).</summary>
    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset RecordedAt { get; init; }

    public string ActorType { get; init; } = string.Empty;

    public Guid? ActorId { get; init; }

    public string ActorName { get; init; } = string.Empty;

    public Guid? OnBehalfOf { get; init; }

    public Guid? WorkspaceId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public string? EntityId { get; init; }

    public string? Reason { get; init; }

    public string? Ip { get; init; }

    public string? BeforeJson { get; init; }

    public string? AfterJson { get; init; }
}
