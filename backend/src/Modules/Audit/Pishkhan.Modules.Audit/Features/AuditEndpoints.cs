using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Audit.Domain;
using Pishkhan.Modules.Audit.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Audit.Features;

public sealed record AuditEntryResponse(
    Guid Id, DateTimeOffset OccurredAt, string ActorType, Guid? ActorId, string ActorName, Guid? OnBehalfOf, Guid? WorkspaceId, string Action,
    string EntityType, string? EntityId, string? Reason, string? Ip, JsonElement? Before, JsonElement? After);

/// <summary>Filters of the audit search. Text filters match anywhere, case-insensitive; <c>actor</c> also matches an exact actor id.</summary>
public sealed record AuditSearch(string? Actor, string? Action, string? EntityType, Guid? WorkspaceId, DateTimeOffset? From, DateTimeOffset? To, int? Page, int? PageSize);

/// <summary>The workspace audit filters: <see cref="AuditSearch"/> without <c>workspaceId</c>, which the route already carries.</summary>
public sealed record WorkspaceAuditSearch(string? Actor, string? Action, string? EntityType, DateTimeOffset? From, DateTimeOffset? To, int? Page, int? PageSize);

internal static class AuditEndpoints
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>What a workspace sees instead of a staff member's name and address.</summary>
    public const string SupportName = "support";

    public static void MapAdmin(IEndpointRouteBuilder app)
    {
        app.MapAdminApi().WithTags("Audit")
            .MapGet("/audit", SearchAsync)
            .RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Support))
            .WithSummary("Search the audit log, newest first")
            .WithDescription("SuperAdmin sees every entry; Support sees only entries of a workspace. Before/after snapshots have secrets redacted.")
            .Produces<PagedResult<AuditEntryResponse>>()
            .WithStandardProblems();
    }

    public static void MapPanel(IEndpointRouteBuilder app)
    {
        app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}").WithTags("Audit")
            .MapGet("/audit", WorkspaceSearchAsync)
            .RequireWorkspacePermission(Permissions.AuditRead)
            .WithSummary("The workspace's audit log, newest first (owner only)")
            .WithDescription("Actions by staff (support access) show as actor \"support\", without the staff member's identity or address.")
            .Produces<PagedResult<AuditEntryResponse>>()
            .WithStandardProblems();
    }

    private static async Task<IResult> SearchAsync([AsParameters] AuditSearch search, ClaimsPrincipal user, AuditDbContext db, CancellationToken ct)
    {
        var supportOnly = !user.HasClaim(PishkhanClaims.Roles, StaffRoles.SuperAdmin);
        return await RunAsync(db, search, search.WorkspaceId, supportOnly, maskStaff: false, ct);
    }

    private static Task<IResult> WorkspaceSearchAsync(Guid workspaceId, [AsParameters] WorkspaceAuditSearch search, AuditDbContext db, CancellationToken ct) =>
        RunAsync(db, new AuditSearch(search.Actor, search.Action, search.EntityType, null, search.From, search.To, search.Page, search.PageSize), workspaceId, workspaceOnly: true, maskStaff: true, ct);

    private static async Task<IResult> RunAsync(AuditDbContext db, AuditSearch search, Guid? workspaceId, bool workspaceOnly, bool maskStaff, CancellationToken ct)
    {
        if (search.From is { } from && search.To is { } to && from > to)
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["from"] = ["validation.range"] }).ToProblem();
        }

        var page = Math.Max(1, search.Page ?? 1);
        var pageSize = Math.Clamp(search.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var query = Filter(db.Entries.AsNoTracking(), search, workspaceId, workspaceOnly);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new PagedResult<AuditEntryResponse>([.. rows.Select(r => ToResponse(r, maskStaff))], total, page, pageSize));
    }

    internal static IQueryable<AuditEntry> Filter(IQueryable<AuditEntry> query, AuditSearch search, Guid? workspaceId, bool workspaceOnly)
    {
        if (workspaceId is { } ws)
        {
            query = query.Where(e => e.WorkspaceId == ws);
        }
        else if (workspaceOnly)
        {
            query = query.Where(e => e.WorkspaceId != null);
        }

        if (Text(search.Actor) is { } actor)
        {
            var pattern = Contains(actor);
            query = Guid.TryParse(actor, out var actorId)
                ? query.Where(e => e.ActorId == actorId)
                : query.Where(e => EF.Functions.ILike(e.ActorName, pattern, "\\"));
        }

        if (Text(search.Action) is { } action)
        {
            var pattern = Contains(action);
            query = query.Where(e => EF.Functions.ILike(e.Action, pattern, "\\"));
        }

        if (Text(search.EntityType) is { } entityType)
        {
            var normalized = entityType.ToLowerInvariant();
            query = query.Where(e => e.EntityType == normalized);
        }

        if (search.From is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (search.To is { } to)
        {
            query = query.Where(e => e.OccurredAt <= to);
        }

        return query;
    }

    internal static AuditEntryResponse ToResponse(AuditEntry e, bool maskStaff)
    {
        var hide = maskStaff && e.ActorType == AuditActorTypes.Staff;
        return new AuditEntryResponse(
            e.Id, e.OccurredAt, e.ActorType, hide ? null : e.ActorId, hide ? SupportName : e.ActorName, e.OnBehalfOf, e.WorkspaceId, e.Action,
            e.EntityType, e.EntityId, e.Reason, hide ? null : e.Ip, Parse(e.BeforeJson), Parse(e.AfterJson));
    }

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Contains(string value) =>
        "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
