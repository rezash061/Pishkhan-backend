using System.Globalization;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Automation.Domain;

/// <summary>
/// Opening hours per weekday (<c>sat</c> … <c>fri</c>), each a list of <c>"HH:mm-HH:mm"</c> ranges in <see cref="TimeZone"/>.
/// No hours configured means always open, so outside-hours rules never fire.
/// </summary>
public sealed record BusinessHours(string TimeZone, IReadOnlyDictionary<string, IReadOnlyList<string>> Days)
{
    public const string DefaultTimeZone = "Asia/Tehran";

    public static readonly BusinessHours AlwaysOpen = new(DefaultTimeZone, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    public static readonly IReadOnlyList<string> DayKeys = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    public bool IsConfigured => Days.Values.Any(r => r.Count > 0);

    public bool IsOpen(DateTimeOffset now)
    {
        if (!IsConfigured)
        {
            return true;
        }

        var local = TimeZoneInfo.ConvertTime(now, Zone(TimeZone));
        var day = DayKeys[(int)local.DayOfWeek];
        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        return Days.TryGetValue(day, out var ranges) && ranges.Any(r => TryParseRange(r, out var from, out var to) && time >= from && time < to);
    }

    public static bool TryParseRange(string range, out TimeOnly from, out TimeOnly to)
    {
        from = to = default;
        var parts = range.Split('-');
        return parts.Length == 2
            && TimeOnly.TryParseExact(parts[0].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
            && TimeOnly.TryParseExact(parts[1].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out to)
            && to > from;
    }

    /// <summary>The zone, or Tehran's fixed +03:30 when the host has no tz database (Iran has not observed DST since 2022).</summary>
    public static TimeZoneInfo Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromMinutes(210), id, id);
        }
    }
}

/// <summary>Per-workspace engine settings: whether the AI answers, business hours, and the fallback message.</summary>
public sealed class AutomationSettings : ITenantOwned
{
    public const int DefaultFallbackCooldownMinutes = 720;

    public Guid WorkspaceId { get; init; }

    /// <summary>The AI agent answers what no rule answered (BE-1.4 consumes the reply requests).</summary>
    public bool AiEnabled { get; set; }

    public BusinessHours BusinessHours { get; set; } = BusinessHours.AlwaysOpen;

    /// <summary>Sent when nothing else answered (and the AI is off or paused), at most once per conversation per cooldown.</summary>
    public string? FallbackText { get; set; }

    public int FallbackCooldownMinutes { get; set; } = DefaultFallbackCooldownMinutes;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static AutomationSettings Default(Guid workspaceId) => new() { WorkspaceId = workspaceId };

    public SettingsView ToView() => new(AiEnabled, BusinessHours, FallbackText, FallbackCooldownMinutes);
}

public sealed record SettingsView(bool AiEnabled, BusinessHours BusinessHours, string? FallbackText, int FallbackCooldownMinutes);

public static class CommentOutcomes
{
    public const string NoRule = "no_rule";
    public const string Replied = "replied";
    public const string Queued = "queued";

    /// <summary>Older than Meta's private reply limit: only a public reply was possible.</summary>
    public const string TooOld = "too_old";
}

public static class PublicReplyStatuses
{
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>
/// One row per comment we processed (backend plan 11): the dedupe for redelivered webhooks and backlog runs, and the record of
/// what was replied. With <c>conversations.private_reply_keys</c> it enforces Meta's one-private-reply rule.
/// </summary>
public sealed class CommentEvent : ITenantOwned
{
    /// <summary>Meta accepts a private reply only within 7 days of the comment.</summary>
    public static readonly TimeSpan PrivateReplyAge = TimeSpan.FromDays(7);

    public Guid ChannelAccountId { get; init; }

    public required string CommentId { get; init; }

    public Guid WorkspaceId { get; init; }

    public required string MediaId { get; init; }

    public required string FromExternalId { get; init; }

    public required string Kind { get; init; }

    public required string Source { get; init; }

    public DateTimeOffset CommentCreatedAt { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public Guid? RuleId { get; set; }

    public string Outcome { get; set; } = CommentOutcomes.NoRule;

    /// <summary>When the private reply is (or was) due; later than <see cref="ReceivedAt"/> when pacing queued it.</summary>
    public DateTimeOffset? PrivateReplyDueAt { get; private set; }

    public DateTimeOffset? PublicReplyDueAt { get; set; }

    public string? PublicReplyStatus { get; set; }

    public string? PublicReplyId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static bool CanPrivateReply(DateTimeOffset commentCreatedAt, DateTimeOffset now) => now - commentCreatedAt < PrivateReplyAge;

    /// <summary>Claims the one private reply. False when it was already claimed.</summary>
    public bool ClaimPrivateReply(DateTimeOffset dueAt)
    {
        if (PrivateReplyDueAt is not null)
        {
            return false;
        }

        PrivateReplyDueAt = dueAt;
        return true;
    }
}

public static class BacklogStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>"Reply to existing comments" for a post: pages through its comments and feeds them to the comment engine (deduped, paced).</summary>
public sealed class BacklogRun : Entity<Guid>, ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public Guid RuleId { get; init; }

    public Guid ChannelAccountId { get; init; }

    public required string MediaId { get; init; }

    public string Status { get; set; } = BacklogStatuses.Running;

    public int Pages { get; set; }

    public int CommentsSeen { get; set; }

    public int CommentsQueued { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public static BacklogRun Start(Guid workspaceId, Guid ruleId, Guid channelAccountId, string mediaId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        RuleId = ruleId,
        ChannelAccountId = channelAccountId,
        MediaId = mediaId,
        CreatedAt = now,
    };
}

/// <summary>Daily counters per rule for <c>GET /rules/{id}/stats</c>.</summary>
public sealed class RuleStatsDay : ITenantOwned
{
    public Guid RuleId { get; init; }

    public DateOnly Day { get; init; }

    public Guid WorkspaceId { get; init; }

    public int Fired { get; init; }

    public int PrivateReplies { get; init; }

    public int PublicReplies { get; init; }

    public int FlowsStarted { get; init; }
}

/// <summary>When a rule last fired for a conversation (cooldowns, the fallback message).</summary>
public sealed class RuleFiring
{
    public Guid RuleId { get; init; }

    public Guid ConversationId { get; init; }

    public DateTimeOffset LastFiredAt { get; init; }
}
