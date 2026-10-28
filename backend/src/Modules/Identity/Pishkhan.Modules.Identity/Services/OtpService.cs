using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.RateLimiting;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Sms;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using StackExchange.Redis;

namespace Pishkhan.Modules.Identity.Services;

/// <summary>
/// Phone OTPs (backend plan 3.4): 6 digits, stored only as an HMAC in the state Redis with a 2-minute TTL, at most 5 attempts,
/// 60 s resend cooldown, at most 5 per phone and 20 per IP per hour.
/// </summary>
internal sealed class OtpService(
    [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis,
    RedisKeys keys,
    RedisSlidingWindow limits,
    IBlindIndexer indexer,
    ISmsSender sms,
    IOptions<IdentityOptions> options,
    IOptions<SmsOptions> smsOptions)
{
    public const string LoginPurpose = "login";

    public async Task<Result<(int ExpiresIn, int ResendAfter)>> SendAsync(PhoneNumber phone, string phoneBidx, string purpose, string? ip, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var db = redis.GetDatabase();

        if (!(await limits.TryHitAsync($"otp:ip:{ip ?? "unknown"}", o.OtpPerIpPerHour, TimeSpan.FromHours(1))).Allowed)
        {
            return Error.RateLimited("auth.otp.too_many_requests");
        }

        if (!await db.StringSetAsync(keys.For("otp", $"cooldown:{purpose}:{phoneBidx}"), "1", o.OtpResendCooldown, When.NotExists))
        {
            return Error.RateLimited("auth.otp.resend_too_soon");
        }

        if (!(await limits.TryHitAsync($"otp:phone:{phoneBidx}", o.OtpPerPhonePerHour, TimeSpan.FromHours(1))).Allowed)
        {
            return Error.RateLimited("auth.otp.too_many_requests");
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000", CultureInfo.InvariantCulture);
        var key = OtpKey(purpose, phoneBidx);
        await db.HashSetAsync(key, [new HashEntry("h", HashCode(code, phoneBidx)), new HashEntry("a", 0)]);
        await db.KeyExpireAsync(key, o.OtpLifetime);

        var text = $"کد ورود شما به پیشخوان: {PersianDigits.ToPersian(code)}\nاین کد را به کسی ندهید.";
        await sms.SendAsync(new SmsMessage(phone, text, smsOptions.Value.OtpTemplate, [code]), cancellationToken);
        return ((int)o.OtpLifetime.TotalSeconds, (int)o.OtpResendCooldown.TotalSeconds);
    }

    public async Task<Result> VerifyAsync(string phoneBidx, string purpose, string code, CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        var key = OtpKey(purpose, phoneBidx);
        var stored = await db.HashGetAsync(key, "h");
        if (stored.IsNullOrEmpty)
        {
            return Error.Unauthorized("auth.otp.expired");
        }

        var attempts = await db.HashIncrementAsync(key, "a");
        if (attempts > options.Value.OtpMaxAttempts)
        {
            await db.KeyDeleteAsync(key);
            return Error.RateLimited("auth.otp.too_many_attempts");
        }

        var expected = Encoding.ASCII.GetBytes(stored.ToString());
        var actual = Encoding.ASCII.GetBytes(HashCode(PersianDigits.ToLatin(code.Trim()), phoneBidx));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return Error.Unauthorized("auth.otp.invalid");
        }

        // Single use: delete; if another request deleted it first, this one loses.
        return await db.KeyDeleteAsync(key) ? Result.Success() : Error.Unauthorized("auth.otp.expired");
    }

    private string OtpKey(string purpose, string phoneBidx) => keys.For("otp", $"{purpose}:{phoneBidx}");

    private string HashCode(string code, string phoneBidx) => indexer.Compute(code, "otp:" + phoneBidx);
}
