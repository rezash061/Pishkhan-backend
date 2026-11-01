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

    /// <summary>
    /// Anonymous public endpoints, per IP. Reads (GET, cached and cheap; the website's server fetches them for every visitor during a
    /// build) get <see cref="RateLimitOptions.PublicReadsPerMinute"/>; writes (lead forms, token refresh, OTP request) the stricter
    /// <see cref="RateLimitOptions.PublicPerMinute"/>.
    /// </summary>
    public const string Public = "per-ip-public";

    /// <summary>Auth endpoints (OTP verify, password login), per IP. Per-phone and per-account limits are enforced in the handlers.</summary>
    public const string Auth = "per-ip-auth";

    /// <summary>Authenticated back-office calls, per staff member.</summary>
    public const string Staff = "per-staff";

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

/// <summary>
/// API rate limits (backend plan 3.6), tuned for the closed beta (BE-1.15). Iranian mobile carriers put many subscribers behind one
/// carrier-grade NAT address, so per-IP limits are set for a shared address and the per-phone and per-account limits in the handlers do
/// the fine-grained work.
/// </summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>Panel calls per user. A busy inbox polls nothing (SignalR pushes), so 600 leaves room for bulk edits and exports.</summary>
    public int UserPerMinute { get; set; } = 600;

    /// <summary>Panel calls per workspace, all members together, counted once membership is checked (so outsiders cannot use it up).</summary>
    public int WorkspacePerMinute { get; set; } = 3_000;

    /// <summary>Anonymous writes per IP: lead forms, token refresh, OTP requests.</summary>
    public int PublicPerMinute { get; set; } = 60;

    /// <summary>Anonymous reads per IP: plans, CMS pages, help search.</summary>
    public int PublicReadsPerMinute { get; set; } = 600;

    /// <summary>Sign-ins per IP (OTP verify, password, support session, staff login). Per-phone attempts are capped in the handlers.</summary>
    public int AuthPerHour { get; set; } = 120;

    public int UploadPerMinute { get; set; } = 30;

    /// <summary>Back-office calls per staff member.</summary>
    public int StaffPerMinute { get; set; } = 600;
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddPishkhanRateLimiting(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddSingleton<RedisSlidingWindow>();
        services.AddSingleton<RedisTokenBucket>();
        var o = Microsoft.Extensions.Configuration.ConfigurationBinder.Get<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section)) ?? new RateLimitOptions();
        services.AddSingleton(o);
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
            r.AddPolicy(RateLimitPolicies.Public, ctx => HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)
                ? Partition(ctx, "public-read", IpKey(ctx), o.PublicReadsPerMinute, TimeSpan.FromMinutes(1))
                : Partition(ctx, "public", IpKey(ctx), o.PublicPerMinute, TimeSpan.FromMinutes(1)));
            r.AddPolicy(RateLimitPolicies.Auth, ctx => Partition(ctx, "auth", IpKey(ctx), o.AuthPerHour, TimeSpan.FromHours(1)));
            r.AddPolicy(RateLimitPolicies.Staff, ctx => Partition(ctx, "staff", UserKey(ctx), o.StaffPerMinute, TimeSpan.FromMinutes(1)));
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(HttpContext ctx, string scope, string id, int limit, TimeSpan period) =>
        RateLimitPartition.Get($"{scope}:{id}", key => new RedisSlidingWindowLimiter(ctx.RequestServices.GetRequiredService<RedisSlidingWindow>(), "api:" + key, limit, period));

    private static string UserKey(HttpContext ctx) =>
        ctx.User.FindFirst(PishkhanClaims.Subject)?.Value ?? IpKey(ctx);

    /// <summary>The per-workspace limit, checked by <c>RequireWorkspacePermission</c> after the membership check. Null when allowed.</summary>
    public static async Task<IResult?> CheckWorkspaceLimitAsync(HttpContext ctx, Guid workspaceId)
    {
        var o = ctx.RequestServices.GetRequiredService<RateLimitOptions>();
        var decision = await ctx.RequestServices.GetRequiredService<RedisSlidingWindow>().TryHitAsync($"api:workspace:{workspaceId:N}", o.WorkspacePerMinute, TimeSpan.FromMinutes(1));
        if (decision.Allowed)
        {
            return null;
        }

        ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: 429, type: ProblemTypes.RateLimited, title: "common.rate_limited");
    }

    private static string IpKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
