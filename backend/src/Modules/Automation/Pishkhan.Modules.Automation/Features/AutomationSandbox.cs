using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Automation.Engine;
using Pishkhan.Modules.Automation.Persistence;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Automation.Features;

/// <summary>The customer a test runs as. Variables such as <c>{{contact.first_name}}</c> and flow conditions read it.</summary>
public sealed record SandboxContact(string? Name, string? Username, IReadOnlyList<string>? Tags, IReadOnlyDictionary<string, string>? Fields);

/// <summary>A customer message in a test: text, or a quick reply / button payload.</summary>
public sealed record SandboxMessage(string? Text, string? Payload);

/// <summary>
/// A sample event. <c>source</c> is <c>dm</c> (default) or <c>comment</c>. For a DM, <c>kind</c> is an inbound kind (<c>text</c>,
/// <c>story_reply</c>, <c>story_mention</c>, <c>share</c>); for a comment, <c>comment</c> or <c>live_comment</c>. <c>at</c> is the time
/// to judge business hours by (default now). <c>ruleId</c> tests one rule, even a disabled one, instead of all enabled rules.
/// </summary>
public sealed record RuleTestRequest(
    string? Text,
    string? Payload = null,
    string? Source = null,
    string? Kind = null,
    Guid? ChannelAccountId = null,
    string? MediaId = null,
    bool IsNewConversation = false,
    DateTimeOffset? At = null,
    Guid? RuleId = null,
    SandboxContact? Contact = null);

/// <summary>
/// One thing the engine would do. <c>type</c>: <c>send_message</c>, <c>send_cards</c>, <c>private_reply</c>, <c>public_reply</c>,
/// <c>add_tag</c>, <c>remove_tag</c>, <c>set_field</c>, <c>handoff</c>, <c>pause_ai</c>, <c>notify_team</c>, <c>start_flow</c>,
/// <c>delay</c> (a flow waits until <c>until</c>), <c>ai</c> (a flow hands the conversation to the AI with <c>goal</c>).
/// </summary>
public sealed record SandboxStep(
    string Type,
    OutboundContent? Message = null,
    IReadOnlyList<string>? Replies = null,
    Guid? CardSetId = null,
    string? Tag = null,
    string? Field = null,
    string? Value = null,
    string? Reason = null,
    string? Title = null,
    Guid? FlowId = null,
    DateTimeOffset? Until = null,
    string? Goal = null);

/// <summary>
/// <c>outcome</c>: <c>rule</c> (a matching rule), <c>outside_hours</c> (the outside-hours rule), <c>product_details</c> (a card button),
/// <c>ai</c> (the AI agent would answer), <c>fallback</c> (the fallback message), <c>none</c> (nothing would be sent).
/// <c>flow</c> is the flow the rule starts, run up to its first wait.
/// </summary>
public sealed record RuleTestResponse(
    string Outcome, Guid? RuleId, string? RuleName, bool BusinessOpen, IReadOnlyList<Guid> OtherMatches, IReadOnlyList<SandboxStep> Steps, FlowTestResponse? Flow);

/// <summary><c>answers</c> are the customer's replies, in order. <c>published</c> runs the published version instead of the draft.</summary>
public sealed record FlowTestRequest(IReadOnlyList<SandboxMessage>? Answers, bool Published = false, DateTimeOffset? At = null, SandboxContact? Contact = null);

/// <summary>What the flow did on start (<c>input</c> null) and after each answer.</summary>
public sealed record FlowTestTurn(SandboxMessage? Input, IReadOnlyList<SandboxStep> Steps);

/// <summary><c>status</c> is the run's state at the end (for example <c>waiting_input</c> or <c>completed</c>); <c>currentNode</c> is where it stopped.</summary>
public sealed record FlowTestResponse(Guid FlowId, int? Version, string Status, string? CurrentNode, IReadOnlyDictionary<string, string> Variables, IReadOnlyList<FlowTestTurn> Turns);

/// <summary>
/// Dry runs for the panel's «test» pane (plan 3.6): the same matching, ordering and flow runner as live events, with no conversation,
/// no messages sent, nothing stored, and no cooldowns. Card sets are named by id; their cards are built only when really sent.
/// </summary>
internal static class AutomationSandbox
{
    public const int MaxAnswers = 30;
    public const int MaxText = 1000;

    /// <summary>Delays fired in a row before the test stops, on top of the runner's own step cap.</summary>
    private const int MaxTimers = 20;

