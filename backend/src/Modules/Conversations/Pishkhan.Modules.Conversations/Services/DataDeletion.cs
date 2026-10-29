using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Persistence;

namespace Pishkhan.Modules.Conversations.Services;

/// <summary>
/// Meta data deletion: hard-deletes the customer's conversations, messages, channel identities and, when nothing else
/// links to it, the contact, across every workspace (the request names a channel user, not a workspace). Then confirms.
/// </summary>
internal sealed class ConversationsDataDeletionHandler(ConversationsDbContext db, ILogger<ConversationsDataDeletionHandler> logger)
    : IdempotentHandler<DataDeletionRequested, ConversationsDbContext>(db)
{
    public const string ModuleName = "conversations";

    protected override async Task HandleOnceAsync(DataDeletionRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var conversations = Db.Conversations.IgnoreQueryFilters()
            .Where(c => c.ChannelKind == message.ChannelKind && c.ExternalUserId == message.ExternalUserId);
        var found = await conversations.Select(c => new { c.Id, c.ContactId, c.ChannelAccountId }).ToListAsync(cancellationToken);
        var conversationIds = found.Select(c => c.Id).ToList();
        var contactIds = found.Select(c => c.ContactId).Distinct().ToList();
        var accountIds = found.Select(c => c.ChannelAccountId).Distinct().ToList();

        // Ingest creates the identity together with the conversation, so the conversations name every account involved.
        var identities = Db.ChannelIdentities.IgnoreQueryFilters()
            .Where(i => i.ExternalUserId == message.ExternalUserId && accountIds.Contains(i.ChannelAccountId));

        var messageIds = Db.Messages.IgnoreQueryFilters().Where(m => conversationIds.Contains(m.ConversationId)).Select(m => m.Id);
        var deleted = await Db.ExternalMessageKeys.Where(k => messageIds.Contains(k.MessageId)).ExecuteDeleteAsync(cancellationToken);
        deleted += await Db.Messages.IgnoreQueryFilters().Where(m => conversationIds.Contains(m.ConversationId)).ExecuteDeleteAsync(cancellationToken);
        deleted += await conversations.ExecuteDeleteAsync(cancellationToken);
        deleted += await identities.ExecuteDeleteAsync(cancellationToken);
        deleted += await Db.Contacts.IgnoreQueryFilters()
            .Where(c => contactIds.Contains(c.Id) && !Db.ChannelIdentities.IgnoreQueryFilters().Any(i => i.ContactId == c.Id))
            .ExecuteDeleteAsync(cancellationToken);

        logger.LogInformation("Data deletion {RequestId}: {Count} row(s) deleted", message.RequestId, deleted);
        Db.Publish(new DataDeletionCompleted(message.RequestId, ModuleName, deleted), message.RequestId.ToString());
    }
}
