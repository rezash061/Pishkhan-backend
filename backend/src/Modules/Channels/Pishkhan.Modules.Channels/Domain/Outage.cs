namespace Pishkhan.Modules.Channels.Domain;

public static class OutageModes
{
    /// <summary>The detector decides from the Graph API's error rate.</summary>
    public const string Auto = "auto";

    /// <summary>Staff declared an outage (for example, filtering the detector cannot see from the edge zone).</summary>
    public const string ForcedDown = "forced_down";

    /// <summary>Staff declared Instagram working, overriding the detector (for example, after a false alarm).</summary>
    public const string ForcedUp = "forced_up";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Auto, ForcedDown, ForcedUp };
}

public static class OutageStatuses
{
    public const string Up = "up";
    public const string Down = "down";
}

/// <summary>How the detector decides (backend plan G11): thresholds over the last <see cref="WindowMinutes"/> minutes of Graph API calls.</summary>
public sealed class OutageOptions
{
    public const string Section = "Channels:Outage";

    public int WindowMinutes { get; set; } = 5;

    /// <summary>Failures needed before an outage can be declared: a quiet night must not flap on two errors.</summary>
    public int MinFailures { get; set; } = 20;

    /// <summary>The failure share at or above which Instagram counts as down.</summary>
    public double DownRatio { get; set; } = 0.5;

    /// <summary>The failure share below which Instagram counts as back, with at least <see cref="MinSuccesses"/> successful calls.</summary>
    public double UpRatio { get; set; } = 0.2;

    public int MinSuccesses { get; set; } = 5;

    /// <summary>An outage lasts at least this long before it can end, so a brief recovery does not flap the status.</summary>
    public int MinDownMinutes { get; set; } = 3;
}

/// <summary>Graph API calls of one window: successes, and failures that mean Instagram (not the account) is failing.</summary>
public readonly record struct CallCounts(long Ok, long Failed)
{
    public long Total => Ok + Failed;

    public double FailureRatio => Total == 0 ? 0 : (double)Failed / Total;
}

/// <summary>
/// The platform's view of Instagram, one row per channel kind (only <c>instagram</c> today). <see cref="Status"/> is what the panels show
/// and what allows a "move my customers" campaign.
/// </summary>
public sealed class PlatformChannelStatus
{
    public required string Kind { get; init; }

    public string Status { get; private set; } = OutageStatuses.Up;

    public string Mode { get; private set; } = OutageModes.Auto;

    /// <summary>When the current status began.</summary>
    public DateTimeOffset Since { get; private set; }

    public string? ModeReason { get; private set; }

    public Guid? ModeSetBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PlatformChannelStatus New(string kind, DateTimeOffset now) => new() { Kind = kind, Since = now, UpdatedAt = now };

    /// <summary>Applies the detector's counts (or the forced mode). Returns true when <see cref="Status"/> changed.</summary>
    public bool Evaluate(CallCounts counts, OutageOptions options, DateTimeOffset now)
    {
        var next = Mode switch
        {
            OutageModes.ForcedDown => OutageStatuses.Down,
            OutageModes.ForcedUp => OutageStatuses.Up,
            _ => Detect(counts, options, now),
        };
        UpdatedAt = now;
        if (next == Status)
        {
            return false;
        }

        Status = next;
        Since = now;
        return true;
    }

    public void SetMode(string mode, string? reason, Guid? staffId, DateTimeOffset now)
    {
        Mode = mode;
        ModeReason = reason;
        ModeSetBy = staffId;
        UpdatedAt = now;
    }

    private string Detect(CallCounts counts, OutageOptions options, DateTimeOffset now) => Status switch
    {
        OutageStatuses.Up when counts.Failed >= options.MinFailures && counts.FailureRatio >= options.DownRatio => OutageStatuses.Down,
        OutageStatuses.Down when now - Since >= TimeSpan.FromMinutes(options.MinDownMinutes) && counts.Ok >= options.MinSuccesses && counts.FailureRatio < options.UpRatio
            => OutageStatuses.Up,
        _ => Status,
    };
}
