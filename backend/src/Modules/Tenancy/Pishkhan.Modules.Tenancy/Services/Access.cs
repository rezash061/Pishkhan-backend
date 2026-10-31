using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Modules.Tenancy.Domain;
using Pishkhan.Modules.Tenancy.Persistence;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Tenancy.Services;

/// <summary>Cached membership lookup used by every workspace-scoped endpoint (<c>ws:{ws}:member:{user}</c>, 5 minutes).</summary>
internal sealed class WorkspaceAccessResolver(TenancyDbContext db, HybridCache cache) : IWorkspaceAccessResolver
{
    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(1) };

    public async Task<WorkspaceAccess?> ResolveAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken)
    {
        var cached = await cache.GetOrCreateAsync(
            $"ws:{workspaceId:N}:member:{userId:N}",
            (db, userId, workspaceId),
            static async (state, ct) =>
            {
                var row = await state.db.Memberships.AsNoTracking()
                    .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
                    .Where(m => m.WorkspaceId == state.workspaceId && m.UserId == state.userId)
                    .Join(state.db.Workspaces, m => m.WorkspaceId, w => w.Id, (m, w) => new { Membership = m, w.Status })
                    .FirstOrDefaultAsync(ct);
                return row is null || row.Status is WorkspaceStatus.PendingDeletion or WorkspaceStatus.Deleted
                    ? null
                    : new CachedAccess(row.Membership.Role, [.. row.Membership.EffectivePermissions()], row.Status == WorkspaceStatus.Suspended);
            },
            Entry,
            [CacheTags.Workspace(workspaceId), CacheTags.Member(workspaceId, userId)],
            cancellationToken);

        return cached is null ? null : new WorkspaceAccess(workspaceId, cached.Role, cached.Permissions.ToHashSet(StringComparer.Ordinal), cached.Suspended);
    }

    internal sealed record CachedAccess(string Role, string[] Permissions, bool Suspended);
}

internal sealed class MembershipQueries(TenancyDbContext db) : IMembershipQueries
{
    public async Task<IReadOnlyList<Guid>> GetWorkspaceIdsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Memberships.AsNoTracking()
            .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.WorkspaceId)
            .Select(m => m.WorkspaceId)
            .ToListAsync(cancellationToken);

    public async Task<Guid?> GetOwnerIdAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await db.Memberships.AsNoTracking()
            .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(m => m.WorkspaceId == workspaceId && m.Role == WorkspaceRoles.Owner)
            .OrderBy(m => m.CreatedAt)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetMembersWithPermissionAsync(Guid workspaceId, string permission, CancellationToken cancellationToken)
    {
        var members = await db.Memberships.AsNoTracking()
            .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. members.Where(m => m.EffectivePermissions().Contains(permission)).Select(m => m.UserId)];
    }

    public async Task<IReadOnlyList<UserMembership>> GetMembershipsOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Memberships.AsNoTracking()
            .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(m => m.UserId == userId)
            .Join(db.Workspaces, m => m.WorkspaceId, w => w.Id, (m, w) => new { m.WorkspaceId, w.Name, w.Status, m.Role, m.CreatedAt })
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new UserMembership(r.WorkspaceId, r.Name, Workspace.StatusName(r.Status), r.Role, r.CreatedAt))];
    }
}

/// <summary>Workspace status and business hours for other modules (<c>ws:{ws}:status</c>, <c>ws:{ws}:hours</c>, 5 minutes, tagged by workspace).</summary>
internal sealed class WorkspaceQueries(TenancyDbContext db, HybridCache cache) : IWorkspaceQueries
{
    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(1) };

    public async Task<bool> IsSuspendedAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var status = await cache.GetOrCreateAsync(
            $"ws:{workspaceId:N}:status",
            (db, workspaceId),
            static async (state, ct) => await state.db.Workspaces.AsNoTracking()
                .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
                .Where(w => w.Id == state.workspaceId)
                .Select(w => (WorkspaceStatus?)w.Status)
                .FirstOrDefaultAsync(ct),
            Entry,
            [CacheTags.Workspace(workspaceId)],
            cancellationToken);
        return status is not WorkspaceStatus.Active;
    }

    public async Task<BusinessHours> GetBusinessHoursAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            $"ws:{workspaceId:N}:hours",
            (db, workspaceId),
            static async (state, ct) => await state.db.Workspaces.AsNoTracking()
                .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
                .Where(w => w.Id == state.workspaceId)
                .Select(w => w.BusinessHours)
                .FirstOrDefaultAsync(ct) ?? BusinessHours.Default,
            Entry,
            [CacheTags.Workspace(workspaceId)],
            cancellationToken);
}
