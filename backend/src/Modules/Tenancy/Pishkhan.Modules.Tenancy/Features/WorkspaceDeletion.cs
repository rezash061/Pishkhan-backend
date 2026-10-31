using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Modules.Tenancy.Domain;
using Pishkhan.Modules.Tenancy.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Tenancy.Features;

/// <summary>The owner types the workspace's name to confirm.</summary>
public sealed record RequestDeletionRequest(string ConfirmName);

public sealed record RequestDeletionCommand(RequestDeletionRequest Request);

public sealed record CancelDeletionCommand(Guid WorkspaceId);

/// <summary>
/// Workspace deletion with a 30-day grace period (backend plan 3.4). Asking stops the workspace at once: members lose access and
/// automations stop sending. The owner can cancel until <c>deletionScheduledFor</c>; after it the purge job deletes the data.
/// </summary>
internal sealed class WorkspaceDeletionHandlers(TenancyDbContext db, ITenantContext tenant, ICurrentUser caller, ICacheInvalidator cache, IClock clock) :
    ICommandHandler<RequestDeletionCommand, WorkspaceResponse>,
    ICommandHandler<CancelDeletionCommand, WorkspaceResponse>
{
    public async Task<Result<WorkspaceResponse>> HandleAsync(RequestDeletionCommand command, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == tenant.WorkspaceId, cancellationToken);
        if (!string.Equals(command.Request.ConfirmName?.Trim(), workspace.Name, StringComparison.Ordinal))
        {
            return Error.Validation(
                "tenancy.deletion.confirm_mismatch",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["confirmName"] = ["tenancy.deletion.confirm_mismatch"] });
        }

        var now = clock.UtcNow;
        workspace.RequestDeletion(now);
        db.Publish(new WorkspaceChanged(workspace.Id, "deletion_requested"), key: workspace.Id.ToString());
        Audit("workspace.deletion_request", workspace.Id, after: new { deletionScheduledFor = workspace.DeletionScheduledFor });
        await SaveAsync(workspace.Id, cancellationToken);
        return WorkspaceHandlers.ToResponse(workspace, WorkspaceRoles.Owner);
    }

    /// <summary>
    /// Not behind the workspace permission filter, which hides a workspace pending deletion. Members who may delete (the owner)
    /// can cancel; other members get 403 and non-members 404.
    /// </summary>
    public async Task<Result<WorkspaceResponse>> HandleAsync(CancelDeletionCommand command, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships
            .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .FirstOrDefaultAsync(m => m.WorkspaceId == command.WorkspaceId && m.UserId == caller.UserId, cancellationToken);
        var workspace = membership is null ? null : await db.Workspaces.FirstOrDefaultAsync(w => w.Id == command.WorkspaceId, cancellationToken);
        if (membership is null || workspace is null || workspace.Status == WorkspaceStatus.Deleted)
        {
            return Error.NotFound();
        }

        if (!membership.EffectivePermissions().Contains(Permissions.WorkspaceDelete))
        {
            return Error.Forbidden();
        }

        if (workspace.Status != WorkspaceStatus.PendingDeletion)
        {
            return Error.Conflict("tenancy.deletion.not_requested");
        }

        tenant.SetWorkspace(workspace.Id);
        var before = new { deletionScheduledFor = workspace.DeletionScheduledFor };
        workspace.CancelDeletion();
        db.Publish(new WorkspaceChanged(workspace.Id, "deletion_cancelled"), key: workspace.Id.ToString());
        Audit("workspace.deletion_cancel", workspace.Id, before);
        await SaveAsync(workspace.Id, cancellationToken);
        return WorkspaceHandlers.ToResponse(workspace, membership.Role);
    }

    private async Task SaveAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        cache.Enqueue(db, CacheTags.Workspace(workspaceId));
        await db.SaveChangesAsync(cancellationToken);
        await cache.InvalidateLocalAsync([CacheTags.Workspace(workspaceId)], cancellationToken);
    }

    private void Audit(string action, Guid workspaceId, object? before = null, object? after = null)
    {
        var entry = AuditEntries.Create(AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), action, "workspace", workspaceId.ToString(), workspaceId, clock.UtcNow, before, after);
        db.Publish(entry, entry.PartitionKey());
    }
}
