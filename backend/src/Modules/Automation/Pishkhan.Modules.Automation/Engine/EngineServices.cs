using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Infrastructure.Timers;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Automation.Persistence;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Persian;

namespace Pishkhan.Modules.Automation.Engine;

public static class AutomationCacheTags
{
    public static string Workspace(Guid workspaceId) => $"automation:{workspaceId:N}";
}

/// <summary>
/// Compiled rule sets per channel account and the workspace settings, in HybridCache for 30 minutes (backend plan 5:
/// <c>auto:{ws}:{channelAccount}:rules</c>). Every rule or settings change invalidates the workspace's tag on all hosts.
/// A miss loads through its own scope: HybridCache may run the factory on another task, outside the caller's transaction.
/// </summary>
internal sealed class AutomationCache(HybridCache cache, IServiceScopeFactory scopes)
{
    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromMinutes(30), LocalCacheExpiration = TimeSpan.FromMinutes(5) };

    public async Task<IReadOnlyList<RuleView>> RulesAsync(Guid workspaceId, Guid channelAccountId, CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            $"auto:{workspaceId:N}:{channelAccountId:N}:rules",
            (scopes, workspaceId, channelAccountId),
            static async (s, token) =>
            {
                await using var scope = s.scopes.CreateAsyncScope();
                var rules = await Db(scope, s.workspaceId).Rules.AsNoTracking()
                    .Where(r => r.WorkspaceId == s.workspaceId && r.Enabled && (r.ChannelAccountId == null || r.ChannelAccountId == s.channelAccountId))
                    .ToListAsync(token);
                return rules.Select(r => r.ToView()).ToList();
            },
            Entry,
            [AutomationCacheTags.Workspace(workspaceId)],
            ct);

    public async Task<SettingsView> SettingsAsync(Guid workspaceId, CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            $"auto:{workspaceId:N}:settings",
            (scopes, workspaceId),
            static async (s, token) =>
            {
                await using var scope = s.scopes.CreateAsyncScope();
                var row = await Db(scope, s.workspaceId).Settings.AsNoTracking().FirstOrDefaultAsync(x => x.WorkspaceId == s.workspaceId, token);
                return (row ?? AutomationSettings.Default(s.workspaceId)).ToView();
            },
            Entry,
            [AutomationCacheTags.Workspace(workspaceId)],
            ct);

    private static AutomationDbContext Db(AsyncServiceScope scope, Guid workspaceId)
    {
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(workspaceId);
        return scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
    }

    public ValueTask InvalidateLocalAsync(Guid workspaceId, CancellationToken ct) => cache.RemoveByTagAsync(AutomationCacheTags.Workspace(workspaceId), ct);
}

/// <summary>
/// Spreads calls of one kind per channel account over time (viral posts, backend plan 11): each call reserves the next free slot,
/// one pacing interval after the previous one. The row lock serializes reservations across replicas and partitions.
/// </summary>
internal static class Pacer
{
    public const string PrivateReply = "private_reply";
    public const string CommentReply = "comment_reply";

    public static async Task<DateTimeOffset> ReserveAsync(AutomationDbContext db, Guid channelAccountId, string family, TimeSpan interval, DateTimeOffset now, CancellationToken ct)
    {
        var slots = await db.Database.SqlQuery<DateTimeOffset>(
            $"""
            INSERT INTO automation.pacing (channel_account_id, family, next_slot_at) VALUES ({channelAccountId}, {family}, {now + interval})
            ON CONFLICT (channel_account_id, family) DO UPDATE SET next_slot_at = GREATEST(automation.pacing.next_slot_at, {now}) + {interval}
            RETURNING next_slot_at - {interval} AS "Value"
            """).ToListAsync(ct);
        return slots[0];
    }
}

internal static class RuleBookkeeping
{
    /// <summary>Fallback messages count as a rule with this id for cooldowns.</summary>
    public static readonly Guid FallbackRuleId = Guid.Empty;

