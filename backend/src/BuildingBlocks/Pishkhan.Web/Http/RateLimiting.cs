using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.RateLimiting;
using Pishkhan.Web.Auth;

namespace Pishkhan.Web.Http;

public static class RateLimitPolicies
{
    /// <summary>Authenticated panel calls, per user.</summary>
    public const string User = "per-user";

    /// <summary>Anonymous public endpoints, per IP.</summary>
    public const string Public = "per-ip-public";

    /// <summary>Auth endpoints (OTP request, login), per IP. Per-phone limits are enforced in the handler.</summary>
    public const string Auth = "per-ip-auth";

    /// <summary>Uploads, per user.</summary>
    public const string Upload = "per-user-upload";
}

/// <summary>An ASP.NET Core rate limiter backed by the Redis sliding window, so limits hold across replicas.</summary>
public sealed class RedisSlidingWindowLimiter(RedisSlidingWindow window, string key, int limit, TimeSpan period) : RateLimiter
{
    private long _lastUsedTicks = DateTime.UtcNow.Ticks;

    public override TimeSpan? IdleDuration => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastUsedTicks));

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => AcquireAsyncCore(permitCount, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
        var decision = await window.TryHitAsync(key, limit, period);
        return new Lease(decision.Allowed, decision.RetryAfter);
    }

    private sealed class Lease(bool acquired, TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => acquired ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (!acquired && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    public int UserPerMinute { get; set; } = 600;

    public int PublicPerMinute { get; set; } = 60;

    public int AuthPerHour { get; set; } = 20;

    public int UploadPerMinute { get; set; } = 30;
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddPishkhanRateLimiting(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddSingleton<RedisSlidingWindow>();
        services.AddSingleton<RedisTokenBucket>();
        var o = Microsoft.Extensions.Configuration.ConfigurationBinder.Get<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section)) ?? new RateLimitOptions();
        services.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            r.OnRejected = async (ctx, token) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await Results.Problem(statusCode: 429, type: ProblemTypes.RateLimited, title: "common.rate_limited").ExecuteAsync(ctx.HttpContext);
            };
            r.AddPolicy(RateLimitPolicies.User, ctx => Partition(ctx, "user", UserKey(ctx), o.UserPerMinute, TimeSpan.FromMinutes(1)));
            r.AddPolicy(RateLimitPolicies.Upload, ctx => Partition(ctx, "upload", UserKey(ctx), o.UploadPerMinute, TimeSpan.FromMinutes(1)));
            r.AddPolicy(RateLimitPolicies.Public, ctx => Partition(ctx, "public", IpKey(ctx), o.PublicPerMinute, TimeSpan.FromMinutes(1)));
            r.AddPolicy(RateLimitPolicies.Auth, ctx => Partition(ctx, "auth", IpKey(ctx), o.AuthPerHour, TimeSpan.FromHours(1)));
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(HttpContext ctx, string scope, string id, int limit, TimeSpan period) =>
        RateLimitPartition.Get($"{scope}:{id}", key => new RedisSlidingWindowLimiter(ctx.RequestServices.GetRequiredService<RedisSlidingWindow>(), "api:" + key, limit, period));

    private static string UserKey(HttpContext ctx) =>
        ctx.User.FindFirst(PishkhanClaims.Subject)?.Value ?? IpKey(ctx);

    private static string IpKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
