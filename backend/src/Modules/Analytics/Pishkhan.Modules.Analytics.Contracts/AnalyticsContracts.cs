namespace Pishkhan.Modules.Analytics.Contracts;

/// <summary>Dashboard periods, in Tehran days: today so far, and the last 7 or 30 days including today.</summary>
public static class DashboardRanges
{
    public const string Today = "today";
    public const string Week = "7d";
    public const string Month = "30d";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Today, Week, Month };

    public static int Days(string range) => range switch
    {
        Week => 7,
        Month => 30,
        _ => 1,
    };
}