    /// <summary>Records that the rule fired for the conversation; false (and nothing recorded) while it is cooling down.</summary>
    public static async Task<bool> TryFireAsync(AutomationDbContext db, Guid ruleId, Guid conversationId, TimeSpan cooldown, DateTimeOffset now, CancellationToken ct)
    {
        if (cooldown <= TimeSpan.Zero)
        {
            return true;
        }

        var cutoff = now - cooldown;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO automation.rule_firings (rule_id, conversation_id, last_fired_at) VALUES ({ruleId}, {conversationId}, {now})
            ON CONFLICT (rule_id, conversation_id) DO UPDATE SET last_fired_at = excluded.last_fired_at
            WHERE automation.rule_firings.last_fired_at <= {cutoff}
            """,
            ct);
        return changed > 0;
    }

    public static Task CountAsync(AutomationDbContext db, Guid workspaceId, Guid ruleId, DateTimeOffset now, CancellationToken ct, int fired = 1, int privateReplies = 0, int publicReplies = 0, int flowsStarted = 0)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO automation.rule_stats (rule_id, day, workspace_id, fired, private_replies, public_replies, flows_started)
            VALUES ({ruleId}, {day}, {workspaceId}, {fired}, {privateReplies}, {publicReplies}, {flowsStarted})
            ON CONFLICT (rule_id, day) DO UPDATE SET
                fired = automation.rule_stats.fired + excluded.fired,
                private_replies = automation.rule_stats.private_replies + excluded.private_replies,
                public_replies = automation.rule_stats.public_replies + excluded.public_replies,
                flows_started = automation.rule_stats.flows_started + excluded.flows_started
            """,
            ct);
    }
}

/// <summary>Variables for message templates (see <see cref="TemplateRenderer"/>).</summary>
internal static class EngineValues
{
    public static Dictionary<string, string?> For(ConversationSnapshot? conversation, string? commentUsername = null, string? commentText = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["contact.name"] = conversation?.ContactName,
            ["contact.first_name"] = TemplateRenderer.FirstName(conversation?.ContactName),
            ["contact.username"] = conversation?.ContactUsername ?? commentUsername,
            ["comment.username"] = commentUsername,
            ["comment.text"] = commentText,
        };
        foreach (var (k, v) in conversation?.Fields ?? new Dictionary<string, string>())
        {
            values["field." + k] = v;
        }

        return values;
    }
}

/// <summary>
/// The engine's decision for one customer, collected while rules and flows run and published as one
/// <see cref="ConversationActionsRequested"/> (Conversations applies it in one transaction).
/// </summary>
internal sealed class EnginePlan(Guid channelAccountId, string channelKind, string externalUserId, Guid? inboundMessageId, DateTimeOffset? inboundMessageCreatedAt, string? username = null)
{
    public List<ConversationAction> Actions { get; } = [];

    public Guid? RuleId { get; set; }

    public Guid? FlowRunId { get; set; }

    /// <summary>True when something answered the inbound message (it is then marked handled even if no message is queued).</summary>
    public bool Handled { get; set; }

    public ConversationActionsRequested ToEvent() =>
        new(channelAccountId, channelKind, externalUserId, HandledBy.Rule, RuleId, FlowRunId, inboundMessageId, inboundMessageCreatedAt, [.. Actions], username);

    public string Key => ConversationActionsRequested.Key(channelAccountId, externalUserId);

    /// <summary>Card sets to send, by the position their message takes among <see cref="Actions"/>; filled in by <see cref="PublishAsync"/>.</summary>
    private readonly List<(int Position, Guid CardSetId)> _cards = [];

    public void AddCards(Guid cardSetId) => _cards.Add((Actions.Count + _cards.Count, cardSetId));

    /// <summary>Builds the pending carousels from the catalog's current prices and stock, then publishes the plan.</summary>
    public async Task PublishAsync(AutomationDbContext db, ICatalogQueries catalog, CancellationToken ct)
    {
        foreach (var (position, cardSetId) in _cards)
        {
            var set = await catalog.GetCardSetAsync(cardSetId, ct);
            var content = set is { Cards.Count: > 0 } ? CardMessages.ToContent(set) : null;
            if (content is not null)
            {
                Actions.Insert(Math.Min(position, Actions.Count), ConversationAction.Send(content));
            }
        }

        _cards.Clear();
        if (Actions.Count > 0 || (Handled && inboundMessageId is not null))
        {
            db.Publish(ToEvent(), Key);
        }
    }
}

