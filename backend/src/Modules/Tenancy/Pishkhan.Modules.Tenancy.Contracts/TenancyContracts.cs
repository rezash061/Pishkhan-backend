using Pishkhan.Contracts;

namespace Pishkhan.Modules.Tenancy.Contracts;

[IntegrationEvent(Topics.WorkspaceChanged)]
public sealed record WorkspaceChanged(Guid WorkspaceId, string Change) : IIntegrationEvent;

/// <summary>
/// The grace period is over: every module deletes what the workspace owned (rows, files). Handlers must be safe to run twice.
/// The audit log is kept.
/// </summary>
[IntegrationEvent(Topics.WorkspaceDeleted)]
public sealed record WorkspaceDeleted(Guid WorkspaceId, DateTimeOffset DeletionRequestedAt) : IIntegrationEvent;

[IntegrationEvent(Topics.MembershipChanged)]
public sealed record MembershipChanged(Guid WorkspaceId, Guid UserId, string? Role, bool Removed) : IIntegrationEvent;

/// <summary>In-process queries other modules may use.</summary>
public interface IMembershipQueries
{
    /// <summary>Ids of the workspaces the user belongs to, sorted.</summary>
    Task<IReadOnlyList<Guid>> GetWorkspaceIdsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The workspace owner's user id, or null for an unknown workspace.</summary>
    Task<Guid?> GetOwnerIdAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>Ids of the workspace's members who have <paramref name="permission"/>.</summary>
    Task<IReadOnlyList<Guid>> GetMembersWithPermissionAsync(Guid workspaceId, string permission, CancellationToken cancellationToken);

    /// <summary>Seats in use for the plan's <c>seats</c> limit: members plus pending, unexpired invitations.</summary>
    Task<int> CountSeatsAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>Back office: the user's workspaces with their role, oldest membership first.</summary>
    Task<IReadOnlyList<UserMembership>> GetMembershipsOfUserAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary><c>WorkspaceStatus</c>: active, suspended, pending_deletion, deleted.</summary>
public sealed record UserMembership(Guid WorkspaceId, string WorkspaceName, string WorkspaceStatus, string Role, DateTimeOffset JoinedAt);

public interface IWorkspaceQueries
{
    /// <summary>True when the workspace is suspended or being deleted (cached; cleared on workspace changes). Unknown workspaces count as suspended.</summary>
    Task<bool> IsSuspendedAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>The workspace's business hours (cached; cleared on workspace changes). Unknown workspaces get <see cref="BusinessHours.Default"/>.</summary>
    Task<BusinessHours> GetBusinessHoursAsync(Guid workspaceId, CancellationToken cancellationToken);
}
