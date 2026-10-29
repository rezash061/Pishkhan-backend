using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Services;

public sealed class SpikeOptions
{
    public const string Section = "Spike";

    /// <summary>M0 only: answer every inbound text with an echo so the end-to-end path can be shown on a real account.</summary>
    public bool EchoReplies { get; set; }

    public string EchoPrefix { get; set; } = "پیامتان رسید: ";
}

/// <summary>
/// Ingest group: raw webhook entry → contact, conversation and stored message → <see cref="InboundMessageReceived"/> (backend plan 9.4).
/// Keyed by the Instagram account id, so one account's entries are processed in order and contact creation cannot race.
/// </summary>
internal sealed class InstagramIngestHandler(
    ConversationsDbContext db,
    ITenantContext tenant,
    IChannelDirectory channels,
    IChannelWebhookParser parser,
    IClock clock,
    ILogger<InstagramIngestHandler> logger) : IdempotentHandler<InstagramWebhookReceived, ConversationsDbContext>(db)
{
    protected override async Task HandleOnceAsync(InstagramWebhookReceived message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var account = await channels.FindByExternalIdAsync(ChannelKinds.Instagram, message.AccountExternalId, cancellationToken);
        if (account is null)
        {
            logger.LogInformation("Webhook for unknown or disconnected Instagram account {AccountExternalId} dropped", message.AccountExternalId);
            return;
        }

        tenant.SetWorkspace(account.WorkspaceId);
        var now = clock.UtcNow;
        foreach (var inbound in parser.ParseInstagramEntry(message.Entry))
        {
            // Echoes are copies of messages sent by the account itself (by us, or by the owner in the Instagram app).
            if (inbound.IsEcho || inbound.SenderExternalId == account.ExternalId)
            {
                continue;
            }

            if (inbound.Kind == InboundKinds.Read)
            {
                await MarkReadAsync(account.Id, inbound.ExternalMessageId, inbound.SentAt, cancellationToken);
                continue;
            }

            if (await Db.ExternalMessageKeys.AnyAsync(k => k.ChannelAccountId == account.Id && k.ExternalId == inbound.ExternalMessageId, cancellationToken))
            {
                continue;
            }

            var conversation = await FindOrOpenConversationAsync(account, inbound.SenderExternalId, now, cancellationToken);
            var normalized = inbound.Text is null ? null : PersianNormalizer.Normalize(inbound.Text);
            var attachments = inbound.Attachments.Count == 0 ? null : JsonSerializer.Serialize(inbound.Attachments, EnvelopeJson.Options);
            var stored = Message.Inbound(account.WorkspaceId, conversation.Id, inbound.Kind, Truncate(inbound.Text), Truncate(normalized), inbound.ExternalMessageId, attachments, now);
            Db.Messages.Add(stored);
            Db.ExternalMessageKeys.Add(new ExternalMessageKey { ChannelAccountId = account.Id, ExternalId = inbound.ExternalMessageId, MessageId = stored.Id, CreatedAt = now });
            conversation.RecordInbound(inbound.SentAt, inbound.Text ?? $"[{inbound.Kind}]");

            Db.Publish(
                new InboundMessageReceived(conversation.Id, stored.Id, stored.CreatedAt, account.Id, account.Kind, conversation.ContactId, stored.Kind, stored.Text, stored.NormalizedText, inbound.Attachments.Count),
                conversation.Id.ToString());
        }
    }

    /// <summary>A read receipt names our latest message the customer saw; everything we sent before it is read too.</summary>
    private async Task MarkReadAsync(Guid accountId, string externalMessageId, DateTimeOffset readAt, CancellationToken ct)
    {
        var key = await Db.ExternalMessageKeys.AsNoTracking().FirstOrDefaultAsync(k => k.ChannelAccountId == accountId && k.ExternalId == externalMessageId, ct);
        var target = key is null ? null : await Db.Messages.AsNoTracking()
            .Where(m => m.Id == key.MessageId && m.Direction == MessageDirections.Outbound)
            .Select(m => new { m.Id, m.ConversationId, m.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (target is null)
        {
            return;
        }

        var count = await Db.Messages
            .Where(m => m.ConversationId == target.ConversationId && m.Direction == MessageDirections.Outbound
                && m.Status == MessageStatuses.Sent && m.CreatedAt <= target.CreatedAt)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MessageStatuses.Read).SetProperty(m => m.ReadAt, readAt).SetProperty(m => m.UpdatedAt, clock.UtcNow), ct);
        if (count > 0)
        {
            Db.Publish(new MessagesRead(target.ConversationId, target.Id, readAt, count), target.ConversationId.ToString());
        }
    }

    private async Task<Conversation> FindOrOpenConversationAsync(ChannelAccountInfo account, string externalUserId, DateTimeOffset now, CancellationToken ct)
    {
        var identity = await Db.ChannelIdentities.FirstOrDefaultAsync(i => i.ChannelAccountId == account.Id && i.ExternalUserId == externalUserId, ct)
            ?? Db.ChannelIdentities.Local.FirstOrDefault(i => i.ChannelAccountId == account.Id && i.ExternalUserId == externalUserId);
        if (identity is null)
        {
            var contact = Contact.Create(account.WorkspaceId, null, null, now);
            identity = ChannelIdentity.Create(account.WorkspaceId, contact.Id, account.Id, externalUserId, now);
            Db.Contacts.Add(contact);
            Db.ChannelIdentities.Add(identity);
        }

        var conversation = Db.Conversations.Local.FirstOrDefault(c => c.ChannelAccountId == account.Id && c.ContactId == identity.ContactId)
            ?? await Db.Conversations.FirstOrDefaultAsync(c => c.ChannelAccountId == account.Id && c.ContactId == identity.ContactId, ct);
        if (conversation is null)
        {
            conversation = Conversation.Open(account.WorkspaceId, account.Id, account.Kind, identity.ContactId, externalUserId, now);
            Db.Conversations.Add(conversation);
        }

        return conversation;
    }

    private static string? Truncate(string? s) => s is { Length: > 4000 } ? s[..4000] : s;
}

