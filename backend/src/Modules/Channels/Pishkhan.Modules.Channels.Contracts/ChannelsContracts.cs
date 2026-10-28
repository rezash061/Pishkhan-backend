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

public sealed record OutboundContent(string? Text);

public sealed record SendResult(bool Success, string? ExternalMessageId, string? ErrorCode, bool Retryable)
{
    public static SendResult Sent(string? externalId) => new(true, externalId, null, false);

    public static SendResult Failed(string code, bool retryable) => new(false, null, code, retryable);
}

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
    Task<SendResult> SendAsync(Guid channelAccountId, string recipientExternalId, OutboundContent content, CancellationToken cancellationToken);
}