/// <summary>Catalog content as channel messages.</summary>
internal static class CardMessages
{
    public static OutboundContent ToContent(StorefrontCardSetContent set) => new(
        null,
        Cards: [.. set.Cards.Select(c => new MessageCard(
            c.Title, c.Subtitle, c.ImageUrl, c.LinkUrl,
            c.LinkUrl is { } link ? [MessageButton.Postback(c.ButtonTitle, c.Payload), MessageButton.Link("مشاهده در سایت", link)] : [MessageButton.Postback(c.ButtonTitle, c.Payload)]))]);

    /// <summary>The reply to a card's button: name, description, price per variant and stock, from the catalog (never from the AI).</summary>
    public static OutboundContent ProductDetails(ProductSummary p)
    {
        var lines = new List<string> { p.Title };
        if (p.Description is { Length: > 0 } description)
        {
            lines.Add(description.Length <= 300 ? description : description[..299] + "…");
        }

        var priced = p.Variants.Where(v => v.Attributes.Count > 0).ToList();
        if (priced.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(priced.Take(10).Select(v =>
                $"• {string.Join("، ", v.Attributes.Select(a => $"{a.Key}: {a.Value}"))}: {PersianMoney.FormatToman(v.Price)}{(v.InStock ? string.Empty : " (ناموجود)")}"));
        }
        else if (p.MinPrice is { } price)
        {
            lines.Add("قیمت: " + PersianMoney.FormatToman(price) + (p.InStock ? string.Empty : " (ناموجود)"));
        }

        var buttons = p.LinkUrl is { } link ? new[] { MessageButton.Link("مشاهده در سایت", link) } : null;
        return new OutboundContent(string.Join('\n', lines).Trim(), Buttons: buttons);
    }
}

/// <summary>Turns rule actions into the plan. Comment replies are handled by the comment engine, which paces them.</summary>
internal static class ActionPlanner
{
    /// <summary>Applies the actions; returns the flow to start, if any.</summary>
    public static Guid? Apply(AutomationDbContext db, EnginePlan plan, IEnumerable<RuleAction> actions, IReadOnlyDictionary<string, string?> values, Guid workspaceId, Guid? conversationId)
    {
        Guid? flow = null;
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case ActionKinds.SendMessage when action.Message is { } message:
                    plan.Actions.Add(ConversationAction.Send(TemplateRenderer.Render(message, values)));
                    break;
                case ActionKinds.SendCards when action.CardSetId is { } cardSetId:
                    plan.AddCards(cardSetId);
                    break;
                case ActionKinds.AddTag:
                    plan.Actions.Add(new ConversationAction(ConversationActionKinds.AddTag, Tag: action.Tag));
                    break;
                case ActionKinds.RemoveTag:
                    plan.Actions.Add(new ConversationAction(ConversationActionKinds.RemoveTag, Tag: action.Tag));
                    break;
                case ActionKinds.SetField:
                    plan.Actions.Add(new ConversationAction(ConversationActionKinds.SetField, Field: action.Field, Value: action.Value is null ? null : TemplateRenderer.Render(action.Value, values)));
                    break;
                case ActionKinds.Handoff:
                    plan.Actions.Add(new ConversationAction(ConversationActionKinds.Handoff, Reason: action.Reason ?? "automation"));
                    break;
                case ActionKinds.PauseAi:
                    plan.Actions.Add(new ConversationAction(ConversationActionKinds.PauseAi));
                    break;
                case ActionKinds.NotifyTeam:
                    var link = conversationId is { } c ? $"/inbox/{c}" : null;
                    db.Publish(
                        new NotificationRequested(workspaceId, null, "automation", action.Title ?? "پیام نیازمند پیگیری", action.Value is null ? null : TemplateRenderer.Render(action.Value, values), link),
                        workspaceId.ToString());
                    break;
                case ActionKinds.StartFlow when action.FlowId is { } id:
                    flow ??= id;
                    break;
            }
        }

        plan.Handled = true;
        return flow;
    }
}

