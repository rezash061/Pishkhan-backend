using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Modules.Tenancy.Domain;
using Pishkhan.Modules.Tenancy.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Tenancy.Features;

/// <summary>
/// Filters of the back-office workspace search. <c>q</c> is a workspace id, an owner phone (exact, any spelling: 0912…, +98912…,
/// Persian digits), or text matched anywhere in the name, the slug or an Instagram username (a leading @ is ignored).
/// <c>status</c>: active, suspended, pending_deletion, deleted.
/// </summary>
public sealed record AdminWorkspaceSearch(string? Q, string? Status, string? Industry, DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedTo, int? Page, int? PageSize);

public sealed record AdminWorkspaceOwner(Guid Id, string? DisplayName, string MaskedPhone);

/// <summary>A connected account. <c>status</c>: connecting, active, token_expiring, reauth_required, disconnected.</summary>
public sealed record AdminWorkspaceChannel(
    Guid Id, string Kind, string? Username, string Status, string? StatusReason, DateTimeOffset? StatusChangedAt, DateTimeOffset? TokenExpiresAt, DateTimeOffset ConnectedAt);

public sealed record AdminWorkspaceSummary(
    Guid Id, string Name, string Slug, string Industry, string Status, DateTimeOffset CreatedAt, DateTimeOffset? DeletionScheduledFor,
    AdminWorkspaceOwner? Owner, int MemberCount, IReadOnlyList<AdminWorkspaceChannel> Channels);

public sealed record AdminWorkspaceMember(Guid UserId, string? DisplayName, string? MaskedPhone, string Role, DateTimeOffset JoinedAt);

public sealed record AdminWorkspaceDetail(
    Guid Id, string Name, string Slug, string Industry, string TimeZone, string Status, Guid? PlanId, DateTimeOffset CreatedAt,
    DateTimeOffset? DeletionRequestedAt, DateTimeOffset? DeletionScheduledFor, DateTimeOffset? DeletedAt, AdminWorkspaceOwner? Owner,
    IReadOnlyList<AdminWorkspaceMember> Members, IReadOnlyList<AdminWorkspaceChannel> Channels, int PendingInvitations);

