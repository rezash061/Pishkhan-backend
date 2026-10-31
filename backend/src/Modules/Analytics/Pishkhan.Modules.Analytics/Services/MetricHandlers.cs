using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Analytics.Domain;
using Pishkhan.Modules.Analytics.Persistence;
using Pishkhan.Modules.Conversations.Contracts;

namespace Pishkhan.Modules.Analytics.Services;

/// <summary>A customer message: counted in the hour it was received; the first one of a conversation also counts a new conversation.</summary>
internal sealed class InboundMetricsHandler(AnalyticsDbContext db) : IdempotentHandler<InboundMessageReceived, AnalyticsDbContext>(db)
{
    protected override Task HandleOnceAsync(InboundMessageReceived message, EventEnvelope envelope, CancellationToken cancellationToken) =>
        envelope.WorkspaceId is { } workspaceId
            ? Db.AddAsync(workspaceId, message.MessageCreatedAt, new MetricsDelta(MessagesReceived: 1, NewConversations: message.IsNewConversation ? 1 : 0), cancellationToken)
            : Task.CompletedTask;
}

/// <summary>A reply queued by a rule, the AI or a team member. A send deferred by the rate limit comes back with <c>deferrals</c> above 0 and is not counted again.</summary>
internal sealed class ReplyMetricsHandler(AnalyticsDbContext db) : IdempotentHandler<OutboundMessageRequested, AnalyticsDbContext>(db)
{
    protected override Task HandleOnceAsync(OutboundMessageRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.WorkspaceId is not { } workspaceId || message.Deferrals > 0)
        {
            return Task.CompletedTask;
        }

        var delta = message.HandledBy switch
        {
            HandledBy.Rule => new MetricsDelta(RepliesByRule: 1),
            HandledBy.Ai => new MetricsDelta(RepliesByAi: 1),
            _ => new MetricsDelta(RepliesByHuman: 1),
        };
        return Db.AddAsync(workspaceId, message.MessageCreatedAt, delta, cancellationToken);
    }
}

internal sealed class HandoffMetricsHandler(AnalyticsDbContext db) : IdempotentHandler<HandoffRequested, AnalyticsDbContext>(db)
{
    protected override Task HandleOnceAsync(HandoffRequested message, EventEnvelope envelope, CancellationToken cancellationToken) =>
        envelope.WorkspaceId is { } workspaceId
            ? Db.AddAsync(workspaceId, envelope.OccurredAt, new MetricsDelta(Handoffs: 1), cancellationToken)
            : Task.CompletedTask;
}
