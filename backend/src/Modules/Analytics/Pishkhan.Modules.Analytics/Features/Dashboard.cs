using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Modules.Analytics.Contracts;
using Pishkhan.Modules.Analytics.Domain;
using Pishkhan.Modules.Analytics.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Analytics.Features;

/// <summary>
/// Counts over the period. Replies are messages sent: by a rule (or flow), by the AI agent, by a team member.
/// <c>automatedShare</c> is the part of replies sent by rules and the AI (null when nothing was sent).
/// </summary>
public sealed record DashboardTotals(
    int MessagesReceived, int NewConversations, int RepliesByRule, int RepliesByAi, int RepliesByHuman, int Handoffs, double? AutomatedShare);

/// <summary>One Tehran day of the period.</summary>
public sealed record DashboardDay(DateOnly Day, int MessagesReceived, int NewConversations, int RepliesByRule, int RepliesByAi, int RepliesByHuman, int Handoffs);

/// <summary>
/// The latest stretch of closed business hours in the last 24 hours (still going when <c>ongoing</c>), and what happened in it.
/// <c>text</c> is a ready Persian sentence for the summary card.
/// </summary>
public sealed record OvernightSummary(DateTimeOffset From, DateTimeOffset To, bool Ongoing, int MessagesReceived, int AutomatedReplies, int Handoffs, string Text);

/// <summary>The inbox right now: conversations open, handed off and waiting for a person (with the oldest wait), with unread messages.</summary>
public sealed record InboxNow(int Open, int HandedOff, int Unread, DateTimeOffset? OldestHandoffAt);

/// <summary><c>healthy</c> is false when the account needs the owner (reconnect, expiring token).</summary>
public sealed record ChannelHealth(Guid Id, string Kind, string? Username, string Status, bool Healthy);

public sealed record DashboardResponse(
    string Range, DateTimeOffset From, DateTimeOffset To, DashboardTotals Totals, IReadOnlyList<DashboardDay> Days, OvernightSummary? Overnight,
    InboxNow Inbox, IReadOnlyList<ChannelHealth> Channels);

/// <summary>The cached part of the dashboard: everything read from the rollups.</summary>
internal sealed record DashboardRollup(DateTimeOffset From, DashboardTotals Totals, IReadOnlyList<DashboardDay> Days, OvernightSummary? Overnight);

