using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Agent.Replies;

/// <summary>
/// AI group: answers a customer turn (backend plan 10.2). Skips a request that is no longer current (a newer message, a reply already
/// sent, the AI paused or the conversation handed off, the window closed). The reply or handoff goes to Conversations as actions handled
/// by <c>ai</c>; a sent reply counts toward the plan's AI meters; the trace is stored.
/// </summary>
internal sealed class ReplyRequestedHandler(
    AgentDbContext db,
    IConversationQueries conversations,
    ReplyPipeline pipeline,
    IEntitlements entitlements,
    ITenantContext tenant,
    IClock clock,
    ILogger<ReplyRequestedHandler> logger) : IdempotentHandler<ReplyRequested, AgentDbContext>(db)
{
    public const int HistoryLimit = 30;

    protected override async Task HandleOnceAsync(ReplyRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (tenant.WorkspaceId is not { } workspaceId
            || await conversations.GetAsync(message.ConversationId, cancellationToken) is not { } conversation
            || conversation.AiPaused
            || conversation.Status is ConversationStatuses.HandedOff or ConversationStatuses.Closed
            || !conversation.IsWindowOpen(now))
        {
            return;
        }

        var history = await conversations.RecentMessagesAsync(conversation.Id, HistoryLimit, cancellationToken);
        var index = history.ToList().FindIndex(m => m.Id == message.MessageId);
        if (index < 0 || index < history.Count - 1)
        {
            // Gone from the recent history, or the customer wrote again (that request answers the whole turn) or someone replied.
            logger.LogDebug("Reply request for {MessageId} is no longer current", message.MessageId);
            return;
        }

        var start = index;
        while (start > 0 && history[start - 1].Direction == MessageDirections.Inbound)
        {
            start--;
        }

        var turnMessages = history.Skip(start).Take(index - start + 1).ToList();
        var turn = string.Join('\n', turnMessages.Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        var profile = await Db.Profiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? AgentProfile.Default(workspaceId);

        ReplyDecision decision;
        if (turn.Length == 0)
        {
            // Only media (a photo of a product, a receipt): a person looks at it until image understanding arrives (Phase 2).
            decision = Bare(conversation, message, profile, "media", AiReplyOutcomes.HandedOff);
        }
        else if (await OverLimitAsync(workspaceId, cancellationToken))
        {
            decision = Bare(conversation, message, profile, "plan_limit", AiReplyOutcomes.Failed);
        }
        else
        {
            var previous = await Db.Traces.AsNoTracking().Where(t => t.ConversationId == conversation.Id)
                .OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            decision = await pipeline.RunAsync(
                new ReplyInput(conversation, message.MessageId, turn, [.. history.Take(start)], message.Goal, message.FlowRunId, previous), profile, cancellationToken);
        }

        var actions = new List<ConversationAction>();
        if (decision.Outcome == AiReplyOutcomes.Sent)
        {
            actions.Add(ConversationAction.Send(new OutboundContent(decision.Reply)));
            Db.Publish(new UsageRecorded(workspaceId, BillingMeters.AiMessages, 1, now), workspaceId.ToString());
            Db.Publish(new UsageRecorded(workspaceId, BillingMeters.AiConversations, 1, now, conversation.Id), workspaceId.ToString());
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(profile.HandoffMessage))
            {
                actions.Add(ConversationAction.Send(new OutboundContent(profile.HandoffMessage)));
            }

            actions.Add(new ConversationAction(ConversationActionKinds.Handoff, Reason: "ai:" + decision.HandoffReason, SuggestedReply: decision.SuggestedReply));
        }

        Db.Publish(
            new ConversationActionsRequested(
                conversation.ChannelAccountId, conversation.ChannelKind, conversation.ExternalUserId, HandledBy.Ai, null, message.FlowRunId,
                message.MessageId, message.MessageCreatedAt, actions),
            ConversationActionsRequested.Key(conversation.ChannelAccountId, conversation.ExternalUserId));

        if (decision.UnansweredQuestion is { Length: > 0 } question)
        {
            Db.Publish(new UnansweredQuestionRaised(workspaceId, conversation.Id, message.MessageId, question, now), workspaceId.ToString());
        }

        Db.Traces.Add(decision.Trace);
    }

    private async Task<bool> OverLimitAsync(Guid workspaceId, CancellationToken ct)
    {
        var plan = await entitlements.GetAsync(workspaceId, ct);
        if (plan.Limit(BillingMeters.AiMessages) is null)
        {
            return false;
        }

        var used = await entitlements.GetUsageAsync(workspaceId, BillingMeters.AiMessages, ct);
        return !plan.Allows(BillingMeters.AiMessages, used + 1);
    }

    private ReplyDecision Bare(ConversationSnapshot conversation, ReplyRequested message, AgentProfile profile, string reason, string outcome) =>
        new(outcome, null, reason, null, null, new AiTrace
        {
            Id = Uuid.NewV7(clock.UtcNow),
            WorkspaceId = conversation.WorkspaceId,
            ConversationId = conversation.Id,
            MessageId = message.MessageId,
            FlowRunId = message.FlowRunId,
            Goal = message.Goal,
            Threshold = profile.ConfidenceThreshold,
            Outcome = outcome,
            HandoffReason = reason,
            CreatedAt = clock.UtcNow,
        });
}
