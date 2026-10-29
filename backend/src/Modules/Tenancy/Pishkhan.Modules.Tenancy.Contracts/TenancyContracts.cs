using Pishkhan.Contracts;

namespace Pishkhan.Modules.Tenancy.Contracts;

[IntegrationEvent(Topics.WorkspaceChanged)]
public sealed record WorkspaceChanged(Guid WorkspaceId, string Change) : IIntegrationEvent;

[IntegrationEvent(Topics.MembershipChanged)]
public sealed record MembershipChanged(Guid WorkspaceId, Guid UserId, string? Role, bool Removed) : IIntegrationEvent;

/// <summary>In-process queries other modules may use.</summary>
public interface IMembershipQueries
{
    /// <summary>Ids of the workspaces the user belongs to, sorted.</summary>
    Task<IReadOnlyList<Guid>> GetWorkspaceIdsAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IWorkspaceQueries
{
    /// <summary>True when the workspace is suspended or being deleted (cached; cleared on workspace changes). Unknown workspaces count as suspended.</summary>
    Task<bool> IsSuspendedAsync(Guid workspaceId, CancellationToken cancellationToken);
}
