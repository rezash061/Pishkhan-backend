using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.Modules.Identity.Domain;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.Modules.Identity.Services;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Identity.Features.Auth;

/// <param name="Phone">Iranian mobile number in any common form; Persian digits are accepted.</param>
/// <param name="Altcha">ALTCHA proof-of-work payload from the frontend widget.</param>
public sealed record RequestOtpRequest(string Phone, string? Altcha);

public sealed record RequestOtpResponse(int ExpiresInSeconds, int ResendAfterSeconds);

public sealed record VerifyOtpRequest(string Phone, string Code);

public sealed record LoginResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, Guid UserId, bool IsNewUser);

internal sealed class RequestOtpValidator : AbstractValidator<RequestOtpRequest>
{
    public RequestOtpValidator()
    {
        RuleFor(x => x.Phone).NotEmpty().WithErrorCode("validation.required").Must(p => PhoneNumber.TryParse(p, out _)).WithErrorCode("validation.phone.invalid");
        RuleFor(x => x.Altcha).MaximumLength(2000).WithErrorCode("validation.too_long");
    }
}

internal sealed class VerifyOtpValidator : AbstractValidator<VerifyOtpRequest>
{
    public VerifyOtpValidator()
    {
        RuleFor(x => x.Phone).NotEmpty().WithErrorCode("validation.required").Must(p => PhoneNumber.TryParse(p, out _)).WithErrorCode("validation.phone.invalid");
        RuleFor(x => x.Code).NotEmpty().WithErrorCode("validation.required").Must(c => Persian.PersianDigits.ToLatin(c.Trim()) is { Length: 6 } d && d.All(char.IsAsciiDigit)).WithErrorCode("validation.otp.format");
    }
}

internal sealed class RequestOtpHandler(OtpService otp, PhoneProtection phones, AltchaVerifier altcha, ICurrentUser caller) : ICommandHandler<RequestOtpRequest, RequestOtpResponse>
{
    public async Task<Result<RequestOtpResponse>> HandleAsync(RequestOtpRequest command, CancellationToken cancellationToken)
    {
        if (!await altcha.VerifyAsync(command.Altcha))
        {
            return Error.Validation("validation.altcha.invalid", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["altcha"] = ["validation.altcha.invalid"] });
        }

        var phone = PhoneNumber.Parse(command.Phone);
        var sent = await otp.SendAsync(phone, phones.BlindIndex(phone), OtpService.LoginPurpose, caller.IpAddress, cancellationToken);
        return sent.IsSuccess ? new RequestOtpResponse(sent.Value.ExpiresIn, sent.Value.ResendAfter) : sent.Error!;
    }
}

/// <summary>Verifies the OTP, registers the user on first login, and starts a session.</summary>
internal sealed class VerifyOtpHandler(IdentityDbContext db, OtpService otp, PhoneProtection phones, SessionService sessions, ICurrentUser caller, IClock clock)
    : ICommandHandler<VerifyOtpRequest, LoginResponse>
{
    public async Task<Result<LoginResponse>> HandleAsync(VerifyOtpRequest command, CancellationToken cancellationToken)
    {
        var phone = PhoneNumber.Parse(command.Phone);
        var bidx = phones.BlindIndex(phone);
        var verified = await otp.VerifyAsync(bidx, OtpService.LoginPurpose, command.Code, cancellationToken);
        if (!verified.IsSuccess)
        {
            return verified.Error!;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.PhoneBidx == bidx, cancellationToken);
        var isNew = user is null;
        if (user is null)
        {
            user = User.Register(phones.Encrypt(phone), bidx, clock.UtcNow);
            db.Users.Add(user);
            db.Publish(new UserRegistered(user.Id, user.CreatedAt), key: user.Id.ToString());
        }
        else if (user.Status != UserStatus.Active)
        {
            return Error.Forbidden("auth.user.suspended");
        }

        var tokens = await sessions.StartAsync(user, "otp", caller.IpAddress, caller.UserAgent, cancellationToken);
        return new LoginResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.RefreshTokenExpiresAt, user.Id, isNew);
    }
}
