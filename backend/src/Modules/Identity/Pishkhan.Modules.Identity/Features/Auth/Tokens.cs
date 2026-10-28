using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.RateLimiting;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Identity.Domain;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.Modules.Identity.Services;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Identity.Features.Auth;

public sealed record RefreshRequest(string RefreshToken);

public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public sealed record RealtimeTokenResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record PasswordLoginRequest(string Phone, string Password, string? TotpCode);

internal sealed class RefreshValidator : AbstractValidator<RefreshRequest>
{
    public RefreshValidator() => RuleFor(x => x.RefreshToken).NotEmpty().WithErrorCode("validation.required").MaximumLength(200).WithErrorCode("validation.too_long");
}

internal sealed class PasswordLoginValidator : AbstractValidator<PasswordLoginRequest>
{
    public PasswordLoginValidator()
    {
        RuleFor(x => x.Phone).NotEmpty().WithErrorCode("validation.required").Must(p => PhoneNumber.TryParse(p, out _)).WithErrorCode("validation.phone.invalid");
        RuleFor(x => x.Password).NotEmpty().WithErrorCode("validation.required").MaximumLength(200).WithErrorCode("validation.too_long");
        RuleFor(x => x.TotpCode).MaximumLength(10).WithErrorCode("validation.too_long");
    }
}

internal sealed class RefreshHandler(SessionService sessions, ICurrentUser caller) : ICommandHandler<RefreshRequest, TokenResponse>
{
    public async Task<Result<TokenResponse>> HandleAsync(RefreshRequest command, CancellationToken cancellationToken)
    {
        var result = await sessions.RefreshAsync(command.RefreshToken, caller.IpAddress, caller.UserAgent, cancellationToken);
        return result.IsSuccess
            ? new TokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiresAt, result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt)
            : result.Error!;
    }
}

public sealed record LogoutCommand;

internal sealed class LogoutHandler(SessionService sessions, ICurrentUser caller) : ICommandHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        await sessions.RevokeFamilyAsync(caller.UserId, caller.SessionId, Session.ReasonLogout, cancellationToken);
        return Unit.Value;
    }
}

/// <summary>Optional password login (users who set a password). TOTP is required when enabled.</summary>
internal sealed class PasswordLoginHandler(
    IdentityDbContext db,
    PhoneProtection phones,
    SessionService sessions,
    ISecretProtector secrets,
    RedisSlidingWindow limits,
    ICurrentUser caller,
    IClock clock,
    Microsoft.Extensions.Options.IOptions<IdentityOptions> options) : ICommandHandler<PasswordLoginRequest, LoginResponse>
{
    private static readonly PasswordHasher<User> Hasher = new();

    public async Task<Result<LoginResponse>> HandleAsync(PasswordLoginRequest command, CancellationToken cancellationToken)
    {
        var bidx = phones.BlindIndex(PhoneNumber.Parse(command.Phone));
        if (!(await limits.TryHitAsync($"pwd:{bidx}", options.Value.PasswordFailuresPerHour, TimeSpan.FromHours(1))).Allowed)
        {
            return Error.RateLimited("auth.login.too_many_attempts");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.PhoneBidx == bidx, cancellationToken);
        if (user?.PasswordHash is null || Hasher.VerifyHashedPassword(user, user.PasswordHash, command.Password) == PasswordVerificationResult.Failed)
        {
            return Error.Unauthorized("auth.login.invalid_credentials");
        }

        if (user.Status != UserStatus.Active)
        {
            return Error.Forbidden("auth.user.suspended");
        }

        var amr = "pwd";
        if (user.TotpEnabled)
        {
            if (string.IsNullOrEmpty(command.TotpCode))
            {
                return Error.Unauthorized("auth.totp.required");
            }

            var secret = Convert.FromBase64String(secrets.Unprotect(user.TotpSecretEnc!, TotpPurpose.User));
            if (!Totp.Verify(secret, command.TotpCode, clock.UtcNow, out var step) || !user.AcceptTotpStep(step))
            {
                return Error.Unauthorized("auth.totp.invalid");
            }

            amr = "pwd+otp";
        }

        var tokens = await sessions.StartAsync(user, amr, caller.IpAddress, caller.UserAgent, cancellationToken);
        return new LoginResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.RefreshTokenExpiresAt, user.Id, false);
    }
}

internal static class TotpPurpose
{
    public const string User = "identity.totp";
}
