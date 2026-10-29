using System.Globalization;
using System.Text.RegularExpressions;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Persian;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Automation.Domain;

public static class FlowNodeTypes
{
    public const string Message = "message";
    public const string Question = "question";
    public const string Condition = "condition";
    public const string Delay = "delay";
    public const string Storefront = "storefront";
    public const string Action = "action";
    public const string Ai = "ai";
    public const string End = "end";

    /// <summary>Follow-gate (BE-1.8).</summary>
    public const string FollowGate = "follow_gate";

    public static readonly IReadOnlySet<string> Available = new HashSet<string>(StringComparer.Ordinal)
    {
        Message, Question, Condition, Delay, Storefront, Action, Ai, End,
    };

    public static readonly IReadOnlySet<string> Later = new HashSet<string>(StringComparer.Ordinal) { FollowGate };

    /// <summary>Node types where a run stops and waits (for an answer or a timer).</summary>
    public static readonly IReadOnlySet<string> Waits = new HashSet<string>(StringComparer.Ordinal) { Question, Delay };
}

public static class FlowHandles
{
    public const string Next = "next";
    public const string Invalid = "invalid";
    public const string True = "true";
    public const string False = "false";

    public static IReadOnlySet<string> For(string nodeType) => nodeType switch
    {
        FlowNodeTypes.Question => new HashSet<string>(StringComparer.Ordinal) { Next, Invalid },
        FlowNodeTypes.Condition => new HashSet<string>(StringComparer.Ordinal) { True, False },
        FlowNodeTypes.Ai or FlowNodeTypes.End => new HashSet<string>(StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal) { Next },
    };
}

public static class AnswerTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Phone = "phone";
    public const string Choice = "choice";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Text, Number, Phone, Choice };
}

/// <summary>
/// A flow: nodes joined by edges, starting at <see cref="Start"/>. An edge leaves a node through a handle (<see cref="FlowHandles"/>);
/// a node without an outgoing edge for the handle taken ends the run.
/// </summary>
public sealed record FlowGraph(string Start, IReadOnlyList<FlowNode> Nodes, IReadOnlyList<FlowEdge> Edges)
{
    public FlowNode? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public string? Next(string from, string handle) => Edges.FirstOrDefault(e => e.From == from && (e.Handle ?? FlowHandles.Next) == handle)?.To;
}

/// <summary>One node. Which fields apply depends on <see cref="Type"/>: see <see cref="FlowValidator"/>.</summary>
public sealed record FlowNode
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    /// <summary>message, storefront (cards) and question (the prompt).</summary>
    public OutboundContent? Message { get; init; }

    /// <summary>storefront: a Catalog card set, sent with current prices and stock (instead of cards written into the node).</summary>
    public Guid? CardSetId { get; init; }

    public string? AnswerType { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>question: where the answer is stored, read back as <c>{{flow.&lt;variable&gt;}}</c>.</summary>
    public string? Variable { get; init; }

    public string? RetryText { get; init; }

    public int? MaxRetries { get; init; }

    public IReadOnlyList<FlowCondition>? Conditions { get; init; }

    /// <summary>condition: <c>all</c> (default) or <c>any</c>.</summary>
    public string? Match { get; init; }

    public int? DelaySeconds { get; init; }

    public IReadOnlyList<RuleAction>? Actions { get; init; }

    /// <summary>ai: what the agent should achieve ("help choose a size").</summary>
    public string? Goal { get; init; }
}

/// <summary>
/// A test in a condition node. <see cref="Subject"/>: <c>tag</c> (ops has, not_has), <c>field</c> or <c>variable</c> named by <see cref="Name"/>
/// (equals, not_equals, contains, exists, not_exists, gt, lt), or <c>time</c> (inside_hours, outside_hours of the workspace's business hours).
/// </summary>
public sealed record FlowCondition(string Subject, string Op, string? Value = null, string? Name = null);

