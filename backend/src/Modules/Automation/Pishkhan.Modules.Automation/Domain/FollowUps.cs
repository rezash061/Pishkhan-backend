using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Automation.Domain;

public static class FollowUpStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";

    /// <summary>Dropped: the customer replied or bought, the owner cancelled it, a newer follow-up from the same source replaced it, or the conversation was handed off.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Due after the 24-hour window closed: Meta does not allow the message, so it was not sent.</summary>
    public const string Skipped = "skipped";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pending, Sent, Cancelled, Skipped };
}

public static class FollowUpReasons
{
    public const string Replied = "replied";
    public const string Purchased = "purchased";
    public const string Owner = "owner";
    public const string Replaced = "replaced";
    public const string HandedOff = "handed_off";
    public const string WindowClosed = "window_closed";
    public const string ConversationGone = "conversation_gone";
}

/// <summary>Rules for the <c>schedule_followup</c> action (BE-1.8).</summary>
public static class FollowUps
{
    public const int MinDelaySeconds = 60;

    /// <summary>23 hours: a follow-up must fit inside Meta's 24-hour messaging window, which starts at the customer's last message.</summary>
    public const int MaxDelaySeconds = 23 * 3600;

    public const string TimerKind = "automation.followup";

    /// <summary>Null when the action is valid; otherwise the field and the error key.</summary>
    public static (string Field, string Code)? Validate(RuleAction action)
    {
        if (action.DelaySeconds is not { } delay || delay < MinDelaySeconds || delay > MaxDelaySeconds)
        {
            return ("delaySeconds", "automation.followup_delay_invalid");
        }

        if (action.Message is null)
        {
            return ("message", "validation.message.empty");
        }

        return MessageValidator.Validate(action.Message) is { } code ? ("message", code) : null;
    }

    /// <summary>The dedupe key: one pending follow-up per conversation and source (a rule or a flow), the newest replacing the older one.</summary>
    public static string Key(Guid conversationId, string source) => $"followup:{conversationId:N}:{source}";
}

/// <summary>
/// A message scheduled by a <c>schedule_followup</c> action, for example «هنوز به این مدل فکر می‌کنی؟» an hour after a product link. It
/// waits in <c>platform.timers</c>; at fire time it is sent only if still pending and the 24-hour window is open.
/// </summary>
public sealed class FollowUp : Entity<Guid>, ITenantOwned
{
    private FollowUp()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid ChannelAccountId { get; private set; }

    public string ChannelKind { get; private set; } = string.Empty;

    public string ExternalUserId { get; private set; } = string.Empty;

    /// <summary><c>rule:{id}</c> or <c>flow:{id}</c>.</summary>
    public string Source { get; private set; } = string.Empty;

    public Guid? RuleId { get; private set; }

    public Guid? FlowId { get; private set; }

    /// <summary>Rendered when scheduled (variables such as the customer's name are filled in then).</summary>
    public OutboundContent Message { get; private set; } = new(string.Empty);

    public DateTimeOffset DueAt { get; private set; }

    public bool CancelOnReply { get; private set; }

    public bool CancelOnPurchase { get; private set; }

    public string Status { get; private set; } = FollowUpStatuses.Pending;

    public string? Reason { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string Key => FollowUps.Key(ConversationId, Source);

    public static FollowUp Schedule(
        Guid workspaceId, Guid conversationId, Guid channelAccountId, string channelKind, string externalUserId, string source, Guid? ruleId, Guid? flowId,
        OutboundContent message, RuleAction action, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        ConversationId = conversationId,
        ChannelAccountId = channelAccountId,
        ChannelKind = channelKind,
        ExternalUserId = externalUserId,
        Source = source,
        RuleId = ruleId,
        FlowId = flowId,
        Message = message,
        DueAt = now.AddSeconds(action.DelaySeconds ?? FollowUps.MinDelaySeconds),
        CancelOnReply = action.CancelOnReply ?? true,
        CancelOnPurchase = action.CancelOnPurchase ?? true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>False when it is no longer pending (nothing changes).</summary>
    public bool Complete(string status, string? reason, DateTimeOffset now)
    {
        if (Status != FollowUpStatuses.Pending)
        {
            return false;
        }

        Status = status;
        Reason = reason;
        CompletedAt = now;
        UpdatedAt = now;
        return true;
    }
}
