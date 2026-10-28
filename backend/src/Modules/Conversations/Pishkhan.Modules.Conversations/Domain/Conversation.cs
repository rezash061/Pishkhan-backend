using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Domain;

/// <summary>A customer. Lives here until the CRM module exists (ADR 0005); then it moves with its identities.</summary>
public sealed class Contact : AggregateRoot<Guid>, ITenantOwned
{
    private Contact()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string? DisplayName { get; private set; }

    public string? Username { get; private set; }

    public static Contact Create(Guid workspaceId, string? displayName, string? username, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        DisplayName = displayName,
        Username = username,
        CreatedAt = now,
    };
}

/// <summary>Who a contact is on one channel account (Instagram-scoped user id, Bale chat id, phone).</summary>
public sealed class ChannelIdentity : Entity<Guid>, ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public Guid ContactId { get; init; }

    public Guid ChannelAccountId { get; init; }

    public required string ExternalUserId { get; init; }

    public static ChannelIdentity Create(Guid workspaceId, Guid contactId, Guid channelAccountId, string externalUserId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        ContactId = contactId,
        ChannelAccountId = channelAccountId,
        ExternalUserId = externalUserId,
        CreatedAt = now,
    };
}

public sealed class Conversation : AggregateRoot<Guid>, ITenantOwned
{
    /// <summary>Instagram's standard messaging window after the customer's last message.</summary>
    public static readonly TimeSpan MessagingWindow = TimeSpan.FromHours(24);

    private Conversation()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid ChannelAccountId { get; private set; }

    public string ChannelKind { get; private set; } = string.Empty;

    public Guid ContactId { get; private set; }

    /// <summary>The customer's id on the channel, kept here so the outbound worker needs no join.</summary>
    public string ExternalUserId { get; private set; } = string.Empty;

    public string Status { get; private set; } = ConversationStatuses.Open;

    public Guid? AssigneeId { get; private set; }

    public bool AiPaused { get; private set; }

    public DateTimeOffset? LastInboundAt { get; private set; }

    public DateTimeOffset? LastOutboundAt { get; private set; }

    public DateTimeOffset? WindowExpiresAt { get; private set; }

    public int UnreadCount { get; private set; }

    public string? LastMessagePreview { get; private set; }

    public static Conversation Open(Guid workspaceId, Guid channelAccountId, string channelKind, Guid contactId, string externalUserId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        ChannelAccountId = channelAccountId,
        ChannelKind = channelKind,
        ContactId = contactId,
        ExternalUserId = externalUserId,
        CreatedAt = now,
    };

    public bool IsWindowOpen(DateTimeOffset now) => WindowExpiresAt > now;

    public void RecordInbound(DateTimeOffset sentAt, string? preview)
    {
        if (LastInboundAt is null || sentAt > LastInboundAt)
        {
            LastInboundAt = sentAt;
            WindowExpiresAt = sentAt + MessagingWindow;
        }

        if (Status == ConversationStatuses.Closed)
        {
            Status = ConversationStatuses.Open;
        }

        UnreadCount++;
        LastMessagePreview = Preview(preview);
    }

    public void RecordOutbound(DateTimeOffset sentAt, string? preview)
    {
        LastOutboundAt = sentAt;
        LastMessagePreview = Preview(preview);
    }

    public void MarkRead() => UnreadCount = 0;

    private static string? Preview(string? text) => text is null ? null : text.Length <= 120 ? text : text[..120];
}

/// <summary>
/// One message. Stored in <c>conversations.messages</c>, range-partitioned by month on <c>created_at</c>,
/// so the primary key is <c>(id, created_at)</c> and every lookup should carry both.
/// </summary>
public sealed class Message : ITenantOwned
{
    public Guid Id { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid WorkspaceId { get; init; }

    public Guid ConversationId { get; init; }

    public required string Direction { get; init; }

    public required string Kind { get; init; }

    public string? Text { get; init; }

    public string? NormalizedText { get; init; }

    public Guid? MediaFileId { get; set; }

    /// <summary>Raw attachment descriptors (type and CDN URL) until the media worker downloads them.</summary>
    public string? AttachmentsJson { get; init; }

    public string? ExternalId { get; private set; }

    public string Status { get; private set; } = MessageStatuses.Received;

    public string? ErrorCode { get; private set; }

    public string? HandledBy { get; init; }

    public Guid? SentBy { get; init; }

    public DateTimeOffset? SentAt { get; private set; }

    public static Message Inbound(Guid workspaceId, Guid conversationId, string kind, string? text, string? normalized, string externalId, string? attachmentsJson, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        CreatedAt = now,
        WorkspaceId = workspaceId,
        ConversationId = conversationId,
        Direction = MessageDirections.Inbound,
        Kind = kind,
        Text = text,
        NormalizedText = normalized,
        ExternalId = externalId,
        AttachmentsJson = attachmentsJson,
    };

    public static Message QueueOutbound(Guid workspaceId, Guid conversationId, string text, string handledBy, Guid? sentBy, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        CreatedAt = now,
        WorkspaceId = workspaceId,
        ConversationId = conversationId,
        Direction = MessageDirections.Outbound,
        Kind = "text",
        Text = text,
        Status = MessageStatuses.Queued,
        HandledBy = handledBy,
        SentBy = sentBy,
    };

    public void MarkSent(string? externalId, DateTimeOffset now)
    {
        Status = MessageStatuses.Sent;
        ExternalId = externalId;
        SentAt = now;
    }

    public void MarkFailed(string errorCode)
    {
        Status = MessageStatuses.Failed;
        ErrorCode = errorCode;
    }
}

/// <summary>Dedupe of channel message ids. Kept outside the partitioned table, where a unique index would need <c>created_at</c>.</summary>
public sealed class ExternalMessageKey
{
    public Guid ChannelAccountId { get; init; }

    public required string ExternalId { get; init; }

    public Guid MessageId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