internal sealed record FlowTimerPayload(Guid RunId, string NodeId);

/// <summary>Starts and advances flow runs, and turns their effects into the plan, timers and AI hand-offs.</summary>
internal sealed class FlowEngine(AutomationDbContext db)
{
    public const string TimerKind = "automation.flow-delay";

    public async Task<FlowRun?> StartAsync(
        Guid flowId, Guid? ruleId, ConversationSnapshot conversation, string channelKind, Guid? messageId, DateTimeOffset? messageCreatedAt,
        SettingsView settings, EnginePlan plan, DateTimeOffset now, CancellationToken ct)
    {
        var flow = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.Id == flowId && f.PublishedVersion != null, ct);
        if (flow is null)
        {
            return null;
        }

        var version = await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == flowId && v.Version == flow.PublishedVersion, ct);

        // One active run per conversation: a new flow replaces the one in progress.
        await db.FlowRuns
            .Where(r => r.ConversationId == conversation.Id && FlowRunStatuses.Active.Contains(r.Status))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, FlowRunStatuses.Cancelled).SetProperty(r => r.EndedAt, now).SetProperty(r => r.UpdatedAt, now), ct);

        var run = FlowRun.Start(conversation.WorkspaceId, flowId, version.Version, conversation.Id, conversation.ChannelAccountId, channelKind, conversation.ExternalUserId, ruleId, now);
        if (messageId is { } id && messageCreatedAt is { } at)
        {
            run.Received(id, at);
        }

        db.FlowRuns.Add(run);
        Apply(FlowRunner.Start(version.Graph, run, Context(conversation, settings, now)), run, conversation, plan);
        return run;
    }

    public async Task AnswerAsync(FlowRun run, string? text, string? payload, ConversationSnapshot conversation, SettingsView settings, EnginePlan plan, DateTimeOffset now, CancellationToken ct)
    {
        var graph = await GraphAsync(run, ct);
        Apply(FlowRunner.Answer(graph, run, text, payload, Context(conversation, settings, now)), run, conversation, plan);
    }

    public async Task TimerAsync(FlowRun run, string nodeId, ConversationSnapshot conversation, SettingsView settings, EnginePlan plan, DateTimeOffset now, CancellationToken ct)
    {
        var graph = await GraphAsync(run, ct);
        Apply(FlowRunner.TimerFired(graph, run, nodeId, Context(conversation, settings, now)), run, conversation, plan);
    }

    private async Task<FlowGraph> GraphAsync(FlowRun run, CancellationToken ct) =>
        (await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == run.FlowId && v.Version == run.FlowVersion, ct)).Graph;

    private static FlowContext Context(ConversationSnapshot conversation, SettingsView settings, DateTimeOffset now) =>
        new(now, conversation.IsWindowOpen(now), conversation.Tags, conversation.Fields, EngineValues.For(conversation), settings.BusinessHours);

    private void Apply(IReadOnlyList<FlowEffect> effects, FlowRun run, ConversationSnapshot conversation, EnginePlan plan)
    {
        plan.FlowRunId = run.Id;
        plan.Handled = true;
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case FlowEffect.Send send:
                    plan.Actions.Add(ConversationAction.Send(send.Content));
                    break;
                case FlowEffect.SendCards cards:
                    plan.AddCards(cards.CardSetId);
                    break;
                case FlowEffect.Act act:
                    ActionPlanner.Apply(db, plan, act.Actions, EngineValues.For(conversation), conversation.WorkspaceId, conversation.Id);
                    break;
                case FlowEffect.Timer timer:
                    TimerScheduler.Schedule(db, TimerKind, timer.DueAt, new FlowTimerPayload(run.Id, timer.NodeId), $"flow:{run.Id:N}:{timer.NodeId}", conversation.WorkspaceId);
                    break;
                case FlowEffect.HandToAi ai when run.LastMessageId is { } messageId:
                    db.Publish(new ReplyRequested(conversation.Id, messageId, run.LastMessageCreatedAt!.Value, ai.Goal, run.Id), conversation.Id.ToString());
                    break;
            }
        }
    }
}
