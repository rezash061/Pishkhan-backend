using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Identity.Domain;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.Modules.Identity.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Identity.Features;

/// <summary>
/// Filters of the back-office user search. <c>q</c> is a user id, a phone (exact, any spelling) or text in the display name.
/// <c>status</c>: active, suspended.
/// </summary>
public sealed record AdminUserSearch(string? Q, string? Status, int? Page, int? PageSize);

/// <summary><c>activeSessions</c> counts signed-in devices (support sessions are not counted).</summary>
public sealed record AdminUserSummary(
    Guid Id, string? DisplayName, string MaskedPhone, string Status, bool TotpEnabled, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, int ActiveSessions);

/// <summary>One signed-in device: <c>id</c> is the session id (stable across refreshes), <c>authMethods</c> for example otp or otp+totp.</summary>
public sealed record AdminUserSession(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, DateTimeOffset ExpiresAt, string? Ip, string? UserAgent, string AuthMethods);

public sealed record AdminUserDetail(
    Guid Id, string? DisplayName, string MaskedPhone, string Status, bool TotpEnabled, bool HasPassword, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt,
    IReadOnlyList<UserMembership> Workspaces, IReadOnlyList<AdminUserSession> Sessions);

/// <summary>Why the action is taken: 5 to 300 characters, kept in the audit log.</summary>
public sealed record AdminUserActionRequest(string Reason);

/// <summary>AdminApi: find a panel user, look at them, sign them out everywhere, block and unblock (BE-1.12, admin plan 4.3).</summary>
internal static class AdminUserEndpoints
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support, StaffRoles.Finance, StaffRoles.ReadOnly];

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Users");
        var read = admin.MapGroup(string.Empty).RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));

        read.MapGet("/users", SearchAsync)
            .WithSummary("Search panel users, newest first")
            .WithDescription("Roles: SuperAdmin, Support, Finance, ReadOnly. Phones are masked; search by a full phone number to find one user.")
            .Produces<PagedResult<AdminUserSummary>>()
            .WithStandardProblems();

        read.MapGet("/users/{userId:guid}", GetAsync)
            .WithSummary("A user with their workspaces and signed-in devices")
            .WithDescription("Roles: SuperAdmin, Support, Finance, ReadOnly.")
            .Produces<AdminUserDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        var act = admin.MapGroup("/users/{userId:guid}").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Support));

        act.MapPost("/sessions/revoke", RevokeSessionsAsync)
            .RequireStepUp()
            .WithSummary("Sign the user out on every device")
            .WithDescription("Support or SuperAdmin, step-up. Access tokens stop working at once; audited as user.sessions_revoke.")
            .Produces<AdminUserSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        act.MapPost("/block", BlockAsync)
            .RequireStepUp()
            .WithSummary("Block the user: no sign-in, every session ends")
            .WithDescription("Support or SuperAdmin, step-up. Their workspaces stay as they are; audited as user.block.")
            .Produces<AdminUserSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        act.MapPost("/unblock", UnblockAsync)
            .RequireStepUp()
            .WithSummary("Let a blocked user sign in again")
            .WithDescription("Support or SuperAdmin, step-up. Audited as user.unblock.")
            .Produces<AdminUserSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();
    }

    private static async Task<IResult> SearchAsync([AsParameters] AdminUserSearch search, IdentityDbContext db, PhoneProtection phones, IClock clock, CancellationToken ct)
    {
        if (search.Q is { Length: > 100 })
        {
            return Invalid("q", "validation.too_long");
        }

        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search.Status))
        {
            if (ParseStatus(search.Status) is not { } status)
            {
                return Invalid("status", "validation.invalid");
            }

            query = query.Where(u => u.Status == status);
        }

        if (search.Q?.Trim() is { Length: > 0 } q)
        {
            if (Guid.TryParse(q, out var id))
            {
                query = query.Where(u => u.Id == id);
            }
            else if (PhoneNumber.TryParse(q, out var phone))
            {
                var index = phones.BlindIndex(phone.Value);
                query = query.Where(u => u.PhoneBidx == index);
            }
            else
            {
                var pattern = "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                query = query.Where(u => u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern, "\\"));
            }
        }

        var page = Math.Max(1, search.Page ?? 1);
        var pageSize = Math.Clamp(search.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(u => u.CreatedAt).ThenByDescending(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = rows.Select(u => u.Id).ToList();
        var sessions = await ActiveSessionCountsAsync(db, ids, clock.UtcNow, ct);
        return Results.Ok(new PagedResult<AdminUserSummary>([.. rows.Select(u => ToSummary(u, phones, sessions.GetValueOrDefault(u.Id)))], total, page, pageSize));
    }

    private static async Task<IResult> GetAsync(Guid userId, IdentityDbContext db, PhoneProtection phones, IMembershipQueries memberships, IClock clock, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        var active = await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        var families = active.Select(s => s.FamilyId).ToList();
        var firstSeen = await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == userId && families.Contains(s.FamilyId))
            .GroupBy(s => s.FamilyId)
            .Select(g => new { FamilyId = g.Key, CreatedAt = g.Min(s => s.CreatedAt) })
            .ToDictionaryAsync(x => x.FamilyId, x => x.CreatedAt, ct);

        return Results.Ok(new AdminUserDetail(
            user.Id, user.DisplayName, phones.Decrypt(user.PhoneE164Enc).Masked, StatusName(user.Status), user.TotpEnabled, user.PasswordHash is not null,
            user.CreatedAt, user.LastLoginAt,
            await memberships.GetMembershipsOfUserAsync(userId, ct),
            [.. active.Select(s => new AdminUserSession(s.FamilyId, firstSeen.GetValueOrDefault(s.FamilyId, s.CreatedAt), s.CreatedAt, s.ExpiresAt, s.Ip, s.UserAgent, s.AuthMethods))]));
    }

    private static Task<IResult> RevokeSessionsAsync(Guid userId, AdminUserActionRequest request, AdminUserActions actions, CancellationToken ct) =>
        actions.RunAsync(userId, request, "user.sessions_revoke", _ => { }, ct);

    private static Task<IResult> BlockAsync(Guid userId, AdminUserActionRequest request, AdminUserActions actions, CancellationToken ct) =>
        actions.RunAsync(userId, request, "user.block", u => u.Suspend(), ct);

    private static Task<IResult> UnblockAsync(Guid userId, AdminUserActionRequest request, AdminUserActions actions, CancellationToken ct) =>
        actions.RunAsync(userId, request, "user.unblock", u => u.Reactivate(), ct, revoke: false);

    internal static async Task<Dictionary<Guid, int>> ActiveSessionCountsAsync(IdentityDbContext db, IReadOnlyCollection<Guid> userIds, DateTimeOffset now, CancellationToken ct) =>
        await db.Sessions.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId) && s.RevokedAt == null && s.ExpiresAt > now)
            .GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Select(s => s.FamilyId).Distinct().Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

    internal static AdminUserSummary ToSummary(User u, PhoneProtection phones, int activeSessions) =>
        new(u.Id, u.DisplayName, phones.Decrypt(u.PhoneE164Enc).Masked, StatusName(u.Status), u.TotpEnabled, u.CreatedAt, u.LastLoginAt, activeSessions);

    internal static string StatusName(UserStatus status) => status.ToString().ToLowerInvariant();

    private static UserStatus? ParseStatus(string value) =>
        Enum.GetValues<UserStatus>().Cast<UserStatus?>().FirstOrDefault(s => StatusName(s!.Value) == value.Trim().ToLowerInvariant());

    internal static IResult Invalid(string field, string code) =>
        Error.Validation("validation.failed", new Dictionary<string, string[]> { [field] = [code] }).ToProblem();
}