    public static void Map(RouteGroupBuilder root)
    {
        root.MapPost("/test", TestRulesAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Dry-run the rules against a sample message or comment")
            .WithDescription("Shows which rule would answer (or the AI, the fallback, nothing) and what it would send, with variables filled in. Nothing is sent or stored, and cooldowns are ignored.")
            .Produces<RuleTestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        root.MapPost("/flows/{flowId:guid}/test", TestFlowAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Dry-run a flow with sample answers")
            .WithDescription("Runs the draft (or `published`) from its start, then feeds `answers` one at a time while it waits for input. Delays end at once. An invalid draft is refused with 422 and the validation result.")
            .Produces<FlowTestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();
    }

    private static async Task<IResult> TestRulesAsync(Guid workspaceId, RuleTestRequest request, AutomationDbContext db, AutomationCache cache, ICatalogQueries catalog, IClock clock, CancellationToken ct)
    {
        var source = request.Source ?? "dm";
        var kind = request.Kind ?? (source == "comment" ? CommentKinds.Comment : InboundKinds.Text);
        var errors = new List<(string, string)>();
        if (source is not ("dm" or "comment"))
        {
            errors.Add(("source", "automation.test_source_invalid"));
        }
        else if (source == "dm" ? kind is not (InboundKinds.Text or InboundKinds.StoryReply or InboundKinds.StoryMention or InboundKinds.Share) : kind is not (CommentKinds.Comment or CommentKinds.LiveComment))
        {
            errors.Add(("kind", "automation.test_kind_invalid"));
        }

        if (request.Text is { Length: > MaxText } || request.Payload is { Length: > MaxText })
        {
            errors.Add(("text", "validation.text.length"));
        }

        if (ValidateContact(request.Contact) is { } contactError)
        {
            errors.Add(contactError);
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var query = db.Rules.AsNoTracking();
        query = request.RuleId is { } only ? query.Where(r => r.Id == only) : query.Where(r => r.Enabled);
        var rules = (await query.ToListAsync(ct)).Select(r => r.ToView()).ToList();
        if (request.RuleId is not null && rules.Count == 0)
        {
            return Error.NotFound().ToProblem();
        }

        var names = await db.Rules.AsNoTracking().Where(r => rules.Select(v => v.Id).Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);

        // Without an account every rule applies, as if the message came to the account each rule is for.
        var account = request.ChannelAccountId ?? Guid.Empty;
        if (request.ChannelAccountId is null)
        {
            rules = [.. rules.Select(r => r with { ChannelAccountId = null })];
        }

        var now = request.At ?? clock.UtcNow;
        var settings = await cache.SettingsAsync(workspaceId, ct);
        var open = settings.BusinessHours.IsOpen(now);
        var values = Values(request.Contact);

        IReadOnlyList<RuleView> candidates;
        if (source == "comment")
        {
            // Without a post id, post-scoped rules are judged as if the comment were on their post.
            var comment = new CommentInput(account, kind, request.MediaId ?? string.Empty, request.Text);
            candidates = RuleEvaluator.CommentCandidates(request.MediaId is null ? rules.Select(r => r with { PostId = null, Trigger = r.Trigger with { Scope = CommentScopes.AllPosts } }) : rules, comment);
            values["comment.username"] = request.Contact?.Username;
            values["comment.text"] = request.Text;
        }
        else
        {
            candidates = RuleEvaluator.DmCandidates(rules, new DmInput(account, kind, request.Text, request.Payload, request.IsNewConversation));
        }

        var outcome = "rule";
        if (candidates.Count == 0 && source == "dm" && !open)
        {
            candidates = RuleEvaluator.OutsideHoursCandidates(rules, account);
            outcome = "outside_hours";
        }

        if (candidates.Count > 0)
        {
            var rule = candidates[0];
            var steps = Steps(rule.Actions, values);
            FlowTestResponse? flow = null;
            if (source == "dm" && rule.Actions.FirstOrDefault(a => a.Kind == ActionKinds.StartFlow && a.FlowId is not null) is { FlowId: { } flowId }
                && await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.Id == flowId && f.PublishedVersion != null, ct) is { } published)
            {
                var version = await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == flowId && v.Version == published.PublishedVersion, ct);
                flow = Run(version.Graph, flowId, version.Version, [], request.Contact, settings, now);
            }

            return Results.Ok(new RuleTestResponse(outcome, rule.Id, names.GetValueOrDefault(rule.Id), open, [.. candidates.Skip(1).Select(r => r.Id)], steps, flow));
        }

        if (source == "comment")
        {
            return Results.Ok(new RuleTestResponse("none", null, null, open, [], [], null));
        }

        if (CatalogPayloads.TryParseProduct(request.Payload, out var productId) && await catalog.GetProductAsync(productId, ct) is { Status: ProductStatuses.Active } product)
        {
            return Results.Ok(new RuleTestResponse("product_details", null, null, open, [], [new SandboxStep(ActionKinds.SendMessage, CardMessages.ProductDetails(product))], null));
        }

        if (settings.AiEnabled)
        {
            return Results.Ok(new RuleTestResponse("ai", null, null, open, [], [], null));
        }

        return string.IsNullOrWhiteSpace(settings.FallbackText)
            ? Results.Ok(new RuleTestResponse("none", null, null, open, [], [], null))
            : Results.Ok(new RuleTestResponse("fallback", null, null, open, [], [new SandboxStep(ActionKinds.SendMessage, new OutboundContent(TemplateRenderer.Render(settings.FallbackText, values)))], null));
    }

    private static async Task<IResult> TestFlowAsync(Guid workspaceId, Guid flowId, FlowTestRequest request, AutomationDbContext db, AutomationCache cache, IClock clock, CancellationToken ct)
    {
        var answers = request.Answers ?? [];
        var errors = new List<(string, string)>();
        if (answers.Count > MaxAnswers)
        {
            errors.Add(("answers", "automation.test_too_many_answers"));
        }

        if (answers.Any(a => a is null || a.Text is { Length: > MaxText } || a.Payload is { Length: > MaxText }))
        {
            errors.Add(("answers", "validation.text.length"));
        }

        if (ValidateContact(request.Contact) is { } contactError)
        {
            errors.Add(contactError);
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var flow = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.Id == flowId, ct);
        if (flow is null)
        {
            return Error.NotFound().ToProblem();
        }

        FlowGraph graph;
        int? version = null;
        if (request.Published)
        {
            if (flow.PublishedVersion is not { } published)
            {
                return Error.NotFound("automation.flow_not_published").ToProblem();
            }

            graph = (await db.FlowVersions.AsNoTracking().FirstAsync(v => v.FlowId == flowId && v.Version == published, ct)).Graph;
            version = published;
        }
        else
        {
            var validation = FlowValidator.Validate(flow.Draft);
            if (!validation.IsValid)
            {
                return Results.Json(validation, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            graph = flow.Draft;
        }

        var settings = await cache.SettingsAsync(workspaceId, ct);
        return Results.Ok(Run(graph, flowId, version, answers, request.Contact, settings, request.At ?? clock.UtcNow));
    }

    private static FlowTestResponse Run(FlowGraph graph, Guid flowId, int? version, IReadOnlyList<SandboxMessage> answers, SandboxContact? contact, SettingsView settings, DateTimeOffset now)
    {
        var tags = new List<string>(contact?.Tags ?? []);
        var fields = new Dictionary<string, string>(contact?.Fields ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var run = FlowRun.Start(Guid.Empty, flowId, version ?? 0, Guid.Empty, Guid.Empty, ChannelKinds.Instagram, "sandbox", null, now);
        FlowContext Context(DateTimeOffset at) => new(at, true, tags, fields, Values(contact, fields), settings.BusinessHours);

        var turns = new List<FlowTestTurn> { new(null, Advance(graph, run, FlowRunner.Start(graph, run, Context(now)), Context, tags, fields, contact, ref now)) };
        foreach (var answer in answers)
        {
            if (run.Status != FlowRunStatuses.WaitingInput)
            {
                break;
            }

            turns.Add(new FlowTestTurn(answer, Advance(graph, run, FlowRunner.Answer(graph, run, answer.Text, answer.Payload, Context(now)), Context, tags, fields, contact, ref now)));
        }

        return new FlowTestResponse(flowId, version, run.Status, run.CurrentNode, run.Variables, turns);
    }

    /// <summary>Turns effects into steps, ending each delay at once, and applies tag and field actions for the conditions that follow.</summary>
    private static List<SandboxStep> Advance(
        FlowGraph graph, FlowRun run, IReadOnlyList<FlowEffect> effects, Func<DateTimeOffset, FlowContext> context, List<string> tags, Dictionary<string, string> fields,
        SandboxContact? contact, ref DateTimeOffset now)
    {
        var steps = new List<SandboxStep>();
        var pending = effects;
        for (var timers = 0; ; timers++)
        {
            FlowEffect.Timer? timer = null;
            foreach (var effect in pending)
            {
                switch (effect)
                {
                    case FlowEffect.Send send:
                        steps.Add(new SandboxStep(ActionKinds.SendMessage, send.Content));
                        break;
                    case FlowEffect.SendCards cards:
                        steps.Add(new SandboxStep(ActionKinds.SendCards, CardSetId: cards.CardSetId));
                        break;
                    case FlowEffect.Act act:
                        var acted = Steps(act.Actions, Values(contact, fields));
                        steps.AddRange(acted);
                        Apply(acted, tags, fields);
                        break;
                    case FlowEffect.Timer t:
                        steps.Add(new SandboxStep("delay", Until: t.DueAt));
                        timer = t;
                        break;
                    case FlowEffect.HandToAi ai:
                        steps.Add(new SandboxStep("ai", Goal: ai.Goal));
                        break;
                }
            }

            if (timer is null || timers >= MaxTimers)
            {
                return steps;
            }

            now = timer.DueAt;
            pending = FlowRunner.TimerFired(graph, run, timer.NodeId, context(now));
        }
    }

    private static void Apply(IEnumerable<SandboxStep> steps, List<string> tags, Dictionary<string, string> fields)
    {
        foreach (var step in steps)
        {
            switch (step.Type)
            {
                case ActionKinds.AddTag when step.Tag is { } tag && !tags.Contains(tag):
                    tags.Add(tag);
                    break;
                case ActionKinds.RemoveTag when step.Tag is { } tag:
                    tags.Remove(tag);
                    break;
                case ActionKinds.SetField when step.Field is { } field:
                    if (step.Value is null)
                    {
                        fields.Remove(field);
                    }
                    else
                    {
                        fields[field] = step.Value;
                    }

                    break;
            }
        }
    }

    /// <summary>The steps of a rule's actions, rendered like <see cref="ActionPlanner"/> renders them.</summary>
    private static List<SandboxStep> Steps(IEnumerable<RuleAction> actions, IReadOnlyDictionary<string, string?> values) =>
    [
        .. actions.Select(a => a.Kind switch
        {
            ActionKinds.SendMessage or ActionKinds.PrivateReply => new SandboxStep(a.Kind, a.Message is null ? null : TemplateRenderer.Render(a.Message, values)),
            ActionKinds.PublicReply => new SandboxStep(a.Kind, Replies: [.. (a.Replies ?? []).Select(r => TemplateRenderer.Render(r, values))]),
            ActionKinds.SendCards => new SandboxStep(a.Kind, CardSetId: a.CardSetId),
            ActionKinds.AddTag or ActionKinds.RemoveTag => new SandboxStep(a.Kind, Tag: a.Tag),
            ActionKinds.SetField => new SandboxStep(a.Kind, Field: a.Field, Value: a.Value is null ? null : TemplateRenderer.Render(a.Value, values)),
            ActionKinds.Handoff => new SandboxStep(a.Kind, Reason: a.Reason ?? "automation"),
            ActionKinds.NotifyTeam => new SandboxStep(a.Kind, Title: a.Title ?? "پیام نیازمند پیگیری", Value: a.Value is null ? null : TemplateRenderer.Render(a.Value, values)),
            ActionKinds.StartFlow => new SandboxStep(a.Kind, FlowId: a.FlowId),
            _ => new SandboxStep(a.Kind),
        }),
    ];

    private static Dictionary<string, string?> Values(SandboxContact? contact, IReadOnlyDictionary<string, string>? fields = null) =>
        EngineValues.For(new ConversationSnapshot(
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, "sandbox", "open", false, null, null, contact?.Name, contact?.Username,
            contact?.Tags ?? [], fields ?? contact?.Fields ?? new Dictionary<string, string>()));

    private static (string, string)? ValidateContact(SandboxContact? contact) =>
        contact is null ? null
        : contact.Name is { Length: > 200 } || contact.Username is { Length: > 100 } || contact.Tags is { Count: > 50 } || contact.Fields is { Count: > 50 }
            ? ("contact", "automation.test_contact_invalid")
            : null;

    private static IResult Invalid(IReadOnlyList<(string Field, string Code)> errors) =>
        Error.Validation(errors[0].Code, errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).Distinct().ToArray(), StringComparer.Ordinal)).ToProblem();
}