public sealed record FlowEdge(string From, string To, string? Handle = null);

public sealed record FlowIssue(string Code, string? NodeId);

public sealed record FlowValidation(IReadOnlyList<FlowIssue> Errors, IReadOnlyList<FlowIssue> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Server-side validation of a flow graph (backend plan 9.5). A flow publishes only without errors; warnings are informational.</summary>
public static partial class FlowValidator
{
    public const int MaxNodes = 200;
    public const int MaxDelaySeconds = 30 * 24 * 3600;

    private static readonly IReadOnlySet<string> FlowActionKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        ActionKinds.AddTag, ActionKinds.RemoveTag, ActionKinds.SetField, ActionKinds.Handoff, ActionKinds.PauseAi, ActionKinds.NotifyTeam,
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    public static FlowValidation Validate(FlowGraph? graph)
    {
        var errors = new List<FlowIssue>();
        var warnings = new List<FlowIssue>();
        if (graph is null || graph.Nodes is null || graph.Edges is null || graph.Nodes.Count == 0)
        {
            return new FlowValidation([new FlowIssue("flow.empty", null)], []);
        }

        if (graph.Nodes.Count > MaxNodes)
        {
            errors.Add(new FlowIssue("flow.too_many_nodes", null));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 40)
            {
                errors.Add(new FlowIssue("flow.node_id_invalid", node.Id));
                continue;
            }

            if (!ids.Add(node.Id))
            {
                errors.Add(new FlowIssue("flow.node_duplicate", node.Id));
            }

            if (FlowNodeTypes.Later.Contains(node.Type))
            {
                errors.Add(new FlowIssue("flow.node_type_unavailable", node.Id));
            }
            else if (!FlowNodeTypes.Available.Contains(node.Type))
            {
                errors.Add(new FlowIssue("flow.node_type_unknown", node.Id));
            }
            else
            {
                errors.AddRange(ValidateNode(node).Select(code => new FlowIssue(code, node.Id)));
            }
        }

        if (!ids.Contains(graph.Start ?? string.Empty))
        {
            errors.Add(new FlowIssue("flow.start_missing", null));
        }

        var seen = new HashSet<(string, string)>();
        foreach (var edge in graph.Edges)
        {
            if (!ids.Contains(edge.From) || !ids.Contains(edge.To))
            {
                errors.Add(new FlowIssue("flow.edge_unknown_node", edge.From));
                continue;
            }

            var handle = edge.Handle ?? FlowHandles.Next;
            var type = graph.Node(edge.From)!.Type;
            if (!FlowHandles.For(type).Contains(handle))
            {
                errors.Add(new FlowIssue("flow.handle_invalid", edge.From));
            }

            if (!seen.Add((edge.From, handle)))
            {
                errors.Add(new FlowIssue("flow.handle_duplicate", edge.From));
            }
        }

        if (errors.Count == 0)
        {
            var reachable = Reachable(graph, graph.Start!, _ => true);
            warnings.AddRange(graph.Nodes.Where(n => !reachable.Contains(n.Id)).Select(n => new FlowIssue("flow.node_unreachable", n.Id)));
            if (HasLoopWithoutWait(graph))
            {
                errors.Add(new FlowIssue("flow.loop_without_wait", null));
            }
        }

        return new FlowValidation(errors, warnings);
    }

    private static IEnumerable<string> ValidateNode(FlowNode node)
    {
        switch (node.Type)
        {
            case FlowNodeTypes.Message:
                if (node.Message is null || node.Message.IsEmpty)
                {
                    yield return "flow.message_required";
                }
                else if (MessageValidator.Validate(node.Message) is { } code)
                {
                    yield return code;
                }

                break;
            case FlowNodeTypes.Storefront when node.CardSetId is not null:
                break;
            case FlowNodeTypes.Storefront:
                if (node.Message?.Cards is not { Count: >= 1 and <= 10 })
                {
                    yield return "flow.cards_required";
                }
                else if (MessageValidator.Validate(node.Message) is { } code)
                {
                    yield return code;
                }

                break;
            case FlowNodeTypes.Question:
                if (node.Message is null || string.IsNullOrWhiteSpace(node.Message.Text))
                {
                    yield return "flow.prompt_required";
                }

                if (node.AnswerType is null || !AnswerTypes.All.Contains(node.AnswerType))
                {
                    yield return "flow.answer_type_invalid";
                }

                if (node.Variable is null || !Identifier().IsMatch(node.Variable))
                {
                    yield return "flow.variable_invalid";
                }

                if (node.AnswerType == AnswerTypes.Choice && node.Choices is not { Count: >= 2 and <= 13 })
                {
                    yield return "flow.choices_invalid";
                }

                if (node.Choices is { } choices && choices.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 20))
                {
                    yield return "flow.choices_invalid";
                }

                if (node.MaxRetries is < 0 or > 5)
                {
                    yield return "flow.max_retries_invalid";
                }

                break;
            case FlowNodeTypes.Condition:
                if (node.Conditions is not { Count: >= 1 and <= 10 } || node.Conditions.Any(c => !IsValidCondition(c)))
                {
                    yield return "flow.conditions_invalid";
                }

                if (node.Match is not (null or "all" or "any"))
                {
                    yield return "flow.match_invalid";
                }

                break;
            case FlowNodeTypes.Delay:
                if (node.DelaySeconds is not { } seconds || seconds < 60 || seconds > MaxDelaySeconds)
                {
                    yield return "flow.delay_invalid";
                }

                break;
            case FlowNodeTypes.Action:
                if (node.Actions is not { Count: >= 1 and <= 10 } || node.Actions.Any(a => !FlowActionKinds.Contains(a.Kind)))
                {
                    yield return "flow.actions_invalid";
                }

                break;
            case FlowNodeTypes.Ai:
                if (node.Goal is { Length: > 500 })
                {
                    yield return "flow.goal_too_long";
                }

                break;
        }
    }

    private static bool IsValidCondition(FlowCondition c) => c.Subject switch
    {
        "tag" => c.Op is "has" or "not_has" && !string.IsNullOrWhiteSpace(c.Value),
        "field" or "variable" => !string.IsNullOrWhiteSpace(c.Name)
            && c.Op is "equals" or "not_equals" or "contains" or "exists" or "not_exists" or "gt" or "lt"
            && (c.Op is "exists" or "not_exists" || c.Value is not null),
        "time" => c.Op is "inside_hours" or "outside_hours",
        _ => false,
    };

    private static HashSet<string> Reachable(FlowGraph graph, string from, Func<FlowNode, bool> through)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>([from]);
        while (queue.TryDequeue(out var id))
        {
            if (graph.Node(id) is not { } node || !through(node))
            {
                continue;
            }

            foreach (var edge in graph.Edges.Where(e => e.From == id))
            {
                if (seen.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        return seen;
    }

    /// <summary>A cycle through nodes that never wait would spin forever without any input; the runtime also caps steps per advance.</summary>
    private static bool HasLoopWithoutWait(FlowGraph graph)
    {
        var instant = graph.Nodes.Where(n => !FlowNodeTypes.Waits.Contains(n.Type)).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            state[id] = 1;
            foreach (var edge in graph.Edges.Where(e => e.From == id && instant.Contains(e.To)))
            {
                var s = state.GetValueOrDefault(edge.To);
                if (s == 1 || (s == 0 && Visit(edge.To)))
                {
                    return true;
                }
            }

            state[id] = 2;
            return false;
        }

        return instant.Any(id => state.GetValueOrDefault(id) == 0 && Visit(id));
    }
}

