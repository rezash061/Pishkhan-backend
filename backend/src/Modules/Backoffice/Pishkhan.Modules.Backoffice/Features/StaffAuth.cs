using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Backoffice.Domain;
using Pishkhan.Modules.Backoffice.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using StackExchange.Redis;

namespace Pishkhan.Modules.Backoffice.Features;

public sealed record StaffLoginRequest(string Username, string Password, string? TotpCode);

/// <param name="Status"><c>ok</c> (tokens present) or <c>totp_enrollment_required</c> (enrollment fields present).</param>
public sealed record StaffLoginResponse(
    string Status,
    string? AccessToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null,
    string? RefreshToken = null,
    DateTimeOffset? RefreshTokenExpiresAt = null,
    string? EnrollmentToken = null,
    string? TotpSecret = null,
    string? OtpAuthUri = null);

public sealed record TotpEnrollRequest(string EnrollmentToken, string Code);

public sealed record StaffRefreshRequest(string RefreshToken);

public sealed record StepUpRequest(string Password, string TotpCode);

public sealed record StepUpResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt);

public sealed record StaffMeResponse(Guid Id, string Username, IReadOnlyList<string> Roles);

public sealed record CreateStaffRequest(string Username, string Password, IReadOnlyList<string> Roles);

public sealed record StaffSummary(Guid Id, string Username, IReadOnlyList<string> Roles, string Status, bool TotpEnabled, DateTimeOffset CreatedAt);

public sealed record StaffLogoutCommand;

public sealed record StaffMeQuery;

public sealed record ListStaffQuery;

internal sealed class StaffLoginValidator : AbstractValidator<StaffLoginRequest>
{
    public StaffLoginValidator()
    {
        RuleFor(x => x.Username).NotEmpty().WithErrorCode("validation.required").MaximumLength(64).WithErrorCode("validation.too_long");
        RuleFor(x => x.Password).NotEmpty().WithErrorCode("validation.required").MaximumLength(200).WithErrorCode("validation.too_long");
    }
}

internal sealed class CreateStaffValidator : AbstractValidator<CreateStaffRequest>
{
    public CreateStaffValidator()
    {
        RuleFor(x => x.Username).Matches("^[a-z][a-z0-9._-]{2,63}$").WithErrorCode("validation.username.invalid");
        RuleFor(x => x.Password).MinimumLength(14).WithErrorCode("validation.password.too_short").MaximumLength(200).WithErrorCode("validation.too_long");
        RuleFor(x => x.Roles).NotEmpty().WithErrorCode("validation.required").Must(r => r.All(StaffRoles.All.Contains)).WithErrorCode("validation.role.invalid");
    }
}

public sealed class BackofficeOptions
{
    public const string Section = "Backoffice";

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan AbsoluteTimeout { get; set; } = TimeSpan.FromHours(8);

    public TimeSpan StepUpLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Base URL of the user panel; support-session links open <c>{UserPanelUrl}/api/auth/impersonation?token=</c>.</summary>
    public string UserPanelUrl { get; set; } = "http://localhost:3001";
}

