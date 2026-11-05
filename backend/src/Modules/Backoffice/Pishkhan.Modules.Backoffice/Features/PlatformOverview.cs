using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pishkhan.Infrastructure.Antivirus;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Backoffice.Features;

/// <summary>One check. <c>ms</c>: how long it took; <c>detail</c>: a short English note (broker count, signature date, lag).</summary>
public sealed record HealthCheckEntry(string Status, long Ms, string? Detail);

/// <summary>Every dependency of the platform. <c>status</c> is the worst check (Healthy, Degraded or Unhealthy).</summary>
public sealed record PlatformHealthReport(string Status, IReadOnlyDictionary<string, HealthCheckEntry> Checks);

/// <summary>Home dashboard numbers. Null when the source cannot be read (for example Kafka down). Money in Rial.</summary>
public sealed record DashboardKpis(
    int? ActiveWorkspaces, int? SignupsToday, int? Signups7d, int? PayingWorkspaces, long? MrrRial, int? AiRepliesToday, decimal? HandoffRateToday,
    int? QuarantinedFiles, int? OpenDeadLetters, long? MaxConsumerLag, int? ChannelsNeedingReauth, string? InstagramStatus);

/// <summary>
/// Something staff should look at. <c>code</c>: instagram_down, kafka_unavailable, consumer_lag, dead_letters, workers_down, quarantine,
/// channels_reauth, ai_failures. <c>severity</c>: warning or danger. <c>count</c> and <c>subject</c> (a group or job) when they apply.
/// </summary>
public sealed record DashboardAlert(string Code, string Severity, long? Count, string? Subject);

public sealed record DashboardResponse(DateTimeOffset At, DashboardKpis Kpis, IReadOnlyList<DashboardAlert> Alerts);

