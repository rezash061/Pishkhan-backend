using Pishkhan.Contracts;
using Pishkhan.Modules.Channels.Contracts;

namespace Pishkhan.Modules.Conversations.Contracts;

public static class ConversationStatuses
{
    public const string Open = "open";
    public const string Pending = "pending";
    public const string HandedOff = "handed_off";
    public const string Closed = "closed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Open, Pending, HandedOff, Closed };
}

public static class MessageDirections
{
    public const string Inbound = "inbound";
    public const string Outbound = "outbound";
}

public static class MessageStatuses
{
    public const string Received = "received";
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Read = "read";
    public const string Failed = "failed";
}

public static class HandledBy
{
    public const string Rule = "rule";
    public const string Ai = "ai";
    public const string Human = "human";
}

/// <summary>
/// A stored inbound message, keyed by conversation id so a conversation is processed in order. <see cref="Payload"/> is the postback or
/// quick reply payload; <see cref="IsNewConversation"/> is true for the first message of a new conversation (welcome rules).
/// </summary>
[IntegrationEvent(Topics.InboundMessageReceived)]
public sealed record InboundMessageReceived(
    Guid ConversationId,
    Guid MessageId,
    DateTimeOffset MessageCreatedAt,
    Guid ChannelAccountId,
    string ChannelKind,
    Guid ContactId,
    string Kind,
    string? Text,
    string? NormalizedText,
    int AttachmentCount,
    string? Payload = null,
    string? ReplyToStoryId = null,
    string? ExternalUserId = null,
    bool IsNewConversation = false) : IIntegrationEvent;

/// <summary>
/// Asks the outbound worker to send an already stored outbound message (status <c>queued</c>). <see cref="Deferrals"/> counts how often the
/// account's rate limit postponed it.
/// </summary>
[IntegrationEvent(Topics.OutboundMessageRequested)]
public sealed record OutboundMessageRequested(Guid ConversationId, Guid MessageId, DateTimeOffset MessageCreatedAt, string HandledBy, Guid? RequestedBy, int Deferrals = 0) : IIntegrationEvent;

[IntegrationEvent(Topics.OutboundMessageCompleted)]
public sealed record OutboundMessageCompleted(Guid ConversationId, Guid MessageId, string Status, string? ExternalId, string? ErrorCode) : IIntegrationEvent;

/// <summary>The customer read our messages up to and including <see cref="UpToMessageId"/> (same topic as completions, keyed by conversation).</summary>
[IntegrationEvent(Topics.OutboundMessageCompleted, Type = "conversations.messages-read")]
public sealed record MessagesRead(Guid ConversationId, Guid UpToMessageId, DateTimeOffset ReadAt, int Count) : IIntegrationEvent;

/// <summary>A conversation needs a person: the AI or a rule gave up, or a team member handed it off. Notifications and realtime consume it.</summary>
[IntegrationEvent(Topics.HandoffRequested)]
public sealed record HandoffRequested(Guid ConversationId, string Reason, Guid? RequestedBy, string? SuggestedReply) : IIntegrationEvent;

/// <summary>
/// The conversation engine (automation rules, flows) asks the AI agent for a reply to <see cref="MessageId"/>. <see cref="Goal"/> is set when
/// a flow hands over with an instruction ("find the right size"). Consumed by the ai group from BE-1.4 on.
/// </summary>
[IntegrationEvent(Topics.ReplyRequested)]
public sealed record ReplyRequested(Guid ConversationId, Guid MessageId, DateTimeOffset MessageCreatedAt, string? Goal, Guid? FlowRunId) : IIntegrationEvent;

public static class ConversationActionKinds
{
    /// <summary>Queue a DM to the customer.</summary>
    public const string Send = "send";

    /// <summary>Queue the one private reply to <see cref="ConversationAction.CommentId"/>.</summary>
    public const string PrivateReply = "private_reply";

    public const string AddTag = "add_tag";
    public const string RemoveTag = "remove_tag";
    public const string SetField = "set_field";
    public const string Handoff = "handoff";
    public const string PauseAi = "pause_ai";
}

public sealed record ConversationAction(
    string Kind,
    OutboundContent? Content = null,
    string? CommentId = null,
    string? Tag = null,
    string? Field = null,
    string? Value = null,
    string? Reason = null)
{
    public static ConversationAction Send(OutboundContent content) => new(ConversationActionKinds.Send, content);

    public static ConversationAction PrivateReply(string commentId, OutboundContent content) => new(ConversationActionKinds.PrivateReply, content, commentId);
}

/// <summary>
/// What the engine decided for a customer, applied by Conversations in one transaction: messages to queue, tags, handoff.
/// The customer is named by channel account and external user id, so a commenter who never wrote a DM gets a conversation here.
/// Keyed by <see cref="Key"/>. <see cref="InboundMessageId"/> is the message that triggered it, marked as handled by
/// <see cref="HandledBy"/> and <see cref="RuleId"/>.
/// </summary>
[IntegrationEvent(Topics.ConversationActionsRequested)]
public sealed record ConversationActionsRequested(
    Guid ChannelAccountId,
    string ChannelKind,
    string ExternalUserId,
    string HandledBy,
    Guid? RuleId,
    Guid? FlowRunId,
    Guid? InboundMessageId,
    DateTimeOffset? InboundMessageCreatedAt,
    IReadOnlyList<ConversationAction> Actions,
    string? Username = null) : IIntegrationEvent
{
    public static string Key(Guid channelAccountId, string externalUserId) => $"{channelAccountId:N}:{externalUserId}";
}

/// <summary>What the engine needs to know about a conversation and its contact.</summary>
public sealed record ConversationSnapshot(
    Guid Id,
    Guid WorkspaceId,
    Guid ChannelAccountId,
    Guid ContactId,
    string ExternalUserId,
    string Status,
    bool AiPaused,
    DateTimeOffset? LastInboundAt,
    DateTimeOffset? WindowExpiresAt,
    string? ContactName,
    string? ContactUsername,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Fields)
{
    public bool IsWindowOpen(DateTimeOffset now) => WindowExpiresAt > now;
}

/// <summary>In-process reads of the Conversations module (current tenant).</summary>
public interface IConversationQueries
{
    Task<ConversationSnapshot?> GetAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<ConversationSnapshot?> FindAsync(Guid channelAccountId, string externalUserId, CancellationToken cancellationToken);
}