/// <summary>Staff sign-in: password, then mandatory TOTP (enrolled on first sign-in), with lockout after 5 failures.</summary>
internal sealed class StaffAuthHandlers(
    BackofficeDbContext db,
    TokenFactory tokens,
    ISecretProtector secrets,
    ISessionRevocationStore revocations,
    [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis,
    RedisKeys keys,
    ICurrentUser caller,
    IClock clock,
    Microsoft.Extensions.Options.IOptions<BackofficeOptions> options) :
    ICommandHandler<StaffLoginRequest, StaffLoginResponse>,
    ICommandHandler<TotpEnrollRequest, StaffLoginResponse>,
    ICommandHandler<StaffRefreshRequest, StaffLoginResponse>,
    ICommandHandler<StaffLogoutCommand, Unit>,
    ICommandHandler<StepUpRequest, StepUpResponse>,
    IQueryHandler<StaffMeQuery, StaffMeResponse>,
    IQueryHandler<ListStaffQuery, IReadOnlyList<StaffSummary>>,
    ICommandHandler<CreateStaffRequest, StaffSummary>,
    IStaffProvisioning
{
    internal const string TotpPurpose = "backoffice.totp";
    private static readonly PasswordHasher<StaffUser> Hasher = new();

    public async Task<Result<StaffLoginResponse>> HandleAsync(StaffLoginRequest command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var staff = await db.StaffUsers.FirstOrDefaultAsync(s => s.Username == command.Username.Trim().ToLowerInvariant(), cancellationToken);
        if (staff is null)
        {
            Hasher.HashPassword(null!, command.Password); // equalize timing
            Audit(new AuditActor(AuditActorTypes.Staff, null, Clip(command.Username.Trim()), caller.IpAddress), "staff.login_failed", "staff", null, reason: "auth.login.invalid_credentials");
            await db.SaveChangesAsync(cancellationToken);
            return Error.Unauthorized("auth.login.invalid_credentials");
        }

        if (staff.Status != StaffStatus.Active || staff.IsLocked(now))
        {
            Audit(Actor(staff), "staff.login_failed", "staff", staff.Id, reason: "auth.login.locked");
            await db.SaveChangesAsync(cancellationToken);
            return Error.Unauthorized("auth.login.locked");
        }

        if (Hasher.VerifyHashedPassword(staff, staff.PasswordHash, command.Password) == PasswordVerificationResult.Failed)
        {
            return await FailAsync(staff, "staff.login_failed", "auth.login.invalid_credentials", cancellationToken);
        }

        if (!staff.TotpEnabled)
        {
            var secret = Totp.NewSecret();
            staff.SetTotpSecret(secrets.Protect(Convert.ToBase64String(secret), TotpPurpose));
            staff.RecordSuccess();
            await db.SaveChangesAsync(cancellationToken);
            var enrollmentToken = Base64Url.Encode(RandomNumberGenerator.GetBytes(24));
            await redis.GetDatabase().StringSetAsync(keys.For("staff", "enroll:" + Hash(enrollmentToken)), staff.Id.ToString(), TimeSpan.FromMinutes(10));
            return new StaffLoginResponse("totp_enrollment_required", EnrollmentToken: enrollmentToken, TotpSecret: Totp.ToBase32(secret), OtpAuthUri: Totp.ProvisioningUri("Pishkhan Admin", staff.Username, secret));
        }

        // The panel signs in in two steps: password first, then the code. A missing code is that first step, not a failed attempt.
        if (string.IsNullOrEmpty(command.TotpCode))
        {
            return Error.Unauthorized("auth.totp.required");
        }

        if (!VerifyTotp(staff, command.TotpCode, now))
        {
            return await FailAsync(staff, "staff.login_failed", "auth.totp.invalid", cancellationToken);
        }

        staff.RecordSuccess();
        return await StartSessionAsync(staff, "staff.login", cancellationToken);
    }

    public async Task<Result<StaffLoginResponse>> HandleAsync(TotpEnrollRequest command, CancellationToken cancellationToken)
    {
        var db0 = redis.GetDatabase();
        var staffId = await db0.StringGetDeleteAsync(keys.For("staff", "enroll:" + Hash(command.EnrollmentToken)));
        if (staffId.IsNullOrEmpty)
        {
            return Error.Unauthorized("auth.totp.enrollment_expired");
        }

        var staff = await db.StaffUsers.FirstAsync(s => s.Id == Guid.Parse(staffId.ToString()), cancellationToken);
        var secret = Convert.FromBase64String(secrets.Unprotect(staff.TotpSecretEnc!, TotpPurpose));
        if (!Totp.Verify(secret, command.Code, clock.UtcNow, out var step))
        {
            return Error.Unauthorized("auth.totp.invalid");
        }

        staff.EnableTotp(step);
        Audit(Actor(staff), "staff.totp_enrolled", "staff", staff.Id);
        return await StartSessionAsync(staff, "staff.login", cancellationToken);
    }

    public async Task<Result<StaffLoginResponse>> HandleAsync(StaffRefreshRequest command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var session = await db.StaffSessions.FirstOrDefaultAsync(s => s.RefreshHash == Hash(command.RefreshToken), cancellationToken);
        if (session is null)
        {
            return Error.Unauthorized("auth.refresh.invalid");
        }

        if (session.RevokedReason == "rotated")
        {
            await RevokeFamilyAsync(session.FamilyId, "reuse_detected", cancellationToken);
            return Error.Unauthorized("auth.refresh.reused");
        }

        if (!session.IsActive(now))
        {
            return Error.Unauthorized("auth.refresh.expired");
        }

        var staff = await db.StaffUsers.FirstAsync(s => s.Id == session.StaffId, cancellationToken);
        if (staff.Status != StaffStatus.Active)
        {
            return Error.Unauthorized("auth.login.locked");
        }

        var refresh = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var next = session.Rotate(Hash(refresh), options.Value.IdleTimeout, caller.IpAddress, caller.UserAgent, now);
        db.StaffSessions.Add(next);
        await db.SaveChangesAsync(cancellationToken);
        return Issue(staff, next, refresh, stepUp: false);
    }

    public async Task<Result<Unit>> HandleAsync(StaffLogoutCommand command, CancellationToken cancellationToken)
    {
        await RevokeFamilyAsync(caller.SessionId, "logout", cancellationToken);
        Audit(await CallerActorAsync(cancellationToken), "staff.logout", "staff_session", caller.SessionId);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }

    public async Task<Result<StepUpResponse>> HandleAsync(StepUpRequest command, CancellationToken cancellationToken)
    {
        var staff = await db.StaffUsers.FirstAsync(s => s.Id == caller.UserId, cancellationToken);
        if (Hasher.VerifyHashedPassword(staff, staff.PasswordHash, command.Password) == PasswordVerificationResult.Failed || !VerifyTotp(staff, command.TotpCode, clock.UtcNow))
        {
            await FailAsync(staff, "staff.step_up_failed", "auth.step_up.invalid", cancellationToken);
            return Error.Unauthorized("auth.step_up.invalid");
        }

        Audit(Actor(staff), "staff.step_up", "staff_session", caller.SessionId);
        await db.SaveChangesAsync(cancellationToken);
        var token = tokens.CreateAccessToken(AuthSchemes.Staff, staff.Id, caller.SessionId, Claims(staff, stepUp: true), options.Value.StepUpLifetime);
        return new StepUpResponse(token.Token, token.ExpiresAt);
    }

    public async Task<Result<StaffMeResponse>> HandleAsync(StaffMeQuery query, CancellationToken cancellationToken)
    {
        var staff = await db.StaffUsers.AsNoTracking().FirstAsync(s => s.Id == caller.UserId, cancellationToken);
        return new StaffMeResponse(staff.Id, staff.Username, staff.Roles);
    }

    public async Task<Result<IReadOnlyList<StaffSummary>>> HandleAsync(ListStaffQuery query, CancellationToken cancellationToken) =>
        (await db.StaffUsers.AsNoTracking().OrderBy(s => s.Username).ToListAsync(cancellationToken)).Select(ToSummary).ToList();

    public async Task<Result<StaffSummary>> HandleAsync(CreateStaffRequest command, CancellationToken cancellationToken)
    {
        if (await db.StaffUsers.AnyAsync(s => s.Username == command.Username, cancellationToken))
        {
            return Error.Conflict("backoffice.staff.username_taken");
        }

        var id = await CreateAsync(command.Username, command.Password, command.Roles, await CallerActorAsync(cancellationToken), cancellationToken);
        return ToSummary(await db.StaffUsers.AsNoTracking().FirstAsync(s => s.Id == id, cancellationToken));
    }

    public Task<Guid> CreateAsync(string username, string password, IReadOnlyList<string> roles, CancellationToken cancellationToken) =>
        CreateAsync(username, password, roles, AuditActor.System, cancellationToken);

    private async Task<Guid> CreateAsync(string username, string password, IReadOnlyList<string> roles, AuditActor actor, CancellationToken cancellationToken)
    {
        var staff = StaffUser.Create(username, string.Empty, roles, clock.UtcNow);
        staff.SetPasswordHash(Hasher.HashPassword(staff, password));
        db.StaffUsers.Add(staff);
        Audit(actor, "staff.create", "staff", staff.Id, after: new { staff.Username, staff.Roles });
        await db.SaveChangesAsync(cancellationToken);
        return staff.Id;
    }

    private bool VerifyTotp(StaffUser staff, string? code, DateTimeOffset now)
    {
        if (staff.TotpSecretEnc is null || string.IsNullOrEmpty(code))
        {
            return false;
        }

        var secret = Convert.FromBase64String(secrets.Unprotect(staff.TotpSecretEnc, TotpPurpose));
        return Totp.Verify(secret, code, now, out var step) && staff.AcceptTotpStep(step);
    }

    /// <summary>Counts a failed attempt (5 lock the account for 15 minutes) and audits it.</summary>
    private async Task<Result<StaffLoginResponse>> FailAsync(StaffUser staff, string action, string code, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        staff.RecordFailure(now);
        Audit(Actor(staff), action, "staff", staff.Id, reason: staff.IsLocked(now) ? code + "; auth.login.locked" : code);
        await db.SaveChangesAsync(cancellationToken);
        return Error.Unauthorized(code);
    }

    private async Task<StaffLoginResponse> StartSessionAsync(StaffUser staff, string action, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var refresh = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var session = StaffSession.Start(staff.Id, Hash(refresh), o.IdleTimeout, o.AbsoluteTimeout, caller.IpAddress, caller.UserAgent, clock.UtcNow);
        db.StaffSessions.Add(session);
        Audit(Actor(staff), action, "staff_session", session.FamilyId);
        await db.SaveChangesAsync(cancellationToken);
        return Issue(staff, session, refresh, stepUp: false);
    }

    private StaffLoginResponse Issue(StaffUser staff, StaffSession session, string refresh, bool stepUp)
    {
        var access = tokens.CreateAccessToken(AuthSchemes.Staff, staff.Id, session.FamilyId, Claims(staff, stepUp));
        return new StaffLoginResponse("ok", access.Token, access.ExpiresAt, refresh, session.IdleExpiresAt);
    }

    private IEnumerable<Claim> Claims(StaffUser staff, bool stepUp)
    {
        foreach (var role in staff.Roles)
        {
            yield return new Claim(PishkhanClaims.Roles, role);
        }

        yield return new Claim(PishkhanClaims.AuthMethods, stepUp ? "pwd+otp+stepup" : "pwd+otp");
        if (stepUp)
        {
            yield return new Claim("step_up_at", clock.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64);
        }
    }

    private async Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await db.StaffSessions.Where(s => s.FamilyId == familyId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, reason).SetProperty(x => x.UpdatedAt, now), cancellationToken);
        await revocations.RevokeAsync(AuthSchemes.Staff, familyId.ToString(), tokens.AccessTokenLifetime(AuthSchemes.Staff) + TimeSpan.FromMinutes(1), cancellationToken);
    }

    private AuditActor Actor(StaffUser staff) => AuditActor.Staff(staff.Id, staff.Username, caller.IpAddress);

    private async Task<AuditActor> CallerActorAsync(CancellationToken cancellationToken)
    {
        var username = await db.StaffUsers.AsNoTracking().Where(s => s.Id == caller.UserId).Select(s => s.Username).FirstOrDefaultAsync(cancellationToken);
        return AuditActor.Staff(caller.UserId, username, caller.IpAddress);
    }

    /// <summary>Staff actions are platform-level entries (no workspace). Written by the next SaveChanges, with the change itself.</summary>
    private void Audit(AuditActor actor, string action, string entityType, Guid? entityId, object? after = null, string? reason = null)
    {
        var entry = AuditEntries.Create(actor, action, entityType, entityId?.ToString(), null, clock.UtcNow, after: after, reason: reason);
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
    }

    private static string Clip(string value) => value.Length <= 100 ? value : value[..100];

    private static StaffSummary ToSummary(StaffUser s) => new(s.Id, s.Username, s.Roles, s.Status.ToString().ToLowerInvariant(), s.TotpEnabled, s.CreatedAt);

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
