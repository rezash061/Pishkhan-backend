using Pishkhan.Contracts;

namespace Pishkhan.Modules.Conversations.Contracts;

public static class ConversationStatuses
{
    public const string Open = "open";
    public const string Pending = "pending";
    public const string HandedOff = "handed_off";
    public const string Closed = "closed";
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
    public const string Failed = "failed";
}

public static class HandledBy
{
    public const string Rule = "rule";
    public const string Ai = "ai";
    public const string Human = "human";
}

/// <summary>A stored inbound message, keyed by conversation id so a conversation is processed in order.</summary>
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
    int AttachmentCount) : IIntegrationEvent;

/// <summary>Asks the outbound worker to send an already stored outbound message (status <c>queued</c>).</summary>
[IntegrationEvent(Topics.OutboundMessageRequested)]
public sealed record OutboundMessageRequested(Guid ConversationId, Guid MessageId, DateTimeOffset MessageCreatedAt, string HandledBy, Guid? RequestedBy) : IIntegrationEvent;

[IntegrationEvent(Topics.OutboundMessageCompleted)]
public sealed record OutboundMessageCompleted(Guid ConversationId, Guid MessageId, string Status, string? ExternalId, string? ErrorCode) : IIntegrationEvent;
