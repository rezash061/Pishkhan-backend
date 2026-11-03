using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.SharedKernel;
using Quartz;
using StackExchange.Redis;

namespace Pishkhan.Modules.Channels.Services;

/// <summary>
/// Counts Graph API calls per minute in the state Redis: successes, and failures that mean Instagram itself is failing (unreachable or
/// Meta's transient errors). Token, permission and rate-limit errors are the account's own and are not counted. Never throws: a Redis
/// problem must not fail an Instagram call.
/// </summary>
internal sealed class InstagramHealth([FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys, IClock clock, ILogger<InstagramHealth> logger)
{
    private static readonly TimeSpan Keep = TimeSpan.FromHours(2);

    public async Task RecordAsync(bool ok)
    {
        try
        {
            var key = Key(clock.UtcNow);
            var db = redis.GetDatabase();
            await db.HashIncrementAsync(key, ok ? "ok" : "failed");
            await db.KeyExpireAsync(key, Keep);
        }
        catch (RedisException e)
        {
            logger.LogDebug(e, "Instagram health not recorded");
        }
    }

    /// <summary>The calls of the last <paramref name="minutes"/> whole minutes, the current one included.</summary>
    public async Task<CallCounts> CountAsync(int minutes)
    {
        var now = clock.UtcNow;
        var db = redis.GetDatabase();
        long ok = 0, failed = 0;
        for (var i = 0; i < minutes; i++)
        {
            var values = await db.HashGetAsync(Key(now.AddMinutes(-i)), ["ok", "failed"]);
            ok += values[0].TryParse(out long o) ? o : 0;
            failed += values[1].TryParse(out long f) ? f : 0;
        }

        return new CallCounts(ok, failed);
    }

    private string Key(DateTimeOffset at) => keys.For("ig-health", at.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture));
}

/// <summary>Reads and changes the platform's view of Instagram.</summary>
internal sealed class ChannelHealth(ChannelsDbContext db, IClock clock) : IChannelHealth
{
    public async Task<ChannelOutageStatus> GetInstagramAsync(CancellationToken cancellationToken)
    {
        var row = await db.PlatformStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.Kind == ChannelKinds.Instagram, cancellationToken);
        return row is null
            ? new ChannelOutageStatus(ChannelKinds.Instagram, OutageStatuses.Up, OutageModes.Auto, clock.UtcNow)
            : new ChannelOutageStatus(row.Kind, row.Status, row.Mode, row.Since);
    }
}

/// <summary>
/// Platform group, every minute: applies the last minutes' counts (or the staff's forced mode) to the Instagram status. On a change it
/// publishes <see cref="ChannelOutageChanged"/> and tells every workspace with an active Instagram account, with a link to the campaign
/// page when Instagram went down.
/// </summary>
[DisallowConcurrentExecution]
public sealed class InstagramOutageJob(IServiceScopeFactory scopes) : IJob
{
    public const string CampaignLink = "/settings/outage";

    public async Task Execute(IJobExecutionContext context)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var health = scope.ServiceProvider.GetRequiredService<InstagramHealth>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<OutageOptions>>().Value;
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        var ct = context.CancellationToken;

        var status = await db.PlatformStatuses.FirstOrDefaultAsync(s => s.Kind == ChannelKinds.Instagram, ct);
        if (status is null)
        {
            status = PlatformChannelStatus.New(ChannelKinds.Instagram, now);
            db.PlatformStatuses.Add(status);
        }

        CallCounts counts;
        try
        {
            counts = await health.CountAsync(options.WindowMinutes);
        }
        catch (RedisException)
        {
            // Without counts the detector cannot tell; keep the status (a forced mode still applies on the next run).
            counts = default;
        }

        if (status.Evaluate(counts, options, now))
        {
            await AnnounceAsync(db, status, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    internal static async Task AnnounceAsync(ChannelsDbContext db, PlatformChannelStatus status, CancellationToken ct)
    {
        db.Publish(new ChannelOutageChanged(status.Kind, status.Status, status.Mode, status.Since), status.Kind, workspaceId: null);
        var workspaces = await db.Accounts.IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(a => a.Kind == ChannelKinds.Instagram && a.Status != ChannelStatuses.Disconnected)
            .Select(a => a.WorkspaceId).Distinct().ToListAsync(ct);
        var down = status.Status == OutageStatuses.Down;
        foreach (var workspaceId in workspaces)
        {
            db.Publish(
                new NotificationRequested(
                    workspaceId, null, "channel_outage",
                    down ? "اینستاگرام در دسترس نیست" : "اینستاگرام دوباره در دسترس است",
                    down
                        ? "پیام‌ها فعلاً به اینستاگرام نمی‌رسند. می‌توانید مشتریانی را که اجازه پیامک داده‌اند با یک پیامک به کانال دیگر دعوت کنید."
                        : "ارسال و دریافت پیام در اینستاگرام از سر گرفته شد.",
                    down ? CampaignLink : "/inbox"),
                workspaceId.ToString(),
                workspaceId: workspaceId);
        }
    }
}
