using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Infrastructure.Timers;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Automation.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Automation.Engine;

/// <summary>
/// Engine group, per inbound DM (backend plan 9.5), in this order: a flow run waiting for this customer's answer → DM rules (postback
/// payloads, keywords, story replies and mentions, shares, welcome) by priority → the outside-business-hours rule → the AI agent (if
/// enabled and not paused for the conversation) → the fallback message. The first that answers wins.
/// </summary>
internal sealed class InboundEngineHandler(
    AutomationDbContext db,
    ITenantContext tenant,
    IConversationQueries conversations,
    AutomationCache cache,
    FlowEngine flows,
    IClock clock) : IdempotentHandler<InboundMessageReceived, AutomationDbContext>(db)
{
    protected override async Task HandleOnceAsync(InboundMessageReceived message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (tenant.WorkspaceId is not { } workspaceId
            || await conversations.GetAsync(message.ConversationId, cancellationToken) is not { } conversation
            || conversation.Status == ConversationStatuses.HandedOff)
        {
            return;
        }

        var now = clock.UtcNow;
        var settings = await cache.SettingsAsync(workspaceId, cancellationToken);
        var plan = new EnginePlan(message.ChannelAccountId, message.ChannelKind, conversation.ExternalUserId, message.MessageId, message.MessageCreatedAt);

        var waiting = await Db.FlowRuns.FirstOrDefaultAsync(r => r.ConversationId == conversation.Id && r.Status == FlowRunStatuses.WaitingInput, cancellationToken);
        if (waiting is not null)
        {
            waiting.Received(message.MessageId, message.MessageCreatedAt);
            plan.RuleId = waiting.RuleId;
            await flows.AnswerAsync(waiting, message.Text, message.Payload, conversation, settings, plan, now, cancellationToken);
            plan.Publish(Db);
            return;
        }

        var rules = await cache.RulesAsync(workspaceId, message.ChannelAccountId, cancellationToken);
        var dm = new DmInput(message.ChannelAccountId, message.Kind, message.NormalizedText ?? message.Text, message.Payload, message.IsNewConversation);
        var fired = await FireFirstAsync(RuleEvaluator.DmCandidates(rules, dm), message, conversation, settings, plan, now, cancellationToken)
            || (!settings.BusinessHours.IsOpen(now)
                && await FireFirstAsync(RuleEvaluator.OutsideHoursCandidates(rules, message.ChannelAccountId), message, conversation, settings, plan, now, cancellationToken));

        if (!fired && !conversation.AiPaused)
        {
            if (settings.AiEnabled)
            {
                Db.Publish(new ReplyRequested(conversation.Id, message.MessageId, message.MessageCreatedAt, null, null), conversation.Id.ToString());
            }
            else if (!string.IsNullOrWhiteSpace(settings.FallbackText)
                && await RuleBookkeeping.TryFireAsync(Db, RuleBookkeeping.FallbackRuleId, conversation.Id, TimeSpan.FromMinutes(settings.FallbackCooldownMinutes), now, cancellationToken))
            {
                plan.Actions.Add(ConversationAction.Send(new OutboundContent(TemplateRenderer.Render(settings.FallbackText, EngineValues.For(conversation)))));
            }
        }

        plan.Publish(Db);
    }

    private async Task<bool> FireFirstAsync(
        IReadOnlyList<RuleView> candidates, InboundMessageReceived message, ConversationSnapshot conversation, SettingsView settings, EnginePlan plan, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var rule in candidates)
        {
            if (!await RuleBookkeeping.TryFireAsync(Db, rule.Id, conversation.Id, rule.Cooldown, now, ct))
            {
                continue;
            }

            plan.RuleId = rule.Id;
            var flowId = ActionPlanner.Apply(Db, plan, rule.Actions, EngineValues.For(conversation), conversation.WorkspaceId, conversation.Id);
            var started = flowId is { } id
                && await flows.StartAsync(id, rule.Id, conversation, message.ChannelKind, message.MessageId, message.MessageCreatedAt, settings, plan, now, ct) is not null;
            await RuleBookkeeping.CountAsync(Db, conversation.WorkspaceId, rule.Id, now, ct, flowsStarted: started ? 1 : 0);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Engine group, per comment (keyed by media id): dedupe, pick the comment rule, then queue the one private reply and the public reply,
/// each paced per channel account so a viral post stays inside Meta's limits. Replies whose slot is in the future wait in
/// <c>platform.timers</c>.
/// </summary>
internal sealed class CommentEngineHandler(
    AutomationDbContext db,
    ITenantContext tenant,
    IConversationQueries conversations,
    IChannelMediaReader media,
    IChannelPacing pacing,
    AutomationCache cache,
    ICacheInvalidator invalidator,
    IClock clock,
    ILogger<CommentEngineHandler> logger) : IdempotentHandler<InboundCommentReceived, AutomationDbContext>(db)
{
    public const string ActionsTimerKind = "automation.comment-actions";
    public const string ReplyTimerKind = "automation.comment-reply";

    /// <summary>Within this much of now a reply goes out directly instead of through a timer.</summary>
    private static readonly TimeSpan DirectWithin = TimeSpan.FromSeconds(1);

    protected override async Task HandleOnceAsync(InboundCommentReceived comment, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (tenant.WorkspaceId is not { } workspaceId)
        {
            return;
        }

        var now = clock.UtcNow;
        var record = await Db.CommentEvents.FirstOrDefaultAsync(e => e.ChannelAccountId == comment.ChannelAccountId && e.CommentId == comment.CommentId, cancellationToken);

        // Seen before. A backlog run may still answer a comment that no rule matched when it arrived.
        if (record is not null && (record.RuleId is not null || comment.Source != CommentSources.Backlog))
        {
            return;
        }

        if (record is null)
        {
            record = new CommentEvent
            {
                ChannelAccountId = comment.ChannelAccountId,
                CommentId = comment.CommentId,
                WorkspaceId = workspaceId,
                MediaId = comment.MediaId,
                FromExternalId = comment.FromExternalId,
                Kind = comment.Kind,
                Source = comment.Source,
                CommentCreatedAt = comment.CreatedAt,
                ReceivedAt = now,
            };
            Db.CommentEvents.Add(record);
        }

        var rule = await ChooseRuleAsync(workspaceId, comment, cancellationToken);
        if (rule is null)
        {
            return;
        }

        record.RuleId = rule.Id;
        record.Outcome = CommentOutcomes.Replied;
        var conversation = await conversations.FindAsync(comment.ChannelAccountId, comment.FromExternalId, cancellationToken);
        var values = EngineValues.For(conversation, comment.FromUsername, comment.Text);
        var plan = new EnginePlan(comment.ChannelAccountId, comment.ChannelKind, comment.FromExternalId, null, null, comment.FromUsername) { RuleId = rule.Id };
        var spacing = await pacing.GetAsync(comment.ChannelAccountId, cancellationToken);

        var privateReplies = 0;
        var dueAt = now;
        if (rule.Actions.FirstOrDefault(a => a.Kind == ActionKinds.PrivateReply) is { Message: { } reply })
        {
            if (CommentEvent.CanPrivateReply(comment.CreatedAt, now))
            {
                dueAt = await Pacer.ReserveAsync(Db, comment.ChannelAccountId, Pacer.PrivateReply, spacing.PrivateReply, now, cancellationToken);
                if (record.ClaimPrivateReply(dueAt))
                {
                    plan.Actions.Add(ConversationAction.PrivateReply(comment.CommentId, TemplateRenderer.Render(reply, values)));
                    privateReplies = 1;
                }
            }
            else
            {
                record.Outcome = CommentOutcomes.TooOld;
            }
        }

        ActionPlanner.Apply(Db, plan, rule.Actions.Where(a => a.Kind is not (ActionKinds.SendMessage or ActionKinds.StartFlow)), values, workspaceId, conversation?.Id);
        if (plan.Actions.Count > 0)
        {
            Dispatch(plan.ToEvent(), plan.Key, ActionsTimerKind, dueAt, $"comment:{comment.ChannelAccountId:N}:{comment.CommentId}:actions", workspaceId, now);
        }

        var publicReplies = 0;
        if (rule.Actions.FirstOrDefault(a => a.Kind == ActionKinds.PublicReply) is { Replies: { Count: > 0 } variants })
        {
            var text = TemplateRenderer.Render(variants[Random.Shared.Next(variants.Count)], values);
            var publicDue = await Pacer.ReserveAsync(Db, comment.ChannelAccountId, Pacer.CommentReply, spacing.CommentReply, now, cancellationToken);
            record.PublicReplyDueAt = publicDue;
            record.PublicReplyStatus = PublicReplyStatuses.Queued;
            Dispatch(new CommentReplyRequested(comment.ChannelAccountId, comment.CommentId, comment.MediaId, text, rule.Id), comment.MediaId, ReplyTimerKind, publicDue,
                $"comment:{comment.ChannelAccountId:N}:{comment.CommentId}:public", workspaceId, now);
            publicReplies = 1;
            dueAt = publicDue > dueAt ? publicDue : dueAt;
        }

        if (dueAt - now > DirectWithin && record.Outcome == CommentOutcomes.Replied)
        {
            record.Outcome = CommentOutcomes.Queued;
        }

        record.UpdatedAt = now;
        await RuleBookkeeping.CountAsync(Db, workspaceId, rule.Id, now, cancellationToken, privateReplies: privateReplies, publicReplies: publicReplies);
    }

    private async Task<RuleView?> ChooseRuleAsync(Guid workspaceId, InboundCommentReceived comment, CancellationToken ct)
    {
        var rules = await cache.RulesAsync(workspaceId, comment.ChannelAccountId, ct);
        var input = new CommentInput(comment.ChannelAccountId, comment.Kind, comment.MediaId, comment.NormalizedText ?? comment.Text);
        foreach (var candidate in RuleEvaluator.CommentCandidates(rules, input))
        {
            if (!RuleEvaluator.NeedsPostBinding(candidate) || await BindAsync(workspaceId, candidate, comment, ct))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Binds a <c>next_post</c> rule to this post when the post was published after the rule was created.</summary>
    private async Task<bool> BindAsync(Guid workspaceId, RuleView candidate, InboundCommentReceived comment, CancellationToken ct)
    {
        var rule = await Db.Rules.FirstOrDefaultAsync(r => r.Id == candidate.Id, ct);
        if (rule is null)
        {
            return false;
        }

        if (rule.PostExternalId is not null)
        {
            return rule.PostExternalId == comment.MediaId;
        }

        var post = await media.GetMediaAsync(comment.ChannelAccountId, comment.MediaId, ct);
        if (!post.Success)
        {
            if (post.Retryable)
            {
                throw new InvalidOperationException("Media lookup failed: " + post.ErrorCode);
            }

            logger.LogInformation("Media {MediaId} unavailable ({ErrorCode}); next-post rule {RuleId} not bound", comment.MediaId, post.ErrorCode, rule.Id);
            return false;
        }

        // Instagram timestamps have whole seconds: a post from the rule's own second counts as after it.
        if (post.Value!.Timestamp is not { } publishedAt || publishedAt < rule.CreatedAt.AddTicks(-(rule.CreatedAt.Ticks % TimeSpan.TicksPerSecond)) || !rule.BindToPost(comment.MediaId))
        {
            return false;
        }

        Db.Publish(new RulesChanged(workspaceId, rule.Id, rule.Revision), workspaceId.ToString());
        invalidator.Enqueue(Db, AutomationCacheTags.Workspace(workspaceId));
        await cache.InvalidateLocalAsync(workspaceId, ct);
        return true;
    }

    private void Dispatch<TEvent>(TEvent evt, string key, string timerKind, DateTimeOffset dueAt, string dedupeKey, Guid workspaceId, DateTimeOffset now)
        where TEvent : IIntegrationEvent
    {
        if (dueAt - now <= DirectWithin)
        {
            Db.Publish(evt, key);
        }
        else
        {
            TimerScheduler.Schedule(Db, timerKind, dueAt, evt, dedupeKey, workspaceId);
        }
    }
}

/// <summary>Outbound group: posts a public reply under a comment. Rate-limited or transient failures throw, so the consumer retries with backoff.</summary>
internal sealed class CommentReplyHandler(AutomationDbContext db, IChannelMessageSender sender, IClock clock, ILogger<CommentReplyHandler> logger)
    : IdempotentHandler<CommentReplyRequested, AutomationDbContext>(db)
{
    public const int MaxAttempts = 6;

    protected override async Task HandleOnceAsync(CommentReplyRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var record = await Db.CommentEvents.FirstOrDefaultAsync(e => e.ChannelAccountId == message.ChannelAccountId && e.CommentId == message.CommentId, cancellationToken);
        if (record is { PublicReplyStatus: PublicReplyStatuses.Sent })
        {
            return;
        }

        var result = await sender.ReplyToCommentAsync(message.ChannelAccountId, message.CommentId, message.Text, cancellationToken);
        if (!result.Success && result.Retryable)
        {
            throw new InvalidOperationException("Retryable public reply failure: " + result.ErrorCode);
        }

        if (!result.Success)
        {
            logger.LogWarning("Public reply to comment {CommentId} failed: {ErrorCode}", message.CommentId, result.ErrorCode);
        }

        if (record is not null)
        {
            record.PublicReplyStatus = result.Success ? PublicReplyStatuses.Sent : PublicReplyStatuses.Failed;
            record.PublicReplyId = result.ExternalMessageId;
            record.UpdatedAt = clock.UtcNow;
        }
    }
}

/// <summary>Engine group: automation timers (paced comment replies, flow delays). Other modules' timer kinds are ignored.</summary>
internal sealed class AutomationTimerHandler(
    AutomationDbContext db,
    IConversationQueries conversations,
    AutomationCache cache,
    FlowEngine flows,
    IClock clock) : IdempotentHandler<TimerFired, AutomationDbContext>(db)
{
    protected override async Task HandleOnceAsync(TimerFired message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case CommentEngineHandler.ActionsTimerKind:
                var actions = message.Payload.Deserialize<ConversationActionsRequested>(EnvelopeJson.Options)!;
                Db.Publish(actions, ConversationActionsRequested.Key(actions.ChannelAccountId, actions.ExternalUserId));
                break;
            case CommentEngineHandler.ReplyTimerKind:
                var reply = message.Payload.Deserialize<CommentReplyRequested>(EnvelopeJson.Options)!;
                Db.Publish(reply, reply.MediaId);
                break;
            case FlowEngine.TimerKind:
                await ResumeFlowAsync(message.Payload.Deserialize<FlowTimerPayload>(EnvelopeJson.Options)!, cancellationToken);
                break;
        }
    }

    private async Task ResumeFlowAsync(FlowTimerPayload payload, CancellationToken ct)
    {
        var run = await Db.FlowRuns.FirstOrDefaultAsync(r => r.Id == payload.RunId, ct);
        if (run is not { Status: FlowRunStatuses.WaitingTimer } || run.CurrentNode != payload.NodeId)
        {
            return;
        }

        var now = clock.UtcNow;
        var conversation = await conversations.GetAsync(run.ConversationId, ct);
        if (conversation is null || conversation.Status == ConversationStatuses.HandedOff)
        {
            run.Cancel(now);
            return;
        }

        var settings = await cache.SettingsAsync(run.WorkspaceId, ct);
        var plan = new EnginePlan(run.ChannelAccountId, run.ChannelKind, run.ExternalUserId, null, null) { RuleId = run.RuleId };
        await flows.TimerAsync(run, payload.NodeId, conversation, settings, plan, now, ct);
        plan.Publish(Db);
    }
}

/// <summary>
/// Engine group: one page of a post's existing comments for a backlog run. Comments within Meta's private reply age go to the comment
/// engine (which dedupes and paces); paging stops at the first page that reaches older comments.
/// </summary>
internal sealed class BacklogPageHandler(AutomationDbContext db, IChannelMediaReader media, IChannelDirectory channels, IClock clock)
    : IdempotentHandler<BacklogPageRequested, AutomationDbContext>(db)
{
    public const int MaxPages = 200;

    protected override async Task HandleOnceAsync(BacklogPageRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var run = await Db.BacklogRuns.FirstOrDefaultAsync(r => r.Id == message.BacklogRunId, cancellationToken);
        if (run is not { Status: BacklogStatuses.Running })
        {
            return;
        }

        var now = clock.UtcNow;
        var account = await channels.GetAsync(run.ChannelAccountId, cancellationToken);
        var page = account is null ? null : await media.ListCommentsAsync(run.ChannelAccountId, run.MediaId, message.After, cancellationToken);
        if (page is null || !page.Success)
        {
            if (page is { Retryable: true })
            {
                throw new InvalidOperationException("Comment listing failed: " + page.ErrorCode);
            }

            run.Status = BacklogStatuses.Failed;
            run.Error = page?.ErrorCode ?? "channel.unavailable";
            run.CompletedAt = now;
            return;
        }

        run.Pages++;
        var reachedOld = false;
        foreach (var c in page.Value!.Items)
        {
            run.CommentsSeen++;
            if (c.FromExternalId is null || c.FromExternalId == account!.ExternalId)
            {
                continue;
            }

            var createdAt = c.Timestamp ?? now;
            if (!CommentEvent.CanPrivateReply(createdAt, now))
            {
                reachedOld = true;
                continue;
            }

            Db.Publish(
                new InboundCommentReceived(
                    run.ChannelAccountId, account.Kind, CommentKinds.Comment, c.Id, run.MediaId, c.ParentId, c.FromExternalId, c.FromUsername,
                    c.Text, c.Text is null ? null : PersianNormalizer.Normalize(c.Text), createdAt, CommentSources.Backlog),
                run.MediaId);
            run.CommentsQueued++;
        }

        if (page.Value.NextCursor is { } next && !reachedOld && run.Pages < MaxPages)
        {
            Db.Publish(new BacklogPageRequested(run.Id, next), run.Id.ToString());
        }
        else
        {
            run.Status = BacklogStatuses.Completed;
            run.CompletedAt = now;
        }
    }
}

/// <summary>Platform group: Meta data deletion. Removes the customer's comment records and flow runs in every workspace.</summary>
internal sealed class AutomationDataDeletionHandler(AutomationDbContext db) : IdempotentHandler<DataDeletionRequested, AutomationDbContext>(db)
{
    public const string ModuleName = "automation";

    protected override async Task HandleOnceAsync(DataDeletionRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var comments = await Db.CommentEvents.IgnoreQueryFilters().Where(e => e.FromExternalId == message.ExternalUserId).ExecuteDeleteAsync(cancellationToken);
        var runs = await Db.FlowRuns.IgnoreQueryFilters()
            .Where(r => r.ExternalUserId == message.ExternalUserId && r.ChannelKind == message.ChannelKind)
            .ExecuteDeleteAsync(cancellationToken);
        Db.Publish(new DataDeletionCompleted(message.RequestId, ModuleName, comments + runs), message.RequestId.ToString());
    }
}
