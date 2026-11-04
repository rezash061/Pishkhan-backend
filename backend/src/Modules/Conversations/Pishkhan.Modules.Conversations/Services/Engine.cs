using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Services;

internal static class ConversationLookup
{
    /// <summary>
    /// The customer's conversation on this channel account, opened (with contact and identity) when there is none. <c>IsNew</c> is true
    /// when the customer has not written to us before: a fresh conversation, or one opened by a private reply to their comment.
    /// </summary>
    public static async Task<(Conversation Conversation, bool IsNew)> FindOrOpenAsync(
        ConversationsDbContext db, Guid workspaceId, Guid channelAccountId, string channelKind, string externalUserId, DateTimeOffset now, CancellationToken ct)
    {
        var identity = db.ChannelIdentities.Local.FirstOrDefault(i => i.ChannelAccountId == channelAccountId && i.ExternalUserId == externalUserId)
            ?? await db.ChannelIdentities.FirstOrDefaultAsync(i => i.ChannelAccountId == channelAccountId && i.ExternalUserId == externalUserId, ct);
        if (identity is null)
        {
            var contact = Contact.Create(workspaceId, null, null, now);
            identity = ChannelIdentity.Create(workspaceId, contact.Id, channelAccountId, externalUserId, now);
            db.Contacts.Add(contact);
            db.ChannelIdentities.Add(identity);
            if (channelKind == ChannelKinds.Instagram)
            {
                db.Publish(new ContactProfileRequested(workspaceId, contact.Id, channelAccountId, externalUserId), contact.Id.ToString(), workspaceId: workspaceId);
            }
        }

        var conversation = db.Conversations.Local.FirstOrDefault(c => c.ChannelAccountId == channelAccountId && c.ContactId == identity.ContactId)
            ?? await db.Conversations.FirstOrDefaultAsync(c => c.ChannelAccountId == channelAccountId && c.ContactId == identity.ContactId, ct);
        if (conversation is null)
        {
            conversation = Conversation.Open(workspaceId, channelAccountId, channelKind, identity.ContactId, externalUserId, now);
            db.Conversations.Add(conversation);
            return (conversation, true);
        }

        return (conversation, conversation.LastInboundAt is null);
    }
}

/// <summary>
/// Engine group: applies what automation decided for a customer, in one transaction (queued messages, tags, fields, handoff, AI pause),
/// and marks the triggering inbound message as handled. The outbound worker enforces the window and the one-private-reply rule again.
/// </summary>
internal sealed class ConversationActionsHandler(ConversationsDbContext db, ITenantContext tenant, IClock clock, ILogger<ConversationActionsHandler> logger)
    : IdempotentHandler<ConversationActionsRequested, ConversationsDbContext>(db)
{
    protected override async Task HandleOnceAsync(ConversationActionsRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (tenant.WorkspaceId is not { } workspaceId)
        {
            logger.LogWarning("Conversation actions {MessageId} without a workspace dropped", envelope.MessageId);
            return;
        }

        var now = clock.UtcNow;
        var (conversation, _) = await ConversationLookup.FindOrOpenAsync(Db, workspaceId, message.ChannelAccountId, message.ChannelKind, message.ExternalUserId, now, cancellationToken);
        var contact = Db.Contacts.Local.FirstOrDefault(c => c.Id == conversation.ContactId)
            ?? await Db.Contacts.FirstAsync(c => c.Id == conversation.ContactId, cancellationToken);
        contact.LearnUsername(message.Username);

        foreach (var action in message.Actions)
        {
            switch (action.Kind)
            {
                case ConversationActionKinds.Send when action.Content is { IsEmpty: false } content:
                    Queue(conversation, content, message, null, now);
                    break;
                case ConversationActionKinds.PrivateReply when action.Content is { IsEmpty: false } content && action.CommentId is { } commentId:
                    Queue(conversation, content, message, commentId, now);
                    break;
                case ConversationActionKinds.AddTag when !string.IsNullOrWhiteSpace(action.Tag):
                    contact.AddTag(action.Tag);
                    break;
                case ConversationActionKinds.RemoveTag when !string.IsNullOrWhiteSpace(action.Tag):
                    contact.RemoveTag(action.Tag);
                    break;
                case ConversationActionKinds.SetField when !string.IsNullOrWhiteSpace(action.Field):
                    contact.SetField(action.Field, action.Value ?? string.Empty);
                    break;
                case ConversationActionKinds.PauseAi:
                    conversation.SetAiPaused(true);
                    break;
                case ConversationActionKinds.CapturePhone:
                    CapturePhone(contact, action, now);
                    break;
                case ConversationActionKinds.Handoff:
                    if (conversation.HandOff(action.Reason ?? "automation", action.SuggestedReply, now))
                    {
                        Db.Publish(new HandoffRequested(conversation.Id, conversation.HandoffReason!, null, conversation.HandoffSuggestedReply), conversation.Id.ToString());
                    }

                    break;
                default:
                    logger.LogWarning("Unknown or incomplete conversation action {Kind} skipped", action.Kind);
                    break;
            }
        }

        if (message is { InboundMessageId: { } inboundId, InboundMessageCreatedAt: { } inboundAt })
        {
            await Db.Messages
                .Where(m => m.Id == inboundId && m.CreatedAt == inboundAt && m.HandledBy == null)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.HandledBy, message.HandledBy).SetProperty(m => m.AutomationRuleId, message.RuleId), cancellationToken);
        }
    }

    /// <summary>The customer's number and their consent from a flow (BE-2.7). The consent history keeps what they agreed to.</summary>
    private void CapturePhone(Contact contact, ConversationAction action, DateTimeOffset now)
    {
        if (!PhoneNumber.TryParse(PersianDigits.ToLatin(action.Value ?? string.Empty).Trim(), out var phone))
        {
            logger.LogInformation("capture_phone skipped for contact {ContactId}: not a mobile number", contact.Id);
            return;
        }

        contact.SetField(FieldKeys.Phone, phone.Value.National);
        foreach (var channel in (action.Channels ?? [ConsentChannels.Sms]).Where(ConsentChannels.All.Contains).Distinct(StringComparer.Ordinal))
        {
            Db.Consents.Add(ContactConsent.Create(contact.WorkspaceId, contact.Id, channel, true, ConsentSources.Flow, action.Evidence, null, now));
        }
    }

    private void Queue(Conversation conversation, Channels.Contracts.OutboundContent content, ConversationActionsRequested request, string? commentId, DateTimeOffset now)
    {
        var queued = Message.QueueOutbound(conversation.WorkspaceId, conversation.Id, content, request.HandledBy, null, now, commentId, request.RuleId);
        Db.Messages.Add(queued);
        Db.Publish(new OutboundMessageRequested(conversation.Id, queued.Id, queued.CreatedAt, request.HandledBy, null), conversation.Id.ToString());
    }
}

