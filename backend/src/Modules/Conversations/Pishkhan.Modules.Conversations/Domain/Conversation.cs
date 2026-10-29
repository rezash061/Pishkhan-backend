using System.Text.Json;
using Pishkhan.Contracts;
using Pishkhan.Modules.Channels.Contracts;
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

    public const int MaxTags = 50;
    public const int MaxFields = 50;

    /// <summary>Labels set by the team or by automation (CRM segments use them from BE-1.7 on).</summary>
    public List<string> Tags { get; private set; } = [];

    /// <summary>Custom fields (answers collected by flows, values set by rules).</summary>
    public Dictionary<string, string> Fields { get; private set; } = new(StringComparer.Ordinal);

    public bool AddTag(string tag)
    {
        if (Tags.Contains(tag, StringComparer.Ordinal) || Tags.Count >= MaxTags)
        {
            return false;
        }

        Tags = [.. Tags, tag];
        return true;
    }

    public bool RemoveTag(string tag)
    {
        if (!Tags.Contains(tag, StringComparer.Ordinal))
        {
            return false;
        }

        Tags = [.. Tags.Where(t => t != tag)];
        return true;
    }

    public bool SetField(string key, string value)
    {
        if (!Fields.ContainsKey(key) && Fields.Count >= MaxFields)
        {
            return false;
        }

        // A new instance, so EF sees the jsonb column change.
        Fields = new Dictionary<string, string>(Fields, StringComparer.Ordinal) { [key] = value };
        return true;
    }

    /// <summary>Fills in the username the first time a channel tells us (a comment carries it; a DM does not).</summary>
    public void LearnUsername(string? username)
    {
        if (Username is null && !string.IsNullOrWhiteSpace(username))
        {
            Username = username.Length <= 100 ? username : username[..100];
        }
    }

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

    /// <summary>How long after the customer's last message a person may still reply with the <c>HUMAN_AGENT</c> tag.</summary>
    public static readonly TimeSpan HumanAgentWindow = TimeSpan.FromDays(7);

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

    public bool IsHumanAgentWindowOpen(DateTimeOffset now) => LastInboundAt + HumanAgentWindow > now;

    public bool Assign(Guid? assigneeId)
    {
        if (AssigneeId == assigneeId)
        {
            return false;
        }

        AssigneeId = assigneeId;
        return true;
    }

    /// <summary>Changes the status. Returns false for an unknown status or no change.</summary>
    public bool ChangeStatus(string status)
    {
        if (!ConversationStatuses.All.Contains(status) || Status == status)
        {
            return false;
        }

        Status = status;
        return true;
    }

    public bool SetAiPaused(bool paused)
    {
        if (AiPaused == paused)
        {
            return false;
        }

        AiPaused = paused;
        return true;
    }

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

    /// <summary>Outbound only: the full content (quick replies, buttons, cards, media) when it is more than plain text.</summary>
    public string? ContentJson { get; init; }

    /// <summary>Outbound only: the Meta message tag used (<c>HUMAN_AGENT</c> outside the 24-hour window).</summary>
    public string? Tag { get; private set; }

    /// <summary>Outbound private reply: the comment it answers (one private reply per comment).</summary>
    public string? ReplyToCommentId { get; init; }

    public DateTimeOffset? ReadAt { get; private set; }

    public string? ExternalId { get; private set; }

    public string Status { get; private set; } = MessageStatuses.Received;

    public string? ErrorCode { get; private set; }

    public string? HandledBy { get; init; }

    /// <summary>The automation rule that produced this message (outbound) or answered it (inbound).</summary>
    public Guid? AutomationRuleId { get; init; }

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

    public static Message QueueOutbound(Guid workspaceId, Guid conversationId, string text, string handledBy, Guid? sentBy, DateTimeOffset now) =>
        QueueOutbound(workspaceId, conversationId, new OutboundContent(text), handledBy, sentBy, now);

    /// <summary>Queues an outbound message. <see cref="Text"/> holds the text or a short summary for the inbox list and search.</summary>
    public static Message QueueOutbound(Guid workspaceId, Guid conversationId, OutboundContent content, string handledBy, Guid? sentBy, DateTimeOffset now, string? replyToCommentId = null, Guid? ruleId = null)
    {
        var plain = content is { Attachment: null, QuickReplies: null or { Count: 0 }, Buttons: null or { Count: 0 }, Cards: null or { Count: 0 } };
        return new Message
        {
            Id = Uuid.NewV7(now),
            CreatedAt = now,
            WorkspaceId = workspaceId,
            ConversationId = conversationId,
            Direction = MessageDirections.Outbound,
            Kind = content.Cards is { Count: > 0 } ? "cards" : content.Attachment is { } a ? a.Type : "text",
            Text = content.Text ?? content.Cards?.FirstOrDefault()?.Title,
            ContentJson = plain ? null : JsonSerializer.Serialize(content with { Tag = null }, EnvelopeJson.Options),
            Status = MessageStatuses.Queued,
            HandledBy = handledBy,
            SentBy = sentBy,
            ReplyToCommentId = replyToCommentId,
            AutomationRuleId = ruleId,
        };
    }

    /// <summary>The content to hand to the channel adapter.</summary>
    public OutboundContent ToContent(string? tag) =>
        (ContentJson is null ? new OutboundContent(Text) : JsonSerializer.Deserialize<OutboundContent>(ContentJson, EnvelopeJson.Options)!) with { Tag = tag };

    public void MarkSent(string? externalId, string? tag, DateTimeOffset now)
    {
        Status = MessageStatuses.Sent;
        ExternalId = externalId;
        Tag = tag;
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

/// <summary>An internal note on a conversation, visible to the team only.</summary>
public sealed class ConversationNote : Entity<Guid>, ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public Guid ConversationId { get; init; }

    public Guid AuthorId { get; init; }

    public required string Text { get; init; }

    public static ConversationNote Create(Guid workspaceId, Guid conversationId, Guid authorId, string text, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        ConversationId = conversationId,
        AuthorId = authorId,
        Text = text,
        CreatedAt = now,
    };
}

/// <summary>A saved reply the team inserts with <c>/shortcut</c> in the composer.</summary>
public sealed class QuickReplyTemplate : Entity<Guid>, ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public string Shortcut { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public static QuickReplyTemplate Create(Guid workspaceId, string shortcut, string text, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Shortcut = shortcut,
        Text = text,
        CreatedAt = now,
    };
}

/// <summary>Enforces one private reply per comment (Meta allows exactly one).</summary>
public sealed class PrivateReplyKey
{
    public Guid ChannelAccountId { get; init; }

    public required string CommentId { get; init; }

    public Guid MessageId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