/// <summary>AdminApi: find a workspace and look at it (BE-1.12, admin plan 4.2). Read only, so not audited.</summary>
internal static class AdminWorkspaceEndpoints
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>Instagram matches considered per search; a search that broad is refined by the staff member anyway.</summary>
    private const int UsernameMatchLimit = 500;

    /// <summary>Roles that see workspaces (admin plan 3): Support works with them, Finance bills them, ReadOnly sees lists.</summary>
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support, StaffRoles.Finance, StaffRoles.ReadOnly];

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Workspaces")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));

        admin.MapGet("/workspaces", SearchAsync)
            .WithSummary("Search workspaces, newest first")
            .WithDescription("Roles: SuperAdmin, Support, Finance, ReadOnly. Owner phones are masked; each row carries its channels' status.")
            .Produces<PagedResult<AdminWorkspaceSummary>>()
            .WithStandardProblems();

        admin.MapGet("/workspaces/{workspaceId:guid}", GetAsync)
            .WithSummary("A workspace with its owner, members and channels")
            .WithDescription("Roles: SuperAdmin, Support, Finance, ReadOnly. Deleted workspaces are still found (their data is gone).")
            .Produces<AdminWorkspaceDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> SearchAsync(
        [AsParameters] AdminWorkspaceSearch search, TenancyDbContext db, IUserDirectory users, IChannelDirectory channels, CancellationToken ct)
    {
        if (search.Q is { Length: > 100 })
        {
            return Invalid("q", "validation.too_long");
        }

        WorkspaceStatus? status = null;
        if (!string.IsNullOrWhiteSpace(search.Status))
        {
            if (ParseStatus(search.Status) is not { } parsed)
            {
                return Invalid("status", "validation.invalid");
            }

            status = parsed;
        }

        if (search.CreatedFrom is { } from && search.CreatedTo is { } to && from > to)
        {
            return Invalid("createdFrom", "validation.range");
        }

        var query = db.Workspaces.AsNoTracking();
        if (search.Q?.Trim() is { Length: > 0 } q)
        {
            query = await MatchAsync(query, q, db, users, channels, ct);
        }

        if (status is { } s)
        {
            query = query.Where(w => w.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(search.Industry))
        {
            var industry = search.Industry.Trim().ToLowerInvariant();
            query = query.Where(w => w.Industry == industry);
        }

        if (search.CreatedFrom is { } createdFrom)
        {
            query = query.Where(w => w.CreatedAt >= createdFrom);
        }

        if (search.CreatedTo is { } createdTo)
        {
            query = query.Where(w => w.CreatedAt <= createdTo);
        }

        var page = Math.Max(1, search.Page ?? 1);
        var pageSize = Math.Clamp(search.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var ids = rows.Select(w => w.Id).ToList();
        var memberships = await Memberships(db).Where(m => ids.Contains(m.WorkspaceId)).Select(m => new { m.WorkspaceId, m.UserId, m.Role }).ToListAsync(ct);
        var owners = memberships.Where(m => m.Role == WorkspaceRoles.Owner).GroupBy(m => m.WorkspaceId).ToDictionary(g => g.Key, g => g.First().UserId);
        var people = await users.GetManyAsync([.. owners.Values.Distinct()], ct);
        var accounts = (await channels.ListForWorkspacesAsync(ids, ct)).ToLookup(a => a.WorkspaceId);

        var items = rows.Select(w => new AdminWorkspaceSummary(
            w.Id, w.Name, w.Slug, w.Industry, Workspace.StatusName(w.Status), w.CreatedAt, w.DeletionScheduledFor,
            owners.TryGetValue(w.Id, out var owner) ? Owner(people, owner) : null,
            memberships.Count(m => m.WorkspaceId == w.Id),
            [.. accounts[w.Id].Select(ToChannel)])).ToList();
        return Results.Ok(new PagedResult<AdminWorkspaceSummary>(items, total, page, pageSize));
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, TenancyDbContext db, IUserDirectory users, IChannelDirectory channels, CancellationToken ct)
    {
        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace is null)
        {
            return Error.NotFound().ToProblem();
        }

        var members = await Memberships(db).Where(m => m.WorkspaceId == workspaceId).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var people = await users.GetManyAsync([.. members.Select(m => m.UserId)], ct);
        var invitations = await db.Invitations.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .CountAsync(i => i.WorkspaceId == workspaceId && i.Status == InvitationStatus.Pending, ct);
        var accounts = await channels.ListForWorkspacesAsync([workspaceId], ct);
        var owner = members.FirstOrDefault(m => m.Role == WorkspaceRoles.Owner);

        return Results.Ok(new AdminWorkspaceDetail(
            workspace.Id, workspace.Name, workspace.Slug, workspace.Industry, workspace.TimeZone, Workspace.StatusName(workspace.Status), workspace.PlanId,
            workspace.CreatedAt, workspace.DeletionRequestedAt, workspace.DeletionScheduledFor, workspace.DeletedAt,
            owner is null ? null : Owner(people, owner.UserId),
            [.. members.Select(m => new AdminWorkspaceMember(m.UserId, people.GetValueOrDefault(m.UserId)?.DisplayName, people.GetValueOrDefault(m.UserId)?.MaskedPhone, m.Role, m.CreatedAt))],
            [.. accounts.Select(ToChannel)],
            invitations));
    }

    /// <summary>An id, an owner phone, or text in the name, slug or an Instagram username.</summary>
    private static async Task<IQueryable<Workspace>> MatchAsync(
        IQueryable<Workspace> query, string q, TenancyDbContext db, IUserDirectory users, IChannelDirectory channels, CancellationToken ct)
    {
        if (Guid.TryParse(q, out var id))
        {
            return query.Where(w => w.Id == id);
        }

        if (PhoneNumber.TryParse(q, out var phone))
        {
            var userId = await users.FindByPhoneAsync(phone.Value, ct);
            var owned = Memberships(db).Where(m => m.UserId == userId && m.Role == WorkspaceRoles.Owner).Select(m => m.WorkspaceId);
            return query.Where(w => owned.Contains(w.Id));
        }

        var text = q.TrimStart('@');
        var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var byUsername = await channels.FindWorkspacesByUsernameAsync(text, UsernameMatchLimit, ct);
        return query.Where(w => EF.Functions.ILike(w.Name, pattern, "\\") || EF.Functions.ILike(w.Slug, pattern, "\\") || byUsername.Contains(w.Id));
    }

    private static IQueryable<Membership> Memberships(TenancyDbContext db) =>
        db.Memberships.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static WorkspaceStatus? ParseStatus(string value) =>
        Enum.GetValues<WorkspaceStatus>().Cast<WorkspaceStatus?>().FirstOrDefault(s => Workspace.StatusName(s!.Value) == value.Trim().ToLowerInvariant());

    private static AdminWorkspaceOwner? Owner(IReadOnlyDictionary<Guid, UserSummary> people, Guid userId) =>
        people.TryGetValue(userId, out var user) ? new AdminWorkspaceOwner(user.Id, user.DisplayName, user.MaskedPhone) : null;

    private static AdminWorkspaceChannel ToChannel(ChannelAccountState a) =>
        new(a.Id, a.Kind, a.Username, a.Status, a.StatusReason, a.StatusChangedAt, a.TokenExpiresAt, a.ConnectedAt);

    private static IResult Invalid(string field, string code) =>
        Error.Validation("validation.failed", new Dictionary<string, string[]> { [field] = [code] }).ToProblem();
}
