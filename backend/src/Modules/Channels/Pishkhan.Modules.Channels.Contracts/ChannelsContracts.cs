using System.Text.Json;
using Pishkhan.Contracts;

namespace Pishkhan.Modules.Channels.Contracts;

public static class ChannelKinds
{
    public const string Instagram = "instagram";
    public const string Sandbox = "sandbox";
    public const string Bale = "bale";
    public const string Sms = "sms";
    public const string Web = "web";
}

public static class ChannelStatuses
{
    public const string Connecting = "connecting";
    public const string Active = "active";
    public const string TokenExpiring = "token_expiring";
    public const string ReauthRequired = "reauth_required";
    public const string Disconnected = "disconnected";
}

/// <summary>One raw <c>entry</c> of a Meta webhook, produced by the Ingress host after signature check and dedupe. No database work happens before this.</summary>
[IntegrationEvent(Topics.InstagramWebhookReceived)]
public sealed record InstagramWebhookReceived(string Object, string AccountExternalId, JsonElement Entry, DateTimeOffset ReceivedAt) : IIntegrationEvent;

[IntegrationEvent(Topics.ChannelStatusChanged)]
public sealed record ChannelStatusChanged(Guid ChannelAccountId, Guid WorkspaceId, string Status, string? Reason) : IIntegrationEvent;

public sealed record ChannelAccountInfo(Guid Id, Guid WorkspaceId, string Kind, string ExternalId, string? Username, string Status);

/// <summary>A normalized inbound event parsed from a channel webhook.</summary>
public sealed record InboundChannelMessage(
    string SenderExternalId,
    string RecipientExternalId,
    string ExternalMessageId,
    DateTimeOffset SentAt,
    string Kind,
    string? Text,
    IReadOnlyList<InboundAttachment> Attachments,
    string? ReplyToStoryId,
    string? Postback,
    bool IsEcho);

public sealed record InboundAttachment(string Type, string? Url);

/// <summary>
/// What to send. Exactly one of <see cref="Text"/>, <see cref="Attachment"/> or <see cref="Cards"/> carries the body;
/// <see cref="QuickReplies"/> and <see cref="Buttons"/> decorate a text message (Instagram: up to 13 quick replies, 3 buttons, 10 cards).
/// <see cref="Tag"/> is <see cref="MessageTags.HumanAgent"/> only for a person's reply outside the 24-hour window (Conversations enforces it).
/// </summary>
public sealed record OutboundContent(
    string? Text,
    OutboundAttachment? Attachment = null,
    IReadOnlyList<QuickReply>? QuickReplies = null,
    IReadOnlyList<MessageButton>? Buttons = null,
    IReadOnlyList<MessageCard>? Cards = null,
    string? Tag = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Attachment is null && (Cards is null || Cards.Count == 0);
}

public static class MessageTags
{
    public const string HumanAgent = "HUMAN_AGENT";
}

/// <summary>Kinds of normalized inbound events. <see cref="Read"/> means the customer read our messages up to the one named.</summary>
public static class InboundKinds
{
    public const string Text = "text";
    public const string Attachment = "attachment";
    public const string StoryReply = "story_reply";
    public const string QuickReply = "quick_reply";
    public const string Postback = "postback";
    public const string Read = "read";
}

public static class AttachmentTypes
{
    public const string Image = "image";
    public const string Audio = "audio";
    public const string Video = "video";
}

public sealed record OutboundAttachment(string Type, Uri Url);

public sealed record QuickReply(string Title, string Payload);

/// <summary>A button: <c>postback</c> (sends <see cref="Payload"/> back) or <c>web_url</c> (opens <see cref="Url"/>).</summary>
public sealed record MessageButton(string Type, string Title, string? Payload = null, Uri? Url = null)
{
    public static MessageButton Postback(string title, string payload) => new("postback", title, payload);

    public static MessageButton Link(string title, Uri url) => new("web_url", title, Url: url);
}

/// <summary>One element of a generic template (carousel card).</summary>
public sealed record MessageCard(string Title, string? Subtitle, Uri? ImageUrl, Uri? DefaultUrl, IReadOnlyList<MessageButton>? Buttons);

/// <summary>Who receives a message: an Instagram-scoped user id, or a comment (private reply, one per comment).</summary>
public sealed record OutboundRecipient(string? UserId, string? CommentId)
{
    public static OutboundRecipient User(string id) => new(id, null);

    public static OutboundRecipient Comment(string id) => new(null, id);
}

/// <summary>Public profile of a customer as the channel exposes it. <see cref="FollowsBusiness"/> drives the follow-gate.</summary>
public sealed record ChannelUserProfile(string ExternalId, string? Name, string? Username, Uri? ProfilePictureUrl, int? FollowerCount, bool? FollowsBusiness, bool? BusinessFollowsUser);

public sealed record ChannelMedia(string Id, string? Caption, string MediaType, Uri? MediaUrl, Uri? ThumbnailUrl, Uri? Permalink, DateTimeOffset? Timestamp);

public sealed record ChannelMediaPage(IReadOnlyList<ChannelMedia> Items, string? NextCursor);

public sealed record SendResult(bool Success, string? ExternalMessageId, string? ErrorCode, bool Retryable)
{
    public static SendResult Sent(string? externalId) => new(true, externalId, null, false);

    public static SendResult Failed(string code, bool retryable) => new(false, null, code, retryable);
}

/// <summary>Meta asked us to delete what we hold about one of its users. Every module holding customer data handles it.</summary>
[IntegrationEvent(Topics.DataDeletionRequested)]
public sealed record DataDeletionRequested(Guid RequestId, string ChannelKind, string ExternalUserId) : IIntegrationEvent;

/// <summary>A module finished deleting data for <see cref="RequestId"/>.</summary>
[IntegrationEvent(Topics.DataDeletionCompleted)]
public sealed record DataDeletionCompleted(Guid RequestId, string Module, int ItemsDeleted) : IIntegrationEvent;

/// <summary>In-process API of the Channels module.</summary>
public interface IChannelDirectory
{
    /// <summary>Finds an account across all workspaces (used by ingest before the tenant is known).</summary>
    Task<ChannelAccountInfo?> FindByExternalIdAsync(string kind, string externalId, CancellationToken cancellationToken);
}

public interface IChannelWebhookParser
{
    IReadOnlyList<InboundChannelMessage> ParseInstagramEntry(JsonElement entry);
}

public interface IChannelMessageSender
{
    Task<SendResult> SendAsync(Guid channelAccountId, OutboundRecipient recipient, OutboundContent content, CancellationToken cancellationToken);

    /// <summary>Replies publicly under a comment.</summary>
    Task<SendResult> ReplyToCommentAsync(Guid channelAccountId, string commentId, string text, CancellationToken cancellationToken);
}

public interface IChannelProfileReader
{
    /// <summary>Fetches a customer's profile. Null when the channel does not expose it (the user has not messaged the account, or blocked it).</summary>
    Task<ChannelUserProfile?> GetProfileAsync(Guid channelAccountId, string externalUserId, CancellationToken cancellationToken);
}
