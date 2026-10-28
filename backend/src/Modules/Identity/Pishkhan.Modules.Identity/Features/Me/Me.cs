using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Identity.Domain;
using Pishkhan.Modules.Identity.Features.Auth;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.Modules.Identity.Services;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Identity.Features.Me;

public sealed record MeResponse(Guid Id, string? DisplayName, string Phone, bool HasPassword, bool TotpEnabled, DateTimeOffset CreatedAt);

public sealed record UpdateMeRequest(string? DisplayName);

public sealed record SetPasswordRequest(string NewPassword, string? CurrentPassword);

public sealed record SessionResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, string? Ip, string? UserAgent, bool Current);

public sealed record TotpSetupResponse(string Secret, string OtpAuthUri);

public sealed record TotpCodeRequest(string Code);

public sealed record GetMeQuery;

public sealed record ListSessionsQuery;

public sealed record RevokeSessionCommand(Guid SessionId);

public sealed record BeginTotpSetupCommand;

public sealed record EnableTotpCommand(string Code);

public sealed record DisableTotpCommand(string Code);

internal sealed class UpdateMeValidator : AbstractValidator<UpdateMeRequest>
{
    public UpdateMeValidator() => RuleFor(x => x.DisplayName).MaximumLength(100).WithErrorCode("validation.too_long");
}

internal sealed class SetPasswordValidator : AbstractValidator<SetPasswordRequest>
{
    public SetPasswordValidator() => RuleFor(x => x.NewPassword)
        .MinimumLength(10).WithErrorCode("validation.password.too_short")
        .MaximumLength(128).WithErrorCode("validation.too_long");
}

internal sealed class MeHandlers(IdentityDbContext db, PhoneProtection phones, ICurrentUser caller, SessionService sessions, ISecretProtector secrets, IClock clock) :
    IQueryHandler<GetMeQuery, MeResponse>,
    ICommandHandler<UpdateMeRequest, MeResponse>,
    ICommandHandler<SetPasswordRequest, Unit>,
    IQueryHandler<ListSessionsQuery, IReadOnlyList<SessionResponse>>,
    ICommandHandler<RevokeSessionCommand, Unit>,
    ICommandHandler<BeginTotpSetupCommand, TotpSetupResponse>,
    ICommandHandler<EnableTotpCommand, Unit>,
    ICommandHandler<DisableTotpCommand, Unit>
{
    private static readonly PasswordHasher<User> Hasher = new();

    public async Task<Result<MeResponse>> HandleAsync(GetMeQuery query, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == caller.UserId, cancellationToken);
        return user is null ? Error.NotFound() : ToResponse(user);
    }

    public async Task<Result<MeResponse>> HandleAsync(UpdateMeRequest command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == caller.UserId, cancellationToken);
        user.Rename(command.DisplayName);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(user);
    }

    public async Task<Result<Unit>> HandleAsync(SetPasswordRequest command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == caller.UserId, cancellationToken);
        if (user.PasswordHash is not null && (command.CurrentPassword is null || Hasher.VerifyHashedPassword(user, user.PasswordHash, command.CurrentPassword) == PasswordVerificationResult.Failed))
        {
            return Error.Unauthorized("auth.password.current_invalid");
        }

        user.SetPassword(Hasher.HashPassword(user, command.NewPassword));
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }

    public async Task<Result<IReadOnlyList<SessionResponse>>> HandleAsync(ListSessionsQuery query, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var rows = await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == caller.UserId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        var firstSeen = await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == caller.UserId)
            .GroupBy(s => s.FamilyId)
            .Select(g => new { FamilyId = g.Key, CreatedAt = g.Min(s => s.CreatedAt) })
            .ToDictionaryAsync(x => x.FamilyId, x => x.CreatedAt, cancellationToken);
        return rows.Select(s => new SessionResponse(s.FamilyId, firstSeen.GetValueOrDefault(s.FamilyId, s.CreatedAt), s.CreatedAt, s.Ip, s.UserAgent, s.FamilyId == caller.SessionId)).ToList();
    }

    public async Task<Result<Unit>> HandleAsync(RevokeSessionCommand command, CancellationToken cancellationToken) =>
        await sessions.RevokeFamilyAsync(caller.UserId, command.SessionId, Session.ReasonRevokedByUser, cancellationToken) ? Unit.Value : Error.NotFound();

    public async Task<Result<TotpSetupResponse>> HandleAsync(BeginTotpSetupCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == caller.UserId, cancellationToken);
        if (user.TotpEnabled)
        {
            return Error.Conflict("auth.totp.already_enabled");
        }

        var secret = Totp.NewSecret();
        user.BeginTotpSetup(secrets.Protect(Convert.ToBase64String(secret), TotpPurpose.User));
        await db.SaveChangesAsync(cancellationToken);
        return new TotpSetupResponse(Totp.ToBase32(secret), Totp.ProvisioningUri("Pishkhan", phones.Decrypt(user.PhoneE164Enc).National, secret));
    }

    public async Task<Result<Unit>> HandleAsync(EnableTotpCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == caller.UserId, cancellationToken);
        if (user.TotpSecretEnc is null)
        {
            return Error.Conflict("auth.totp.not_set_up");
        }

        var secret = Convert.FromBase64String(secrets.Unprotect(user.TotpSecretEnc, TotpPurpose.User));
        if (!Totp.Verify(secret, command.Code, clock.UtcNow, out var step))
        {
            return Error.Validation("auth.totp.invalid");
        }

        user.EnableTotp(step);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }

    public async Task<Result<Unit>> HandleAsync(DisableTotpCommand command, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == caller.UserId, cancellationToken);
        if (!user.TotpEnabled)
        {
            return Unit.Value;
        }

        var secret = Convert.FromBase64String(secrets.Unprotect(user.TotpSecretEnc!, TotpPurpose.User));
        if (!Totp.Verify(secret, command.Code, clock.UtcNow, out var step) || !user.AcceptTotpStep(step))
        {
            return Error.Validation("auth.totp.invalid");
        }

        user.DisableTotp();
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }

    private MeResponse ToResponse(User user) =>
        new(user.Id, user.DisplayName, phones.Decrypt(user.PhoneE164Enc).National, user.PasswordHash is not null, user.TotpEnabled, user.CreatedAt);
}