/// <summary>Engine group, M0 spike: queues an echo reply for each inbound text when <c>Spike:EchoReplies</c> is on.</summary>
internal sealed class EchoReplyHandler(ConversationsDbContext db, IOptionsMonitor<SpikeOptions> options, IClock clock)
    : IdempotentHandler<InboundMessageReceived, ConversationsDbContext>(db)
{
    protected override async Task HandleOnceAsync(InboundMessageReceived message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.EchoReplies || string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        var conversation = await Db.Conversations.FirstOrDefaultAsync(c => c.Id == message.ConversationId, cancellationToken);
        if (conversation is null || conversation.AiPaused || conversation.Status == ConversationStatuses.HandedOff)
        {
            return;
        }

        var now = clock.UtcNow;
        var reply = Message.QueueOutbound(conversation.WorkspaceId, conversation.Id, options.CurrentValue.EchoPrefix + message.Text, HandledBy.Rule, null, now);
        Db.Messages.Add(reply);
        Db.Publish(new OutboundMessageRequested(conversation.Id, reply.Id, reply.CreatedAt, HandledBy.Rule, null), conversation.Id.ToString());
    }
}

/// <summary>Outbound policy (backend plan 9.4, Meta platform policy). Decides whether a queued message may go out, and with which tag.</summary>
internal static class OutboundPolicy
{
    public static OutboundDecision Check(Conversation conversation, Message message, DateTimeOffset now)
    {
        // A private reply answers a comment, not a DM thread: the 24-hour window does not apply (the comment's age limit is the producer's check).
        if (message.ReplyToCommentId is not null || conversation.IsWindowOpen(now))
        {
            return OutboundDecision.Allow(null);
        }

        // HUMAN_AGENT is only for a person replying from the panel, within 7 days of the customer's last message.
        if (message is { HandledBy: HandledBy.Human, SentBy: not null } && conversation.IsHumanAgentWindowOpen(now))
        {
            return OutboundDecision.Allow(MessageTags.HumanAgent);
        }

        return OutboundDecision.Reject("conversation.window_closed");
    }
}

