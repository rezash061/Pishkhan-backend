using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Tenancy.Contracts;

/// <summary>Weekly opening hours in Asia/Tehran, plus closed dates (holidays). The one source of business hours: the inbox, the "outside business hours" trigger and flow conditions all read it.</summary>
public sealed record BusinessHours(IReadOnlyList<DayHours> Days, IReadOnlyList<DateOnly> ClosedDates)
{
    /// <summary>Saturday to Wednesday 9-21, Thursday 9-14, Friday closed: a common Iranian shop schedule.</summary>
    public static readonly BusinessHours Default = new(
    [
        new(DayOfWeek.Saturday, [new TimeRange(new TimeOnly(9, 0), new TimeOnly(21, 0))]),
        new(DayOfWeek.Sunday, [new TimeRange(new TimeOnly(9, 0), new TimeOnly(21, 0))]),
        new(DayOfWeek.Monday, [new TimeRange(new TimeOnly(9, 0), new TimeOnly(21, 0))]),
        new(DayOfWeek.Tuesday, [new TimeRange(new TimeOnly(9, 0), new TimeOnly(21, 0))]),
        new(DayOfWeek.Wednesday, [new TimeRange(new TimeOnly(9, 0), new TimeOnly(21, 0))]),
        new(DayOfWeek.Thursday, [new TimeRange(new TimeOnly(9, 0), new TimeOnly(14, 0))]),
        new(DayOfWeek.Friday, []),
    ], []);

    /// <summary>Every day, all day (tests and dry runs that do not care about hours).</summary>
    public static readonly BusinessHours AlwaysOpen = new(
        [.. Enum.GetValues<DayOfWeek>().Select(d => new DayHours(d, [new TimeRange(TimeOnly.MinValue, TimeOnly.MaxValue)]))], []);

    public bool IsOpen(DateTimeOffset utc)
    {
        var local = TehranTime.ToTehran(utc);
        if (ClosedDates.Contains(DateOnly.FromDateTime(local.DateTime)))
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(local.DateTime);
        return Days.FirstOrDefault(d => d.Day == local.DayOfWeek)?.Ranges.Any(r => time >= r.From && time < r.To) ?? false;
    }
}

public sealed record DayHours(DayOfWeek Day, IReadOnlyList<TimeRange> Ranges);

public sealed record TimeRange(TimeOnly From, TimeOnly To);
