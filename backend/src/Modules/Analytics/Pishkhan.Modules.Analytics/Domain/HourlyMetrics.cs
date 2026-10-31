using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Analytics.Domain;

/// <summary>
/// One workspace hour (UTC, truncated to the hour) of inbox activity: the rollup the dashboard reads (backend plan 9.17).
/// Rows are only ever incremented, by the analytics handlers, once per event.
/// </summary>
public sealed class HourlyMetrics : ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public DateTimeOffset Hour { get; init; }

    public int MessagesReceived { get; init; }

    public int NewConversations { get; init; }

    public int RepliesByRule { get; init; }

    public int RepliesByAi { get; init; }

    public int RepliesByHuman { get; init; }

    public int Handoffs { get; init; }

    public static DateTimeOffset HourOf(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}

/// <summary>Increments for one hour row.</summary>
public sealed record MetricsDelta(int MessagesReceived = 0, int NewConversations = 0, int RepliesByRule = 0, int RepliesByAi = 0, int RepliesByHuman = 0, int Handoffs = 0);