internal sealed record OutboundDecision(bool Allowed, string? Tag, string? ErrorCode)
{
    public static OutboundDecision Allow(string? tag) => new(true, tag, null);

    public static OutboundDecision Reject(string code) => new(false, null, code);
}

/// <summary>
/// Outbound group: workspace and policy checks, one private reply per comment, then the channel adapter, then <see cref="OutboundMessageCompleted"/>.
/// A retryable failure throws so the consumer retries with backoff (the transaction, including the private reply key, rolls back); anything else marks the message failed.
/// </summary>
internal sealed class OutboundSendHandler(ConversationsDbContext db, IChannelMessageSender sender, IWorkspaceQueries workspaces, IClock clock, ILogger<OutboundSendHandler> logger)
    : IdempotentHandler<OutboundMessageRequested, ConversationsDbContext>(db)
{
    public const int MaxAttempts = 6;

    protected override async Task HandleOnceAsync(OutboundMessageRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var stored = await Db.Messages.FirstOrDefaultAsync(m => m.Id == message.MessageId && m.CreatedAt == message.MessageCreatedAt, cancellationToken);
        if (stored is null || stored.Status != MessageStatuses.Queued)
        {
            return;
        }

        var conversation = await Db.Conversations.FirstAsync(c => c.Id == message.ConversationId, cancellationToken);
        var now = clock.UtcNow;
        if (await workspaces.IsSuspendedAsync(conversation.WorkspaceId, cancellationToken))
        {
            Fail(stored, conversation, "workspace.suspended");
            return;
        }

        var decision = OutboundPolicy.Check(conversation, stored, now);
        if (!decision.Allowed)
        {
            Fail(stored, conversation, decision.ErrorCode!);
            return;
        }

        if (stored.ReplyToCommentId is { } commentId)
        {
            var claimed = await Db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO conversations.private_reply_keys (channel_account_id, comment_id, message_id, created_at) VALUES ({conversation.ChannelAccountId}, {commentId}, {stored.Id}, {now}) ON CONFLICT DO NOTHING",
                cancellationToken);
            if (claimed == 0)
            {
                Fail(stored, conversation, "comment.private_reply_exists");
                return;
            }
        }

        var recipient = stored.ReplyToCommentId is { } c ? OutboundRecipient.Comment(c) : OutboundRecipient.User(conversation.ExternalUserId);
        var result = await sender.SendAsync(conversation.ChannelAccountId, recipient, stored.ToContent(decision.Tag), cancellationToken);
        if (result.Success)
        {
            stored.MarkSent(result.ExternalMessageId, decision.Tag, now);
            conversation.RecordOutbound(now, stored.Text);
            if (result.ExternalMessageId is { } mid)
            {
                // Read receipts and echoes name our message by Meta's id.
                Db.ExternalMessageKeys.Add(new ExternalMessageKey { ChannelAccountId = conversation.ChannelAccountId, ExternalId = mid, MessageId = stored.Id, CreatedAt = now });
            }

            Db.Publish(new OutboundMessageCompleted(conversation.Id, stored.Id, MessageStatuses.Sent, result.ExternalMessageId, null), conversation.Id.ToString());
            return;
        }

        if (result.Retryable)
        {
            throw new OutboundRetryException(result.ErrorCode ?? "channel.transient");
        }

        logger.LogWarning("Outbound message {MessageId} failed: {ErrorCode}", stored.Id, result.ErrorCode);
        Fail(stored, conversation, result.ErrorCode ?? "channel.rejected");
    }

    private void Fail(Message stored, Conversation conversation, string code)
    {
        stored.MarkFailed(code);
        Db.Publish(new OutboundMessageCompleted(conversation.Id, stored.Id, MessageStatuses.Failed, null, code), conversation.Id.ToString());
    }
}

internal sealed class OutboundRetryException(string code) : Exception("Retryable send failure: " + code)
{
    public string Code { get; } = code;
}
