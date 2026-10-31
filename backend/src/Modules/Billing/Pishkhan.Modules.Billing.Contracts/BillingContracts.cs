using Pishkhan.Contracts;

namespace Pishkhan.Modules.Billing.Contracts;

/// <summary>What a plan limits per billing period (backend plan 9.13). A missing or null limit means unlimited.</summary>
public static class BillingMeters
{
    public const string AiConversations = "ai_conversations";
    public const string AiMessages = "ai_messages";
    public const string ChannelAccounts = "channel_accounts";
    public const string Seats = "seats";
    public const string ReceiptVerifications = "receipt_verifications";
    public const string Sms = "sms";

    public static readonly IReadOnlyList<string> All = [AiConversations, AiMessages, ChannelAccounts, Seats, ReceiptVerifications, Sms];

    /// <summary>Meters counted per period from <see cref="UsageRecorded"/> events. The others are current counts (accounts, seats).</summary>
    public static readonly IReadOnlyList<string> Counted = [AiConversations, AiMessages, ReceiptVerifications, Sms];

    /// <summary>Meters counted once per subject and period (an AI conversation is one conversation however many replies it gets).</summary>
    public static readonly IReadOnlyList<string> Distinct = [AiConversations];
}

/// <summary>
/// Something billable happened in a workspace. Producers publish it through their outbox, keyed by workspace id. For a meter in
/// <see cref="BillingMeters.Distinct"/>, <c>SubjectId</c> (for example the conversation id) is required and counts once per period.
/// </summary>
[IntegrationEvent(Topics.UsageRecorded)]
public sealed record UsageRecorded(Guid WorkspaceId, string Meter, int Quantity, DateTimeOffset OccurredAt, Guid? SubjectId = null) : IIntegrationEvent;

/// <summary>
/// What a workspace may use now. <c>Source</c>: <c>subscription</c> (a granted plan or trial), <c>free</c> (no subscription, the
/// published free plan) or <c>none</c> (no free plan is published: nothing is limited).
/// </summary>
public sealed record Entitlements(
    Guid WorkspaceId, string Source, Guid? PlanId, string? PlanCode, string? PlanName, int? PlanVersion, bool Trial, DateTimeOffset? EndsAt,
    Dictionary<string, int?> Limits)
{
    /// <summary>The limit of <paramref name="meter"/>, or null when unlimited.</summary>
    public int? Limit(string meter) => Limits.GetValueOrDefault(meter);

    /// <summary>True when using <paramref name="total"/> of <paramref name="meter"/> stays within the limit.</summary>
    public bool Allows(string meter, long total) => Limit(meter) is not { } limit || total <= limit;
}

/// <summary>In-process entitlement checks for other modules. A check that fails answers 402 with <c>Error.PlanLimit(meter)</c>.</summary>
public interface IEntitlements
{
    /// <summary>The workspace's current plan limits (cached; cleared when its subscription or the plans change).</summary>
    Task<Entitlements> GetAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>Usage of a counted meter in the current billing period.</summary>
    Task<long> GetUsageAsync(Guid workspaceId, string meter, CancellationToken cancellationToken);
}