/// <summary>A staff action on a user: change it, end its sessions, audit it as a platform entry with the reason.</summary>
internal sealed class AdminUserActions(
    IdentityDbContext db,
    PhoneProtection phones,
    ISessionRevocationStore revocations,
    IStaffDirectory staff,
    ICurrentUser caller,
    IConfiguration configuration,
    IClock clock)
{
    public async Task<IResult> RunAsync(Guid userId, AdminUserActionRequest request, string action, Action<User> change, CancellationToken ct, bool revoke = true)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 5 or > 300)
        {
            return AdminUserEndpoints.Invalid("reason", reason.Length < 5 ? "validation.too_short" : "validation.too_long");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        var before = AdminUserEndpoints.StatusName(user.Status);
        change(user);
        var families = revoke
            ? await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now).Select(s => s.FamilyId).Distinct().ToListAsync(ct)
            : [];

        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), action, "user", userId.ToString(), null, now,
            before: new { status = before }, after: new { status = AdminUserEndpoints.StatusName(user.Status), sessionsEnded = families.Count }, reason: reason);
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
        await db.SaveChangesAsync(ct);

        if (revoke)
        {
            await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, Session.ReasonRevokedByStaff).SetProperty(x => x.UpdatedAt, now), ct);

            // Access tokens live a few minutes more; the revocation list rejects them until they expire.
            var lifetime = (configuration.GetSection("Auth:User").Get<JwtAudienceOptions>() ?? new JwtAudienceOptions()).AccessTokenLifetime + TimeSpan.FromMinutes(1);
            foreach (var family in families)
            {
                await revocations.RevokeAsync(AuthSchemes.User, family.ToString(), lifetime, ct);
            }
        }

        return Results.Ok(AdminUserEndpoints.ToSummary(user, phones, revoke ? 0 : await CountAsync(userId, now, ct)));
    }

    private async Task<int> CountAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        (await AdminUserEndpoints.ActiveSessionCountsAsync(db, [userId], now, ct)).GetValueOrDefault(userId);
}
