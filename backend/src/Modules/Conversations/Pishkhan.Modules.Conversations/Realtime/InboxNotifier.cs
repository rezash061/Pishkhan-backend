using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Features;
using Pishkhan.Modules.Conversations.Persistence;

namespace Pishkhan.Modules.Conversations.Realtime;

public sealed record ConversationUpdatedPayload(Guid Id, string Status, Guid? AssigneeId, bool AiPaused, string? LastMessage, int UnreadCount, DateTimeOffset? LastInboundAt, DateTimeOffset? WindowExpiresAt);

public sealed record MessageCreatedPayload(Guid ConversationId, MessageResponse Message);

/// <summary><see cref="UpTo"/> true means every earlier outbound message of the conversation has this status too (read receipts).</summary>
public sealed record MessageStatusPayload(Guid ConversationId, Guid MessageId, string Status, string? ErrorCode, bool UpTo);

public sealed record HandoffPayload(Guid ConversationId, string Reason, string? SuggestedReply);

public sealed record ChannelStatusPayload(Guid ChannelAccountId, string Status, string? Reason);

/// <summary>Pushes inbox events to SignalR groups. Through the Redis backplane every Api replica's clients receive them.</summary>
public interface IInboxNotifier
{
    Task ConversationUpdatedAsync(Conversation conversation, CancellationToken ct);

    Task MessageCreatedAsync(Guid workspaceId, Message message, CancellationToken ct);

    Task MessageStatusAsync(Guid workspaceId, MessageStatusPayload payload, CancellationToken ct);

    Task HandoffRequestedAsync(Guid workspaceId, HandoffPayload payload, CancellationToken ct);

    Task ChannelStatusAsync(Guid workspaceId, ChannelStatusPayload payload, CancellationToken ct);
}

internal sealed class InboxNotifier(IHubContext<InboxHub> hub) : IInboxNotifier
{
    public Task ConversationUpdatedAsync(Conversation c, CancellationToken ct) =>
        hub.Clients.Group(InboxGroups.Workspace(c.WorkspaceId)).SendAsync(
            InboxEvents.ConversationUpdated,
            new ConversationUpdatedPayload(c.Id, c.Status, c.AssigneeId, c.AiPaused, c.LastMessagePreview, c.UnreadCount, c.LastInboundAt, c.WindowExpiresAt),
            ct);

    public Task MessageCreatedAsync(Guid workspaceId, Message message, CancellationToken ct) =>
        hub.Clients.Group(InboxGroups.Conversation(message.ConversationId)).SendAsync(
            InboxEvents.MessageCreated, new MessageCreatedPayload(message.ConversationId, ConversationEndpoints.ToResponse(message)), ct);

    public Task MessageStatusAsync(Guid workspaceId, MessageStatusPayload payload, CancellationToken ct) =>
        hub.Clients.Group(InboxGroups.Conversation(payload.ConversationId)).SendAsync(InboxEvents.MessageStatus, payload, ct);

    public Task HandoffRequestedAsync(Guid workspaceId, HandoffPayload payload, CancellationToken ct) =>
        hub.Clients.Group(InboxGroups.Workspace(workspaceId)).SendAsync(InboxEvents.HandoffRequested, payload, ct);

    public Task ChannelStatusAsync(Guid workspaceId, ChannelStatusPayload payload, CancellationToken ct) =>
        hub.Clients.Group(InboxGroups.Workspace(workspaceId)).SendAsync(InboxEvents.ChannelStatus, payload, ct);
}

/// <summary>
/// Api host, <c>realtime</c> group: turns pipeline events into SignalR pushes. Pushes are best effort and harmless to repeat,
/// so these handlers use no inbox; a client that missed something refetches on reconnect.
/// </summary>
internal sealed class RealtimeInboundHandler(ConversationsDbContext db, IInboxNotifier notifier) : IIntegrationEventHandler<InboundMessageReceived>
{
    public async Task HandleAsync(InboundMessageReceived message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var stored = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == message.MessageId && m.CreatedAt == message.MessageCreatedAt, cancellationToken);
        var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == message.ConversationId, cancellationToken);
        if (stored is null || conversation is null)
        {
            return;
        }

        await notifier.MessageCreatedAsync(conversation.WorkspaceId, stored, cancellationToken);
        await notifier.ConversationUpdatedAsync(conversation, cancellationToken);
    }
}

internal sealed class RealtimeOutboundQueuedHandler(ConversationsDbContext db, IInboxNotifier notifier) : IIntegrationEventHandler<OutboundMessageRequested>
{
    public async Task HandleAsync(OutboundMessageRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var stored = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == message.MessageId && m.CreatedAt == message.MessageCreatedAt, cancellationToken);
        if (stored is not null)
        {
            await notifier.MessageCreatedAsync(stored.WorkspaceId, stored, cancellationToken);
        }
    }
}

internal sealed class RealtimeOutboundCompletedHandler(ConversationsDbContext db, IInboxNotifier notifier) : IIntegrationEventHandler<OutboundMessageCompleted>
{
    public async Task HandleAsync(OutboundMessageCompleted message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == message.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        await notifier.MessageStatusAsync(conversation.WorkspaceId, new MessageStatusPayload(message.ConversationId, message.MessageId, message.Status, message.ErrorCode, false), cancellationToken);
        await notifier.ConversationUpdatedAsync(conversation, cancellationToken);
    }
}

internal sealed class RealtimeReadHandler(IInboxNotifier notifier) : IIntegrationEventHandler<MessagesRead>
{
    public Task HandleAsync(MessagesRead message, EventEnvelope envelope, CancellationToken cancellationToken) =>
        envelope.WorkspaceId is { } ws
            ? notifier.MessageStatusAsync(ws, new MessageStatusPayload(message.ConversationId, message.UpToMessageId, MessageStatuses.Read, null, true), cancellationToken)
            : Task.CompletedTask;
}

internal sealed class RealtimeHandoffHandler(ConversationsDbContext db, IInboxNotifier notifier) : IIntegrationEventHandler<HandoffRequested>
{
    public async Task HandleAsync(HandoffRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == message.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        await notifier.HandoffRequestedAsync(conversation.WorkspaceId, new HandoffPayload(message.ConversationId, message.Reason, message.SuggestedReply), cancellationToken);
        await notifier.ConversationUpdatedAsync(conversation, cancellationToken);
    }
}

internal sealed class RealtimeChannelStatusHandler(IInboxNotifier notifier) : IIntegrationEventHandler<ChannelStatusChanged>
{
    public Task HandleAsync(ChannelStatusChanged message, EventEnvelope envelope, CancellationToken cancellationToken) =>
        notifier.ChannelStatusAsync(message.WorkspaceId, new ChannelStatusPayload(message.ChannelAccountId, message.Status, message.Reason), cancellationToken);
}