/// <summary>
/// AdminApi: the health page across every dependency and the home dashboard (AD-0.4, AD-1.10). The dashboard reads other modules' tables with
/// read-only SQL: it is reporting, and each number fails on its own (null) rather than the whole page.
/// </summary>
internal static partial class PlatformOverviewEndpoints
{
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support, StaffRoles.AiOps, StaffRoles.Finance, StaffRoles.ContentEditor, StaffRoles.ReadOnly];

    [GeneratedRegex(@"/\s*\d+\s*/\s*(?<date>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ClamDate();

    public static void Map(IEndpointRouteBuilder app)
    {
        var read = app.MapAdminApi().WithTags("Platform").RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));

        read.MapGet("/platform/health", HealthAsync)
            .WithSummary("Health of every dependency: PostgreSQL, Redis, Kafka (with lag), object storage, ClamAV (with signature age), workers, Instagram, AI")
            .WithDescription("Every staff role. Same shape as /health/ready, with a detail per check. Checks run in parallel with a 5 second limit each.")
            .Produces<PlatformHealthReport>();

        read.MapGet("/dashboard", DashboardAsync)
            .WithSummary("Home dashboard KPIs and alerts")
            .WithDescription("Every staff role. \"Today\" is the Tehran calendar day.")
            .Produces<DashboardResponse>();
    }

    private static async Task<IResult> HealthAsync(IServiceProvider services, NpgsqlDataSource dataSource, KafkaInspector inspector, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var checks = new List<Task<(string Name, HealthCheckEntry Entry)>>
        {
            Run("postgres", () => FromHealthCheck(ActivatorUtilities.CreateInstance<PostgresHealthCheck>(services), ct)),
            Run("redis-cache", () => FromHealthCheck(ActivatorUtilities.CreateInstance<RedisHealthCheck>(services, RedisInstances.Cache), ct, HealthStatus.Degraded)),
            Run("redis-state", () => FromHealthCheck(ActivatorUtilities.CreateInstance<RedisHealthCheck>(services, RedisInstances.State), ct)),
            Run("kafka", () => KafkaAsync(inspector, ct)),
            Run("workers", () => WorkersAsync(dataSource, now, ct)),
        };
        if (services.GetService<IObjectStorage>() is { } storage)
        {
            checks.Add(Run("storage", () => FromHealthCheck(new StorageHealthCheck(storage), ct)));
        }

        if (services.GetService<IAntivirusScanner>() is { } scanner)
        {
            checks.Add(Run("clamav", () => ClamAvAsync(scanner, now, ct)));
        }

        if (services.GetService<IChannelHealth>() is { } channels)
        {
            checks.Add(Run("instagram", async () =>
            {
                var status = await channels.GetInstagramAsync(ct);
                return status.IsDown ? ("Degraded", $"down since {status.Since:O} ({status.Mode})") : ("Healthy", status.Mode);
            }));
        }

        var results = (await Task.WhenAll(checks)).ToList();
        foreach (var contributor in services.GetServices<IPlatformHealthContributor>())
        {
            var watch = Stopwatch.StartNew();
            try
            {
                foreach (var c in await contributor.CheckAsync(ct))
                {
                    results.Add((c.Name, new HealthCheckEntry(c.Status, watch.ElapsedMilliseconds, c.Detail)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add((contributor.GetType().Name, new HealthCheckEntry("Unhealthy", watch.ElapsedMilliseconds, ex.GetType().Name)));
            }
        }

        var worst = results.Select(r => r.Entry.Status).OrderByDescending(Rank).FirstOrDefault() ?? "Healthy";
        return Results.Ok(new PlatformHealthReport(worst, results.ToDictionary(r => r.Name, r => r.Entry)));
    }

    private static int Rank(string status) => status switch { "Unhealthy" => 2, "Degraded" => 1, _ => 0 };

    private static async Task<(string Name, HealthCheckEntry Entry)> Run(string name, Func<Task<(string Status, string? Detail)>> check)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var (status, detail) = await Task.Run(check).WaitAsync(TimeSpan.FromSeconds(5));
            return (name, new HealthCheckEntry(status, watch.ElapsedMilliseconds, detail));
        }
        catch (TimeoutException)
        {
            return (name, new HealthCheckEntry("Unhealthy", watch.ElapsedMilliseconds, "timed out"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (name, new HealthCheckEntry("Unhealthy", watch.ElapsedMilliseconds, ex.GetType().Name));
        }
    }

    private static async Task<(string, string?)> FromHealthCheck(IHealthCheck check, CancellationToken ct, HealthStatus failure = HealthStatus.Unhealthy)
    {
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("check", check, failure, null) }, ct);
        var status = result.Status == HealthStatus.Unhealthy ? failure : result.Status;
        return (status.ToString(), result.Description);
    }

    private static async Task<(string, string?)> KafkaAsync(KafkaInspector inspector, CancellationToken ct)
    {
        var overview = await inspector.GetAsync(ct);
        var worst = overview.Groups.OrderByDescending(g => g.Lag).FirstOrDefault();
        var missing = overview.Topics.Count(t => !t.Exists);
        if (missing > 0)
        {
            return ("Degraded", $"{missing} topic(s) missing");
        }

        return worst is { Lag: > 1000 } ? ("Degraded", $"{worst.Group} lag {worst.Lag}") : ("Healthy", worst is null ? "no consumer groups" : $"max lag {worst.Lag}");
    }

    private static async Task<(string, string?)> WorkersAsync(NpgsqlDataSource dataSource, DateTimeOffset now, CancellationToken ct)
    {
        var alive = await AliveWorkersAsync(dataSource, now, ct);
        return alive switch
        {
            null => ("Healthy", "no scheduler tables"),
            0 => ("Unhealthy", "no worker replica checked in"),
            _ => ("Healthy", $"{alive} replica(s)"),
        };
    }

    /// <summary>Workers replicas that checked in recently, or null when the Quartz tables do not exist (tests, a fresh database).</summary>
    internal static async Task<int?> AliveWorkersAsync(NpgsqlDataSource dataSource, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var rows = await connection.QueryAsync<(long Checkin, long Interval)>(new CommandDefinition(
                "SELECT last_checkin_time, checkin_interval FROM platform.qrtz_scheduler_state", cancellationToken: ct));
            return rows.Count(r => now - new DateTimeOffset(r.Checkin, TimeSpan.Zero) < TimeSpan.FromMilliseconds(r.Interval) * 2 + TimeSpan.FromSeconds(30));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    private static async Task<(string, string?)> ClamAvAsync(IAntivirusScanner scanner, DateTimeOffset now, CancellationToken ct)
    {
        if (!await scanner.PingAsync(ct))
        {
            return ("Degraded", "unreachable; files stay pending_scan");
        }

        var version = await scanner.GetVersionAsync(ct);
        var age = SignatureAge(version, now);
        return age is { } a && a > TimeSpan.FromDays(2) ? ("Degraded", $"signatures {a.TotalDays:0} days old ({version})") : ("Healthy", version);
    }

    /// <summary>clamd answers <c>ClamAV 1.4.1/27450/Tue Nov  4 09:31:02 2026</c>; the date is when the signatures were built (UTC).</summary>
    internal static TimeSpan? SignatureAge(string? version, DateTimeOffset now)
    {
        if (version is null || ClamDate().Match(version) is not { Success: true } match)
        {
            return null;
        }

        // The weekday is dropped: parsing checks it against the date, and the date alone is what counts.
        var parts = Regex.Replace(match.Groups["date"].Value.Trim(), @"\s+", " ").Split(' ');
        var text = string.Join(' ', parts.Length == 5 ? parts[1..] : parts);
        return DateTime.TryParseExact(text, "MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? now - new DateTimeOffset(at, TimeSpan.Zero)
            : null;
    }

    private static async Task<IResult> DashboardAsync(
        NpgsqlDataSource dataSource, KafkaInspector inspector, IServiceProvider services, IClock clock, ILoggerFactory logs, CancellationToken ct)
    {
        var log = logs.CreateLogger(typeof(PlatformOverviewEndpoints));
        var now = clock.UtcNow;
        var tehran = TehranTime.ToTehran(now);
        var today = new DateTimeOffset(tehran.Date, tehran.Offset).ToUniversalTime();
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        async Task<T?> Read<T>(string sql, object? args = null)
            where T : struct
        {
            try
            {
                return await connection.ExecuteScalarAsync<T?>(new CommandDefinition(sql, args, cancellationToken: ct));
            }
            catch (PostgresException ex)
            {
                log.LogWarning(ex, "Dashboard query failed: {Sql}", sql);
                return null;
            }
        }

        var active = await Read<int>("SELECT count(*)::int FROM tenancy.workspaces WHERE status = 'Active' AND deleted_at IS NULL");
        var signupsToday = await Read<int>("SELECT count(*)::int FROM tenancy.workspaces WHERE created_at >= @today", new { today });
        var signups7d = await Read<int>("SELECT count(*)::int FROM tenancy.workspaces WHERE created_at >= @from", new { from = today.AddDays(-6) });
        var paying = await Read<int>(
            "SELECT count(*)::int FROM billing.subscriptions s JOIN billing.plans p ON p.id = s.plan_id WHERE s.status = 'Active' AND NOT s.trial AND NOT p.is_free");
        var mrr = await Read<long>(
            """
            SELECT coalesce(sum(CASE WHEN s.cycle = 'annual' AND p.annual_price IS NOT NULL THEN p.annual_price / 12 ELSE p.monthly_price END), 0)::bigint
            FROM billing.subscriptions s JOIN billing.plans p ON p.id = s.plan_id
            WHERE s.status = 'Active' AND NOT s.trial AND NOT p.is_free
            """);
        var replies = await Read<int>("SELECT count(*)::int FROM agent.ai_traces WHERE created_at >= @today", new { today });
        var handedOff = await Read<int>("SELECT count(*)::int FROM agent.ai_traces WHERE created_at >= @today AND outcome = 'handed_off'", new { today });
        var quarantined = await Read<int>("SELECT count(*)::int FROM files.files WHERE status IN ('infected', 'scan_failed')");
        var deadLetters = await Read<int>("SELECT count(*)::int FROM platform.dead_letters WHERE status = 'open'");
        var reauth = await Read<int>("SELECT count(*)::int FROM channels.channel_accounts WHERE status = 'reauth_required'");
        var aiFailedHour = await Read<int>("SELECT count(*)::int FROM agent.ai_traces WHERE created_at >= @from AND outcome = 'failed'", new { from = now.AddHours(-1) });
        var infectedDay = await Read<int>("SELECT count(*)::int FROM files.files WHERE status = 'infected' AND updated_at >= @from", new { from = now.AddDays(-1) });
        var oldestDeadLetter = await Read<DateTime>("SELECT min(failed_at) FROM platform.dead_letters WHERE status = 'open'");

        KafkaOverview? kafka = null;
        try
        {
            // The admin client blocks while the brokers do not answer; the dashboard waits 5 seconds at most.
            kafka = await Task.Run(() => inspector.GetAsync(ct), ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (Exception ex) when (ex is Confluent.Kafka.KafkaException or TimeoutException)
        {
            log.LogWarning(ex, "Kafka unavailable for the dashboard");
        }

        string? instagram = null;
        if (services.GetService<IChannelHealth>() is { } channels)
        {
            try
            {
                instagram = (await channels.GetInstagramAsync(ct)).Status;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Instagram status unavailable for the dashboard");
            }
        }

        var alerts = new List<DashboardAlert>();
        if (instagram == "down")
        {
            alerts.Add(new DashboardAlert("instagram_down", "danger", null, null));
        }

        if (kafka is null)
        {
            alerts.Add(new DashboardAlert("kafka_unavailable", "danger", null, null));
        }
        else
        {
            alerts.AddRange(kafka.Groups.Where(g => g.Lag > 1000).OrderByDescending(g => g.Lag).Select(g => new DashboardAlert("consumer_lag", g.Lag > 10_000 ? "danger" : "warning", g.Lag, g.Group)));
        }

        if (deadLetters > 0)
        {
            var old = oldestDeadLetter is { } o && now - new DateTimeOffset(DateTime.SpecifyKind(o, DateTimeKind.Utc)) > TimeSpan.FromHours(1);
            alerts.Add(new DashboardAlert("dead_letters", deadLetters > 50 || old ? "danger" : "warning", deadLetters, null));
        }

        if (await AliveWorkersAsync(dataSource, now, ct) == 0)
        {
            alerts.Add(new DashboardAlert("workers_down", "danger", null, null));
        }

        if (infectedDay > 0)
        {
            alerts.Add(new DashboardAlert("quarantine", "warning", infectedDay, null));
        }

        if (reauth > 0)
        {
            alerts.Add(new DashboardAlert("channels_reauth", reauth > 10 ? "danger" : "warning", reauth, null));
        }

        if (aiFailedHour >= 5)
        {
            alerts.Add(new DashboardAlert("ai_failures", aiFailedHour >= 50 ? "danger" : "warning", aiFailedHour, null));
        }

        var kpis = new DashboardKpis(
            active, signupsToday, signups7d, paying, mrr, replies,
            replies is > 0 && handedOff is { } h ? Math.Round((decimal)h / replies.Value, 4) : null,
            quarantined, deadLetters, kafka?.Groups.Select(g => g.Lag).DefaultIfEmpty(0).Max(), reauth, instagram);
        return Results.Ok(new DashboardResponse(now, kpis, [.. alerts.OrderBy(a => a.Severity == "danger" ? 0 : 1)]));
    }
}
