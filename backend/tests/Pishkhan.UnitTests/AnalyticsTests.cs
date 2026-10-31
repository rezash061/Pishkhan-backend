using Pishkhan.Modules.Analytics.Domain;
using Pishkhan.Modules.Analytics.Features;
using Pishkhan.Modules.Tenancy.Contracts;

namespace Pishkhan.UnitTests;

public class AnalyticsTests
{
    /// <summary>Every day 09:00-21:00 Tehran (UTC+03:30), so hourly buckets start at :30 local time.</summary>
    private static readonly BusinessHours Daily = new(
        [.. Enum.GetValues<DayOfWeek>().Select(d => new DayHours(d, [new TimeRange(new TimeOnly(9, 0), new TimeOnly(21, 0))]))], []);

    private static HourlyMetrics Row(string hour, int received, int rule = 0, int ai = 0, int human = 0, int handoffs = 0) => new()
    {
        Hour = DateTimeOffset.Parse(hour, System.Globalization.CultureInfo.InvariantCulture),
        MessagesReceived = received,
        RepliesByRule = rule,
        RepliesByAi = ai,
        RepliesByHuman = human,
        Handoffs = handoffs,
    };

    [Fact]
    public void Hours_are_truncated_in_utc() =>
        HourlyMetrics.HourOf(new DateTimeOffset(2026, 10, 31, 10, 45, 12, TimeSpan.FromHours(3.5))).ShouldBe(new DateTimeOffset(2026, 10, 31, 7, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Overnight_is_the_last_closed_stretch_and_ignores_open_hours()
    {
        var now = new DateTimeOffset(2026, 10, 31, 6, 30, 0, TimeSpan.Zero); // 10:00 Tehran, open
        IReadOnlyList<HourlyMetrics> rows =
        [
            Row("2026-10-30T16:00:00Z", 4, rule: 4), // 19:30, open
            Row("2026-10-30T20:00:00Z", 5, rule: 3, ai: 1, handoffs: 1), // 23:30, closed
            Row("2026-10-31T04:00:00Z", 2, ai: 2), // 07:30, closed
            Row("2026-10-31T06:00:00Z", 9, human: 3), // 09:30, open
        ];

        var summary = Dashboard.Overnight(rows, Daily, now)!;
        summary.From.ShouldBe(new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero));
        summary.To.ShouldBe(new DateTimeOffset(2026, 10, 31, 6, 0, 0, TimeSpan.Zero));
        summary.Ongoing.ShouldBeFalse();
        (summary.MessagesReceived, summary.AutomatedReplies, summary.Handoffs).ShouldBe((7, 6, 1));
        summary.Text.ShouldBe("از ۲۱:۳۰ تا ۰۹:۳۰: ۷ پیام دریافت شد، ۶ پاسخ خودکار داده شد و ۱ گفتگو به تیم سپرده شد.");
    }

    [Fact]
    public void Overnight_runs_until_now_while_closed_and_is_null_when_always_open()
    {
        var night = new DateTimeOffset(2026, 10, 30, 20, 10, 0, TimeSpan.Zero); // 23:40 Tehran
        var summary = Dashboard.Overnight([], Daily, night)!;
        summary.Ongoing.ShouldBeTrue();
        summary.From.ShouldBe(new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero));
        summary.Text.ShouldBe("از ۲۱:۳۰ تا الان: پیامی نرسید.");

        Dashboard.Overnight([], BusinessHours.AlwaysOpen, night).ShouldBeNull();
    }
}
