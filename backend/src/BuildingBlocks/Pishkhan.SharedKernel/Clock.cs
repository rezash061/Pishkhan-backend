namespace Pishkhan.SharedKernel;

/// <summary>All code reads the time through this interface so tests can control it.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class TehranTime
{
    /// <summary>Business-hours logic uses Asia/Tehran. Storage is always UTC.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");

    public static DateTimeOffset ToTehran(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);
}