/// <summary>Limits of Instagram message content (shared by rules and flows).</summary>
public static class MessageValidator
{
    public static string? Validate(OutboundContent content)
    {
        if (content.IsEmpty)
        {
            return "validation.message.empty";
        }

        if (content.Text is { Length: > 1000 })
        {
            return "validation.message.text_length";
        }

        if (content.QuickReplies is { Count: > 0 } quick && (quick.Count > 13 || quick.Any(q => string.IsNullOrWhiteSpace(q.Title) || q.Title.Length > 20 || string.IsNullOrWhiteSpace(q.Payload))))
        {
            return "validation.message.quick_replies";
        }

        if (content.Buttons is { Count: > 0 } buttons && (buttons.Count > 3 || content.Text is { Length: > 640 }
            || buttons.Any(b => string.IsNullOrWhiteSpace(b.Title) || b.Title.Length > 20 || b.Url is { Scheme: not "https" } || (b.Url is null && string.IsNullOrWhiteSpace(b.Payload)))))
        {
            return "validation.message.buttons";
        }

        if (content.Cards is { Count: > 0 } cards && (cards.Count > 10 || cards.Any(c => string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 80 || c.Subtitle is { Length: > 80 } || c.Buttons is { Count: > 3 })))
        {
            return "validation.message.cards";
        }

        return content.Attachment is { Url.Scheme: not "https" } ? "validation.message.attachment" : null;
    }
}