internal static class Dashboard
{
    /// <summary>Plan 7.3: <c>dash:{ws}:{range}</c>, 5 minutes.</summary>
    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(1) };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}").WithTags("Dashboard")
            .MapGet("/dashboard", GetAsync)
            .RequireWorkspacePermission(Permissions.AnalyticsRead)
            .WithSummary("Dashboard: activity over `range` (today, 7d, 30d), the overnight summary, the inbox now and channel health")
            .WithDescription("Period counts come from hourly rollups and may lag a few minutes; `inbox` and `channels` are live. Days are Tehran days.")
            .Produces<DashboardResponse>()
            .WithStandardProblems();
    }

    private static async Task<IResult> GetAsync(
        Guid workspaceId, string? range, AnalyticsDbContext db, HybridCache cache, IWorkspaceQueries workspaces, IConversationQueries conversations,
        IChannelDirectory channels, IClock clock, CancellationToken ct)
    {
        range ??= DashboardRanges.Today;
        if (!DashboardRanges.All.Contains(range))
        {
            return Error.Validation("analytics.range_invalid", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["range"] = ["analytics.range_invalid"] }).ToProblem();
        }

        var now = clock.UtcNow;
        var hours = await workspaces.GetBusinessHoursAsync(workspaceId, ct);
        var rollup = await cache.GetOrCreateAsync(
            $"dash:{workspaceId:N}:{range}",
            async token => await RollupAsync(db, range, hours, now, token),
            Entry,
            [CacheTags.Workspace(workspaceId)],
            ct);

        var counts = await conversations.CountsAsync(ct);
        var accounts = await channels.ListAsync(ct);
        return Results.Ok(new DashboardResponse(
            range, rollup.From, now, rollup.Totals, rollup.Days, rollup.Overnight,
            new InboxNow(counts.Open, counts.HandedOff, counts.Unread, counts.OldestHandoffAt),
            [.. accounts.Select(a => new ChannelHealth(a.Id, a.Kind, a.Username, a.Status, a.Status == ChannelStatuses.Active))]));
    }

    internal static async Task<DashboardRollup> RollupAsync(AnalyticsDbContext db, string range, BusinessHours hours, DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TehranTime.ToTehran(now).DateTime);
        var firstDay = today.AddDays(1 - DashboardRanges.Days(range));
        var from = TehranMidnight(firstDay);
        var since = from < now.AddHours(-24) ? from : HourlyMetrics.HourOf(now.AddHours(-24));
        var rows = await db.Hourly.AsNoTracking().Where(h => h.Hour >= since).ToListAsync(ct);

        var period = rows.Where(r => r.Hour >= from).ToList();
        var days = Enumerable.Range(0, DashboardRanges.Days(range))
            .Select(i => firstDay.AddDays(i))
            .Select(day =>
            {
                var of = period.Where(r => DateOnly.FromDateTime(TehranTime.ToTehran(r.Hour).DateTime) == day).ToList();
                return new DashboardDay(
                    day, of.Sum(r => r.MessagesReceived), of.Sum(r => r.NewConversations), of.Sum(r => r.RepliesByRule), of.Sum(r => r.RepliesByAi),
                    of.Sum(r => r.RepliesByHuman), of.Sum(r => r.Handoffs));
            })
            .ToList();
        int rule = period.Sum(r => r.RepliesByRule), ai = period.Sum(r => r.RepliesByAi), human = period.Sum(r => r.RepliesByHuman);
        var totals = new DashboardTotals(
            period.Sum(r => r.MessagesReceived), period.Sum(r => r.NewConversations), rule, ai, human, period.Sum(r => r.Handoffs),
            rule + ai + human == 0 ? null : Math.Round((double)(rule + ai) / (rule + ai + human), 3));
        return new DashboardRollup(from, totals, days, Overnight(rows, hours, now));
    }

    /// <summary>
    /// The latest run of hours when the business was closed, within the last 24 hours. An hour counts as closed when it is closed at its
    /// start. Null when the business was open all day (or has no hours set and so is always open).
    /// </summary>
    internal static OvernightSummary? Overnight(IReadOnlyList<HourlyMetrics> rows, BusinessHours hours, DateTimeOffset now)
    {
        var current = HourlyMetrics.HourOf(now);
        var oldest = current.AddHours(-23);
        var end = current;
        while (end >= oldest && hours.IsOpen(end))
        {
            end = end.AddHours(-1);
        }

        if (end < oldest)
        {
            return null;
        }

        var ongoing = end == current;
        var start = end;
        while (start.AddHours(-1) >= oldest && !hours.IsOpen(start.AddHours(-1)))
        {
            start = start.AddHours(-1);
        }

        var to = ongoing ? now : end.AddHours(1);
        var within = rows.Where(r => r.Hour >= start && r.Hour <= end).ToList();
        int received = within.Sum(r => r.MessagesReceived), automated = within.Sum(r => r.RepliesByRule + r.RepliesByAi), handoffs = within.Sum(r => r.Handoffs);
        return new OvernightSummary(start, to, ongoing, received, automated, handoffs, Text(start, to, ongoing, received, automated, handoffs));
    }

    /// <summary>For example «از ۲۱:۰۰ تا ۰۹:۰۰: ۴۷ پیام دریافت شد، ۴۲ پاسخ خودکار داده شد و ۳ گفتگو به تیم سپرده شد.»</summary>
    internal static string Text(DateTimeOffset from, DateTimeOffset to, bool ongoing, int received, int automated, int handoffs)
    {
        static string Clock(DateTimeOffset at) => TehranTime.ToTehran(at).ToString("HH:mm", CultureInfo.InvariantCulture);
        var period = ongoing ? $"از {Clock(from)} تا الان" : $"از {Clock(from)} تا {Clock(to)}";
        if (received == 0 && automated == 0 && handoffs == 0)
        {
            return PersianDigits.ToPersian($"{period}: پیامی نرسید.");
        }

        var parts = new List<string> { $"{received} پیام دریافت شد", $"{automated} پاسخ خودکار داده شد" };
        if (handoffs > 0)
        {
            parts.Add($"{handoffs} گفتگو به تیم سپرده شد");
        }

        var sentence = parts.Count == 2 ? $"{parts[0]} و {parts[1]}" : $"{parts[0]}، {parts[1]} و {parts[2]}";
        return PersianDigits.ToPersian($"{period}: {sentence}.");
    }

    private static DateTimeOffset TehranMidnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TehranTime.Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
