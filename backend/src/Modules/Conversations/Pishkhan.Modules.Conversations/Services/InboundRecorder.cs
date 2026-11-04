using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Services;

/// <summary>
/// One inbound message of any channel: contact, conversation and stored message, then <see cref="InboundMessageReceived"/> (backend plan
/// 9.4). Shared by the Instagram and Bale ingest handlers.
/// </summary>
internal static class InboundRecorder
{
    /// <summary>False when the message was recorded before (a redelivered webhook).</summary>
    public static async Task<bool> RecordAsync(ConversationsDbContext db, ChannelAccountInfo account, InboundChannelMessage inbound, DateTimeOffset now, CancellationToken ct)
    {
        if (db.ExternalMessageKeys.Local.Any(k => k.ChannelAccountId == account.Id && k.ExternalId == inbound.ExternalMessageId)
            || await db.ExternalMessageKeys.AnyAsync(k => k.ChannelAccountId == account.Id && k.ExternalId == inbound.ExternalMessageId, ct))
        {
            return false;
        }

        var (conversation, isNew) = await ConversationLookup.FindOrOpenAsync(db, account.WorkspaceId, account.Id, account.Kind, inbound.SenderExternalId, now, ct);
        var normalized = inbound.Text is null ? null : PersianNormalizer.Normalize(inbound.Text);

        // The story a customer replied to is kept with the attachments, so the inbox can show what they answered.
        IReadOnlyList<InboundAttachment> media = inbound.ReplyToStoryId is null ? inbound.Attachments : [.. inbound.Attachments, new InboundAttachment("story_reply", inbound.ReplyToStoryUrl)];
        var attachments = media.Count == 0 ? null : JsonSerializer.Serialize(media, EnvelopeJson.Options);
        var stored = Message.Inbound(account.WorkspaceId, conversation.Id, inbound.Kind, Truncate(inbound.Text), Truncate(normalized), inbound.ExternalMessageId, attachments, now);

        // A voice note without text is answered once it is transcribed (BE-2.4).
        var voice = string.IsNullOrWhiteSpace(inbound.Text) ? VoiceNotes.AudioUrl(inbound.Attachments) : null;
        if (voice is not null)
        {
            stored.AwaitTranscript();
        }

        db.Messages.Add(stored);
        db.ExternalMessageKeys.Add(new ExternalMessageKey { ChannelAccountId = account.Id, ExternalId = inbound.ExternalMessageId, MessageId = stored.Id, CreatedAt = now });
        conversation.RecordInbound(inbound.SentAt, inbound.Text ?? $"[{inbound.Kind}]");

        db.Publish(
            new InboundMessageReceived(
                conversation.Id, stored.Id, stored.CreatedAt, account.Id, account.Kind, conversation.ContactId, stored.Kind, stored.Text, stored.NormalizedText,
                inbound.Attachments.Count, inbound.Postback, inbound.ReplyToStoryId, inbound.SenderExternalId, isNew, AwaitingTranscript: voice is not null),
            conversation.Id.ToString());
        if (voice is not null)
        {
            db.Publish(new VoiceNoteReceived(account.WorkspaceId, conversation.Id, stored.Id, stored.CreatedAt, voice), conversation.Id.ToString(), workspaceId: account.WorkspaceId);
        }

        return true;
    }

    private static string? Truncate(string? s) => s is { Length: > 4000 } ? s[..4000] : s;
}

/// <summary>
/// A one-time code that joins a customer's Bale chat to their existing contact (BE-2.7): the panel or a campaign gives the customer
/// <c>https://ble.ir/{bot}?start={token}</c>; when they press start, the bot's first message carries the token.
/// </summary>
public sealed class ContactLinkToken : ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public required string Token { get; init; }

    public Guid WorkspaceId { get; init; }

    public Guid ContactId { get; init; }

    /// <summary>The Bale account the link opens.</summary>
    public Guid ChannelAccountId { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public static ContactLinkToken Create(Guid workspaceId, Guid contactId, Guid channelAccountId, DateTimeOffset now) => new()
    {
        // 16 random bytes, URL-safe: Telegram-compatible start parameters allow A-Z, a-z, 0-9, _ and -.
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        WorkspaceId = workspaceId,
        ContactId = contactId,
        ChannelAccountId = channelAccountId,
        ExpiresAt = now + Lifetime,
        CreatedAt = now,
    };

    /// <summary>The token of a <c>/start {token}</c> message, or null.</summary>
    public static string? FromStart(string? text) =>
        text is { Length: > 7 } t && t.StartsWith("/start ", StringComparison.Ordinal) && t[7..].Trim() is { Length: >= 16 and <= 64 } token ? token : null;
}

/// <summary>
/// Ingest group, keyed by Bale account: one bot update. A <c>/start {token}</c> from a link first joins the Bale chat to the token's
/// contact (and records that they chose to talk on Bale), so the customer continues as the same contact in the inbox.
/// </summary>
internal sealed class BaleIngestHandler(
    ConversationsDbContext db, ITenantContext tenant, IChannelDirectory channels, IChannelWebhookParser parser, IClock clock, ILogger<BaleIngestHandler> logger)
    : IdempotentHandler<BaleUpdateReceived, ConversationsDbContext>(db)
{
    protected override async Task HandleOnceAsync(BaleUpdateReceived message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        tenant.SetWorkspace(message.WorkspaceId);
        var account = await channels.GetAsync(message.ChannelAccountId, cancellationToken);
        if (account is not { Kind: ChannelKinds.Bale } || account.Status == ChannelStatuses.Disconnected)
        {
            logger.LogInformation("Bale update for unknown or disconnected account {ChannelAccountId} dropped", message.ChannelAccountId);
            return;
        }

        if (parser.ParseBaleUpdate(message.Update, account.ExternalId) is not { } inbound)
        {
            return;
        }

        var now = clock.UtcNow;
        if (ContactLinkToken.FromStart(inbound.Text) is { } token)
        {
            await LinkAsync(account, inbound.SenderExternalId, token, now, cancellationToken);
            inbound = inbound with { Text = "/start" };
        }

        await InboundRecorder.RecordAsync(Db, account, inbound, now, cancellationToken);
    }

    private async Task LinkAsync(ChannelAccountInfo account, string chatId, string token, DateTimeOffset now, CancellationToken ct)
    {
        var link = await Db.ContactLinkTokens.FirstOrDefaultAsync(t => t.Token == token && t.ChannelAccountId == account.Id, ct);
        if (link is null || link.UsedAt is not null || link.ExpiresAt < now)
        {
            return;
        }

        // A chat already known to the bot keeps its contact: a link never moves someone's conversation to another person.
        if (await Db.ChannelIdentities.AnyAsync(i => i.ChannelAccountId == account.Id && i.ExternalUserId == chatId, ct)
            || !await Db.Contacts.AnyAsync(c => c.Id == link.ContactId, ct))
        {
            return;
        }

        link.UsedAt = now;
        Db.ChannelIdentities.Add(ChannelIdentity.Create(account.WorkspaceId, link.ContactId, account.Id, chatId, now));
        Db.Consents.Add(ContactConsent.Create(account.WorkspaceId, link.ContactId, ConsentChannels.Bale, true, ConsentSources.Channel, "start: " + account.Username, null, now));
    }
}