public static class FlowStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
}

/// <summary>A flow as edited in the panel. Publishing freezes the draft as a new <see cref="FlowVersion"/>; runs use the version they started on.</summary>
public sealed class Flow : AggregateRoot<Guid>, ITenantOwned, ISoftDeletable
{
    private Flow()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public FlowGraph Draft { get; private set; } = new(string.Empty, [], []);

    public string Status { get; private set; } = FlowStatuses.Draft;

    public int? PublishedVersion { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public static Flow Create(Guid workspaceId, string name, FlowGraph draft, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Name = name.Trim(),
        Draft = draft,
        CreatedAt = now,
    };

    public void Edit(string name, FlowGraph draft)
    {
        Name = name.Trim();
        Draft = draft;
    }

    public FlowVersion Publish(DateTimeOffset now)
    {
        var validation = FlowValidator.Validate(Draft);
        if (!validation.IsValid)
        {
            throw new DomainException(validation.Errors[0].Code);
        }

        PublishedVersion = (PublishedVersion ?? 0) + 1;
        PublishedAt = now;
        Status = FlowStatuses.Published;
        return new FlowVersion { FlowId = Id, Version = PublishedVersion.Value, WorkspaceId = WorkspaceId, Graph = Draft, PublishedAt = now };
    }
}

public sealed class FlowVersion : ITenantOwned
{
    public Guid FlowId { get; init; }

    public int Version { get; init; }

    public Guid WorkspaceId { get; init; }

    public required FlowGraph Graph { get; init; }

    public DateTimeOffset PublishedAt { get; init; }
}

public static class FlowRunStatuses
{
    public const string WaitingInput = "waiting_input";
    public const string WaitingTimer = "waiting_timer";
    public const string Completed = "completed";

    /// <summary>The customer did not give a valid answer within the retries, and the question has no <c>invalid</c> branch.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>A delay ended after the 24-hour window closed: nothing more may be sent.</summary>
    public const string Expired = "expired";
    public const string HandedToAi = "handed_to_ai";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> Active = [WaitingInput, WaitingTimer];
}

/// <summary>One customer going through one flow version. All waits are an answer or a timer (backend plan 9.5).</summary>
public sealed class FlowRun : Entity<Guid>, ITenantOwned
{
    private FlowRun()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid FlowId { get; private set; }

