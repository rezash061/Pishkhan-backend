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
    bool IsEcho,
    string? ReplyToStoryUrl = null);

public sealed record InboundAttachment(string Type, string? Url);

/// <summary>A comment (or live video comment) on one of the account's media, parsed from the webhook's <c>changes</c>.</summary>
public sealed record InboundChannelComment(
    string Kind,
    string CommentId,
    string MediaId,
    string? MediaProductType,
    string? ParentCommentId,
    string FromExternalId,
    string? FromUsername,
    string? Text,
    DateTimeOffset CreatedAt);

public static class CommentKinds
{
    public const string Comment = "comment";
    public const string LiveComment = "live_comment";
}

/// <summary>Where a comment event came from: the webhook, or the backlog job that pages through older comments of a post.</summary>
public static class CommentSources
{
    public const string Webhook = "webhook";
    public const string Backlog = "backlog";
}

/// <summary>
/// A customer's comment on the account's media, keyed by media id so a viral post is processed in order on one partition.
/// <see cref="NormalizedText"/> is the Persian-normalized text used for keyword matching.
/// </summary>
[IntegrationEvent(Topics.InboundCommentReceived)]
public sealed record InboundCommentReceived(
    Guid ChannelAccountId,
    string ChannelKind,
    string Kind,
    string CommentId,
    string MediaId,
    string? ParentCommentId,
    string FromExternalId,
    string? FromUsername,
    string? Text,
    string? NormalizedText,
    DateTimeOffset CreatedAt,
    string Source) : IIntegrationEvent;

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

    /// <summary>The customer mentioned the account in their story.</summary>
    public const string StoryMention = "story_mention";

    /// <summary>The customer shared a post or reel into the conversation.</summary>
    public const string Share = "share";
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

public sealed record ChannelComment(string Id, string? Text, string? FromExternalId, string? FromUsername, string? ParentId, DateTimeOffset? Timestamp);

public sealed record ChannelCommentPage(IReadOnlyList<ChannelComment> Items, string? NextCursor);

/// <summary>The result of a channel read: a value, or an error code. <see cref="Retryable"/> errors (rate limits, outages) are worth retrying.</summary>
public sealed record ChannelReadResult<T>(T? Value, string? ErrorCode, bool Retryable)
{
    public bool Success => ErrorCode is null;

    public static ChannelReadResult<T> Ok(T value) => new(value, null, false);

    public static ChannelReadResult<T> Failed(string code, bool retryable) => new(default, code, retryable);
}

/// <summary>How far apart calls of each kind must be for an account to stay inside its rate profile (backend plan 11, viral posts).</summary>
public sealed record ChannelPacing(TimeSpan Send, TimeSpan PrivateReply, TimeSpan CommentReply);

/// <summary>
/// The outcome of a send. <see cref="RetryAfter"/> is set when the account's rate limit refused the call (<see cref="ChannelErrors.RateLimited"/>):
/// nothing reached the channel, and the call may be made again after that time.
/// </summary>
public sealed record SendResult(bool Success, string? ExternalMessageId, string? ErrorCode, bool Retryable, TimeSpan? RetryAfter = null)
{
    public static SendResult Sent(string? externalId) => new(true, externalId, null, false);

    public static SendResult Failed(string code, bool retryable, TimeSpan? retryAfter = null) => new(false, null, code, retryable, retryAfter);

    public bool IsRateLimited => ErrorCode == ChannelErrors.RateLimited;
}

public static class ChannelErrors
{
    /// <summary>Our own per-account token bucket refused the call before it reached the channel.</summary>
    public const string RateLimited = "channel.rate_limited";
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

    /// <summary>An account of the current workspace, any status.</summary>
    Task<ChannelAccountInfo?> GetAsync(Guid id, CancellationToken cancellationToken);
}

public interface IChannelWebhookParser
{
    IReadOnlyList<InboundChannelMessage> ParseInstagramEntry(JsonElement entry);

    /// <summary>Comments and live comments in the entry's <c>changes</c>.</summary>
    IReadOnlyList<InboundChannelComment> ParseInstagramComments(JsonElement entry);
}

public interface IChannelMessageSender
{
    Task<SendResult> SendAsync(Guid channelAccountId, OutboundRecipient recipient, OutboundContent content, CancellationToken cancellationToken);

    /// <summary>Replies publicly under a comment.</summary>
    Task<SendResult> ReplyToCommentAsync(Guid channelAccountId, string commentId, string text, CancellationToken cancellationToken);
}

public interface IChannelMediaReader
{
    Task<ChannelReadResult<ChannelMedia>> GetMediaAsync(Guid channelAccountId, string mediaId, CancellationToken cancellationToken);

    /// <summary>One page of a media's top-level comments, newest first.</summary>
    Task<ChannelReadResult<ChannelCommentPage>> ListCommentsAsync(Guid channelAccountId, string mediaId, string? after, CancellationToken cancellationToken);
}

public interface IChannelPacing
{
    Task<ChannelPacing> GetAsync(Guid channelAccountId, CancellationToken cancellationToken);
}

public interface IChannelProfileReader
{
    /// <summary>Fetches a customer's profile. Null when the channel does not expose it (the user has not messaged the account, or blocked it).</summary>
    Task<ChannelUserProfile?> GetProfileAsync(Guid channelAccountId, string externalUserId, CancellationToken cancellationToken);
}
