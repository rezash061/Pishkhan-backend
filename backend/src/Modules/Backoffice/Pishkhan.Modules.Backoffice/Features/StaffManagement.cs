using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Domain;
using Pishkhan.Modules.Backoffice.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Backoffice.Features;

/// <summary><c>reason</c>: 5 to 300 characters, kept in the audit log.</summary>
public sealed record StaffActionRequest(string Reason);

public sealed record StaffRolesRequest(IReadOnlyList<string> Roles, string Reason);

/// <summary>A sign-in of the current staff member (one per refresh token family). <c>current</c>: the session making this request.</summary>
public sealed record StaffSessionResponse(Guid Id, DateTimeOffset StartedAt, DateTimeOffset LastActiveAt, DateTimeOffset ExpiresAt, string? Ip, string? UserAgent, bool Current);

/// <summary>AdminApi staff management (AD-0.2 / AD-2.7): activate, deactivate, TOTP reset, roles, and the caller's own sessions.</summary>
internal static class StaffManagementEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Staff");
        var super = admin.MapGroup("/staff/{id:guid}").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.SuperAdmin)).RequireStepUp();

        super.MapPost("/deactivate", DeactivateAsync)
            .WithSummary("Deactivate a staff account and end its sessions")
            .WithDescription("SuperAdmin, step-up, reason. 409 `backoffice.staff.self` for your own account, `backoffice.staff.last_super_admin` for the last active SuperAdmin.")
            .Produces<StaffSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        super.MapPost("/activate", ActivateAsync)
            .WithSummary("Reactivate a staff account (also clears a lockout)")
            .WithDescription("SuperAdmin, step-up, reason.")
            .Produces<StaffSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        super.MapPost("/totp-reset", ResetTotpAsync)
            .WithSummary("Clear a staff member's TOTP so the next sign-in enrolls again, and end their sessions")
            .WithDescription("SuperAdmin, step-up, reason. 409 `backoffice.staff.self` for your own account.")
            .Produces<StaffSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        super.MapPut("/roles", SetRolesAsync)
            .WithSummary("Replace a staff member's roles and end their sessions so the new roles apply")
            .WithDescription("SuperAdmin, step-up, reason. 409 `backoffice.staff.last_super_admin` when it would leave no active SuperAdmin.")
            .Produces<StaffSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        admin.MapGet("/me/sessions", MySessionsAsync)
            .WithSummary("The current staff member's active sessions")
            .Produces<IReadOnlyList<StaffSessionResponse>>();

        admin.MapDelete("/me/sessions/{sessionId:guid}", RevokeMySessionAsync)
            .WithSummary("End one of your own sessions")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static Task<IResult> DeactivateAsync(Guid id, StaffActionRequest request, StaffOps ops, CancellationToken ct) =>
        ops.ChangeAsync(id, request.Reason, "staff.deactivate", async (staff, caller) =>
        {
            if (staff.Id == caller)
            {
                return "backoffice.staff.self";
            }

            if (staff.Roles.Contains(StaffRoles.SuperAdmin) && !await ops.OtherActiveSuperAdminAsync(staff.Id, ct))
            {
                return "backoffice.staff.last_super_admin";
            }

            staff.Disable();
            return null;
        }, endSessions: true, ct);

    private static Task<IResult> ActivateAsync(Guid id, StaffActionRequest request, StaffOps ops, CancellationToken ct) =>
        ops.ChangeAsync(id, request.Reason, "staff.activate", (staff, _) =>
        {
            staff.Activate();
            return Task.FromResult<string?>(null);
        }, endSessions: false, ct);

    private static Task<IResult> ResetTotpAsync(Guid id, StaffActionRequest request, StaffOps ops, CancellationToken ct) =>
        ops.ChangeAsync(id, request.Reason, "staff.totp_reset", (staff, caller) =>
        {
            if (staff.Id == caller)
            {
                return Task.FromResult<string?>("backoffice.staff.self");
            }

            staff.ResetTotp();
            return Task.FromResult<string?>(null);
        }, endSessions: true, ct);

    private static Task<IResult> SetRolesAsync(Guid id, StaffRolesRequest request, StaffOps ops, CancellationToken ct)
    {
        if (request.Roles is null || request.Roles.Count == 0)
        {
            return Task.FromResult(PlatformOpsEndpoints.Invalid("roles", "validation.required"));
        }

        if (!request.Roles.All(StaffRoles.All.Contains))
        {
            return Task.FromResult(PlatformOpsEndpoints.Invalid("roles", "validation.role.invalid"));
        }

        return ops.ChangeAsync(id, request.Reason, "staff.roles_change", async (staff, _) =>
        {
            if (staff.Roles.Contains(StaffRoles.SuperAdmin) && !request.Roles.Contains(StaffRoles.SuperAdmin) && staff.Status == StaffStatus.Active &&
                !await ops.OtherActiveSuperAdminAsync(staff.Id, ct))
            {
                return "backoffice.staff.last_super_admin";
            }

            staff.SetRoles(request.Roles);
            return null;
        }, endSessions: true, ct);
    }

    private static async Task<IResult> MySessionsAsync(BackofficeDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await db.StaffSessions.AsNoTracking().Where(s => s.StaffId == caller.UserId && s.AbsoluteExpiresAt > now).ToListAsync(ct);
        var sessions = rows.GroupBy(s => s.FamilyId)
            .Select(g => (Family: g.Key, Latest: g.MaxBy(s => s.CreatedAt)!, Started: g.Min(s => s.CreatedAt)))
            .Where(x => x.Latest.IsActive(now))
            .OrderByDescending(x => x.Latest.CreatedAt)
            .Select(x => new StaffSessionResponse(x.Family, x.Started, x.Latest.CreatedAt, x.Latest.IdleExpiresAt, x.Latest.Ip, x.Latest.UserAgent, x.Family == caller.SessionId))
            .ToList();
        return Results.Ok(sessions);
    }

    private static async Task<IResult> RevokeMySessionAsync(Guid sessionId, StaffOps ops, BackofficeDbContext db, ICurrentUser caller, CancellationToken ct)
    {
        if (!await db.StaffSessions.AnyAsync(s => s.StaffId == caller.UserId && s.FamilyId == sessionId && s.RevokedAt == null, ct))
        {
            return Error.NotFound().ToProblem();
        }

        await ops.RevokeFamiliesAsync([sessionId], "revoked_by_owner", ct);
        var entry = AuditEntries.Create(await ops.CallerActorAsync(ct), "staff.session_revoke", "staff_session", sessionId.ToString(), null, ops.Now);
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

/// <summary>Shared steps of the staff management endpoints.</summary>
internal sealed class StaffOps(BackofficeDbContext db, ISessionRevocationStore revocations, TokenFactory tokens, ICurrentUser caller, IClock clock)
{
    public DateTimeOffset Now => clock.UtcNow;

    /// <summary>Loads the staff member, applies <paramref name="change"/> (which returns a conflict code or null), ends sessions if asked and audits.</summary>
    public async Task<IResult> ChangeAsync(Guid id, string? reason, string action, Func<StaffUser, Guid, Task<string?>> change, bool endSessions, CancellationToken ct)
    {
        if (PlatformOpsEndpoints.ReasonError(reason) is { } reasonError)
        {
            return PlatformOpsEndpoints.Invalid("reason", reasonError);
        }

        var staff = await db.StaffUsers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null)
        {
            return Error.NotFound().ToProblem();
        }

        var before = Snapshot(staff);
        if (await change(staff, caller.UserId) is { } conflict)
        {
            return Error.Conflict(conflict).ToProblem();
        }

        if (endSessions)
        {
            var families = await db.StaffSessions.Where(s => s.StaffId == id && s.RevokedAt == null).Select(s => s.FamilyId).Distinct().ToListAsync(ct);
            await RevokeFamiliesAsync(families, action, ct);
        }

        var entry = AuditEntries.Create(await CallerActorAsync(ct), action, "staff", id.ToString(), null, clock.UtcNow, before: before, after: Snapshot(staff), reason: reason!.Trim());
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
        await db.SaveChangesAsync(ct);
        return Results.Ok(StaffAuthHandlers.ToSummary(staff, await LastActiveAsync(id, ct)));
    }

    public Task<bool> OtherActiveSuperAdminAsync(Guid except, CancellationToken ct) =>
        db.StaffUsers.AnyAsync(s => s.Id != except && s.Status == StaffStatus.Active && s.Roles.Contains(StaffRoles.SuperAdmin), ct);

    public async Task RevokeFamiliesAsync(IReadOnlyList<Guid> families, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await db.StaffSessions.Where(s => families.Contains(s.FamilyId) && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, reason).SetProperty(x => x.UpdatedAt, now), ct);
        var ttl = tokens.AccessTokenLifetime(AuthSchemes.Staff) + TimeSpan.FromMinutes(1);
        foreach (var family in families)
        {
            await revocations.RevokeAsync(AuthSchemes.Staff, family.ToString(), ttl, ct);
        }
    }

    public async Task<AuditActor> CallerActorAsync(CancellationToken ct)
    {
        var username = await db.StaffUsers.AsNoTracking().Where(s => s.Id == caller.UserId).Select(s => s.Username).FirstOrDefaultAsync(ct);
        return AuditActor.Staff(caller.UserId, username, caller.IpAddress);
    }

    private Task<DateTimeOffset?> LastActiveAsync(Guid id, CancellationToken ct) =>
        db.StaffSessions.AsNoTracking().Where(s => s.StaffId == id).MaxAsync(s => (DateTimeOffset?)s.CreatedAt, ct);

    private static object Snapshot(StaffUser s) => new { status = s.Status.ToString().ToLowerInvariant(), roles = s.Roles.ToArray(), totpEnabled = s.TotpEnabled };
}