    public int FlowVersion { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid ChannelAccountId { get; private set; }

    public string ChannelKind { get; private set; } = string.Empty;

    public string ExternalUserId { get; private set; } = string.Empty;

    public Guid? RuleId { get; private set; }

    public string? CurrentNode { get; private set; }

    public string Status { get; private set; } = FlowRunStatuses.WaitingInput;

    public int Retries { get; private set; }

    public Dictionary<string, string> Variables { get; private set; } = new(StringComparer.Ordinal);

    public DateTimeOffset? WaitingUntil { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>The customer's latest message in this run, handed to the AI by an <c>ai</c> node.</summary>
    public Guid? LastMessageId { get; private set; }

    public DateTimeOffset? LastMessageCreatedAt { get; private set; }

    public bool IsActive => FlowRunStatuses.Active.Contains(Status);

    public static FlowRun Start(Guid workspaceId, Guid flowId, int version, Guid conversationId, Guid channelAccountId, string channelKind, string externalUserId, Guid? ruleId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        FlowId = flowId,
        FlowVersion = version,
        ConversationId = conversationId,
        ChannelAccountId = channelAccountId,
        ChannelKind = channelKind,
        ExternalUserId = externalUserId,
        RuleId = ruleId,
        CreatedAt = now,
    };

    internal void At(string node) => CurrentNode = node;

    internal void WaitForInput(string node)
    {
        CurrentNode = node;
        Status = FlowRunStatuses.WaitingInput;
        WaitingUntil = null;
    }

    internal void WaitForTimer(string node, DateTimeOffset until)
    {
        CurrentNode = node;
        Status = FlowRunStatuses.WaitingTimer;
        WaitingUntil = until;
    }

    internal void Set(string variable, string value) =>
        Variables = new Dictionary<string, string>(Variables, StringComparer.Ordinal) { [variable] = value };

    internal int Retry() => ++Retries;

    internal void ResetRetries() => Retries = 0;

    internal void End(string status, DateTimeOffset now)
    {
        Status = status;
        EndedAt = now;
        WaitingUntil = null;
    }

    public void Cancel(DateTimeOffset now) => End(FlowRunStatuses.Cancelled, now);

    public void Received(Guid messageId, DateTimeOffset createdAt)
    {
        LastMessageId = messageId;
        LastMessageCreatedAt = createdAt;
    }
}

/// <summary>What a flow step asks the engine to do. The runner itself has no I/O.</summary>
public abstract record FlowEffect
{
    public sealed record Send(OutboundContent Content) : FlowEffect;

    public sealed record SendCards(Guid CardSetId) : FlowEffect;

    public sealed record Act(IReadOnlyList<RuleAction> Actions) : FlowEffect;

    public sealed record Timer(string NodeId, DateTimeOffset DueAt) : FlowEffect;

    public sealed record HandToAi(string? Goal) : FlowEffect;
}

/// <summary>What the runner may read: time, the customer, the variables and the workspace's business hours.</summary>
public sealed record FlowContext(DateTimeOffset Now, bool WindowOpen, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, string?> Values, BusinessHours Hours);

/// <summary>
/// The flow state machine (backend plan 9.5): enters nodes until one waits (question, delay) or the run ends. Pure: it changes the
/// <see cref="FlowRun"/> and returns effects; the engine sends, schedules and persists.
/// </summary>
public static class FlowRunner
{
    /// <summary>Safety cap on nodes entered per advance, on top of the validator's loop check.</summary>
    public const int MaxSteps = 50;
    public const int DefaultMaxRetries = 2;

    public static IReadOnlyList<FlowEffect> Start(FlowGraph graph, FlowRun run, FlowContext context) =>
        Enter(graph, run, graph.Start, context);

    /// <summary>The customer answered while the run waits at a question. <paramref name="payload"/> is a quick reply payload, if any.</summary>
    public static IReadOnlyList<FlowEffect> Answer(FlowGraph graph, FlowRun run, string? text, string? payload, FlowContext context)
    {
        if (run.Status != FlowRunStatuses.WaitingInput || run.CurrentNode is null || graph.Node(run.CurrentNode) is not { Type: FlowNodeTypes.Question } question)
        {
            return [];
        }

        if (ReadAnswer(question, text, payload) is { } answer)
        {
            run.Set(question.Variable!, answer);
            run.ResetRetries();
            return Follow(graph, run, question.Id, FlowHandles.Next, context);
        }

        if (run.Retry() > (question.MaxRetries ?? DefaultMaxRetries))
        {
            run.ResetRetries();
            if (graph.Next(question.Id, FlowHandles.Invalid) is { } invalid)
            {
                return Enter(graph, run, invalid, context);
            }

            run.End(FlowRunStatuses.Abandoned, context.Now);
            return [];
        }

        var retry = string.IsNullOrWhiteSpace(question.RetryText) ? question.Message! : question.Message! with { Text = question.RetryText };
        return [new FlowEffect.Send(Prompt(question, retry, context with { Values = WithVariables(context, run) }))];
    }

    /// <summary>A delay ended. Past the 24-hour window nothing may be sent, so the run expires instead of continuing.</summary>
    public static IReadOnlyList<FlowEffect> TimerFired(FlowGraph graph, FlowRun run, string nodeId, FlowContext context)
    {
        if (run.Status != FlowRunStatuses.WaitingTimer || run.CurrentNode != nodeId)
        {
            return [];
        }

        if (!context.WindowOpen)
        {
            run.End(FlowRunStatuses.Expired, context.Now);
            return [];
        }

        return Follow(graph, run, nodeId, FlowHandles.Next, context);
    }

    private static IReadOnlyList<FlowEffect> Follow(FlowGraph graph, FlowRun run, string from, string handle, FlowContext context)
    {
        if (graph.Next(from, handle) is { } next)
        {
            return Enter(graph, run, next, context);
        }

        run.End(FlowRunStatuses.Completed, context.Now);
        return [];
    }

    private static IReadOnlyList<FlowEffect> Enter(FlowGraph graph, FlowRun run, string nodeId, FlowContext context)
    {
        var effects = new List<FlowEffect>();
        string? current = nodeId;
        for (var step = 0; current is not null; step++)
        {
            if (step >= MaxSteps || graph.Node(current) is not { } node)
            {
                run.End(FlowRunStatuses.Failed, context.Now);
                return effects;
            }

            run.At(node.Id);
            var values = WithVariables(context, run);
            switch (node.Type)
            {
                case FlowNodeTypes.Storefront when node.CardSetId is { } cardSetId:
                    effects.Add(new FlowEffect.SendCards(cardSetId));
                    current = graph.Next(node.Id, FlowHandles.Next);
                    break;
                case FlowNodeTypes.Message:
                case FlowNodeTypes.Storefront:
                    effects.Add(new FlowEffect.Send(TemplateRenderer.Render(node.Message!, values)));
                    current = graph.Next(node.Id, FlowHandles.Next);
                    break;
                case FlowNodeTypes.Action:
                    effects.Add(new FlowEffect.Act(node.Actions!));
                    current = graph.Next(node.Id, FlowHandles.Next);
                    break;
                case FlowNodeTypes.Condition:
                    current = graph.Next(node.Id, Evaluate(node, run, context) ? FlowHandles.True : FlowHandles.False);
                    break;
                case FlowNodeTypes.Question:
                    effects.Add(new FlowEffect.Send(Prompt(node, node.Message!, context with { Values = values })));
                    run.WaitForInput(node.Id);
                    return effects;
                case FlowNodeTypes.Delay:
                    var due = context.Now.AddSeconds(node.DelaySeconds!.Value);
                    effects.Add(new FlowEffect.Timer(node.Id, due));
                    run.WaitForTimer(node.Id, due);
                    return effects;
                case FlowNodeTypes.Ai:
                    effects.Add(new FlowEffect.HandToAi(node.Goal));
                    run.End(FlowRunStatuses.HandedToAi, context.Now);
                    return effects;
                default:
                    current = null;
                    break;
            }
        }

        run.End(FlowRunStatuses.Completed, context.Now);
        return effects;
    }

    private static OutboundContent Prompt(FlowNode question, OutboundContent prompt, FlowContext context)
    {
        var rendered = TemplateRenderer.Render(prompt, context.Values);
        if (question.AnswerType == AnswerTypes.Choice && question.Choices is { Count: > 0 } choices && rendered.Buttons is null or { Count: 0 })
        {
            rendered = rendered with { QuickReplies = [.. choices.Select((c, i) => new QuickReply(c, $"choice:{i.ToString(CultureInfo.InvariantCulture)}"))] };
        }

        return rendered;
    }

    /// <summary>The normalized answer, or null when it does not fit the question.</summary>
    public static string? ReadAnswer(FlowNode question, string? text, string? payload)
    {
        switch (question.AnswerType)
        {
            case AnswerTypes.Choice:
                var choices = question.Choices ?? [];
                if (payload is not null && payload.StartsWith("choice:", StringComparison.Ordinal)
                    && int.TryParse(payload.AsSpan(7), CultureInfo.InvariantCulture, out var index) && index >= 0 && index < choices.Count)
                {
                    return choices[index];
                }

                var key = KeywordMatcher.Compact(KeywordMatcher.Key(text));
                return choices.FirstOrDefault(c => KeywordMatcher.Compact(KeywordMatcher.Key(c)) == key && key.Length > 0);
            case AnswerTypes.Number:
                var digits = PersianNormalizer.Normalize(text).Replace(",", string.Empty, StringComparison.Ordinal).Replace("٬", string.Empty, StringComparison.Ordinal).Trim();
                return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
            case AnswerTypes.Phone:
                return IranianMobile.ToE164(PersianDigits.ToLatin(text ?? string.Empty).Trim());
            default:
                var trimmed = text?.Trim();
                return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= 500 ? trimmed : trimmed[..500];
        }
    }

    private static bool Evaluate(FlowNode node, FlowRun run, FlowContext context)
    {
        var results = node.Conditions!.Select(c => Test(c, run, context));
        return node.Match == "any" ? results.Any(r => r) : results.All(r => r);
    }

    private static bool Test(FlowCondition c, FlowRun run, FlowContext context)
    {
        switch (c.Subject)
        {
            case "tag":
                var has = context.Tags.Contains(c.Value!, StringComparer.Ordinal);
                return c.Op == "has" ? has : !has;
            case "time":
                var open = context.Hours.IsOpen(context.Now);
                return c.Op == "inside_hours" ? open : !open;
            default:
                var source = c.Subject == "field" ? context.Fields : run.Variables;
                var exists = source.TryGetValue(c.Name!, out var actual) && !string.IsNullOrEmpty(actual);
                return c.Op switch
                {
                    "exists" => exists,
                    "not_exists" => !exists,
                    "equals" => exists && Same(actual!, c.Value!),
                    "not_equals" => !exists || !Same(actual!, c.Value!),
                    "contains" => exists && KeywordMatcher.Compact(KeywordMatcher.Key(actual)).Contains(KeywordMatcher.Compact(KeywordMatcher.Key(c.Value)), StringComparison.Ordinal),
                    "gt" => exists && Number(actual) is { } a && Number(c.Value) is { } b && a > b,
                    "lt" => exists && Number(actual) is { } x && Number(c.Value) is { } y && x < y,
                    _ => false,
                };
        }
    }

    private static bool Same(string a, string b) => KeywordMatcher.Compact(KeywordMatcher.Key(a)) == KeywordMatcher.Compact(KeywordMatcher.Key(b));

    private static decimal? Number(string? s) =>
        decimal.TryParse(PersianNormalizer.Normalize(s).Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static Dictionary<string, string?> WithVariables(FlowContext context, FlowRun run)
    {
        var values = new Dictionary<string, string?>(context.Values, StringComparer.Ordinal);
        foreach (var (k, v) in run.Variables)
        {
            values["flow." + k] = v;
        }

        return values;
    }
}