internal sealed class ConversationQueries(ConversationsDbContext db) : IConversationQueries
{
    public async Task<ConversationCounts> CountsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Conversations.AsNoTracking()
            .Where(c => c.Status != ConversationStatuses.Closed)
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Unread = g.Count(c => c.UnreadCount > 0), Oldest = g.Min(c => c.HandedOffAt) })
            .ToListAsync(cancellationToken);
        var handedOff = rows.FirstOrDefault(r => r.Status == ConversationStatuses.HandedOff);
        return new ConversationCounts(
            rows.Where(r => r.Status != ConversationStatuses.HandedOff).Sum(r => r.Count), handedOff?.Count ?? 0, rows.Sum(r => r.Unread), handedOff?.Oldest);
    }

    public async Task<ConversationSnapshot?> GetAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await SnapshotAsync(db.Conversations.Where(c => c.Id == conversationId), cancellationToken);

    public async Task<ConversationSnapshot?> FindAsync(Guid channelAccountId, string externalUserId, CancellationToken cancellationToken) =>
        await SnapshotAsync(db.Conversations.Where(c => c.ChannelAccountId == channelAccountId && c.ExternalUserId == externalUserId), cancellationToken);

    private async Task<ConversationSnapshot?> SnapshotAsync(IQueryable<Conversation> query, CancellationToken ct)
    {
        var row = await query.AsNoTracking()
            .Join(db.Contacts.AsNoTracking(), c => c.ContactId, k => k.Id, (c, k) => new { Conversation = c, Contact = k })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var (c, k) = (row.Conversation, row.Contact);
        return new ConversationSnapshot(
            c.Id, c.WorkspaceId, c.ChannelAccountId, c.ContactId, c.ExternalUserId, c.Status, c.AiPaused, c.LastInboundAt, c.WindowExpiresAt,
            k.DisplayName, k.Username, k.Tags, k.Fields, c.ChannelKind);
    }

    public async Task<ContactSnapshot?> GetContactAsync(Guid contactId, CancellationToken cancellationToken) =>
        await db.Contacts.AsNoTracking().Where(k => k.Id == contactId).Select(k => new ContactSnapshot(k.Id, k.DisplayName, k.Username)).FirstOrDefaultAsync(cancellationToken);

    public async Task<MessageImage?> GetInboundImageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var row = await db.Messages.AsNoTracking()
            .Where(m => m.Id == messageId && m.Direction == MessageDirections.Inbound && m.AttachmentsJson != null)
            .Join(db.Conversations.AsNoTracking(), m => m.ConversationId, c => c.Id, (m, c) => new { m.Id, m.CreatedAt, m.AttachmentsJson, c.ContactId, ConversationId = c.Id, c.ChannelKind })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var attachments = System.Text.Json.JsonSerializer.Deserialize<List<InboundAttachment>>(row.AttachmentsJson!, EnvelopeJson.Options) ?? [];
        return attachments.FirstOrDefault(a => a.Type == AttachmentTypes.Image && a.Url is { Length: > 0 }) is { Url: { } url }
            ? new MessageImage(row.Id, row.ConversationId, row.ContactId, row.ChannelKind, url, row.CreatedAt)
            : null;
    }

    public async Task<IReadOnlyList<MessageSnapshot>> RecentMessagesAsync(Guid conversationId, int limit, CancellationToken cancellationToken)
    {
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(limit)
            .Select(m => new MessageSnapshot(m.Id, m.CreatedAt, m.Direction, m.Kind, m.Text ?? m.Transcript, m.HandledBy, m.Status))
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return rows;
    }
}
