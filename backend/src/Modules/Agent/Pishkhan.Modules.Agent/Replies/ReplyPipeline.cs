using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pishkhan.AI;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Agent.Replies;

public static class Intents
{
    public const string PriceQuestion = "price_question";
    public const string Availability = "availability";
    public const string Order = "order";
    public const string Shipping = "shipping";
    public const string Complaint = "complaint";
    public const string DiscountRequest = "discount_request";
    public const string HumanRequest = "human_request";
    public const string Greeting = "greeting";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PriceQuestion, Availability, Order, Shipping, Complaint, DiscountRequest, HumanRequest, Greeting, Other,
    };
}

/// <summary>The classifier's answer (backend plan 10.2 step 3). Unknown or missing values fall back to <c>other</c> and 0.5.</summary>
public sealed record Classification(string Intent, string? Language, string? Sentiment, double Confidence)
{
    public static readonly Classification Unknown = new(Intents.Other, null, null, 0.5);

    public static Classification Parse(string? output)
    {
        if (JsonOutput.Object(output) is not { } root)
        {
            return Unknown;
        }

        var intent = root.TryGetProperty("intent", out var i) && i.ValueKind == JsonValueKind.String && Intents.All.Contains(i.GetString()!) ? i.GetString()! : Intents.Other;
        return new Classification(
            intent,
            JsonOutput.String(root, "language"),
            JsonOutput.String(root, "sentiment"),
            JsonOutput.Number(root, "confidence") is { } c ? Math.Clamp(c, 0, 1) : 0.5);
    }
}

/// <summary>The reply model's answer: <c>{"reply": "...", "confidence": 0..1, "answered": true}</c>.</summary>
public sealed record GeneratedReply(string? Reply, double Confidence, bool Answered)
{
    public static GeneratedReply Parse(string? output)
    {
        if (JsonOutput.Object(output) is not { } root)
        {
            // A model that ignored the format: its text is the reply, with middling confidence.
            return string.IsNullOrWhiteSpace(output) ? new GeneratedReply(null, 0, false) : new GeneratedReply(output.Trim(), 0.5, true);
        }

        var reply = JsonOutput.String(root, "reply")?.Trim();
        var answered = !root.TryGetProperty("answered", out var a) || a.ValueKind != JsonValueKind.False;
        var confidence = JsonOutput.Number(root, "confidence") is { } c ? Math.Clamp(c, 0, 1) : 0.5;
        return new GeneratedReply(string.IsNullOrWhiteSpace(reply) ? null : reply, confidence, answered && !string.IsNullOrWhiteSpace(reply));
    }
}

internal static class JsonOutput
{
    /// <summary>The first JSON object in a model's output (models sometimes wrap it in prose or code fences).</summary>
    public static JsonElement? Object(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var start = output.IndexOf('{', StringComparison.Ordinal);
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output.AsMemory(start, end - start + 1));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        } : null;
}

/// <summary>Handoff rules in code (backend plan 10.2 step 4), before and after classification.</summary>
public static class HandoffPolicy
{
    /// <summary>Built-in phrases that ask for a person, on normalised text. «ادمین» is left out: customers greet the page with it.</summary>
    public static readonly IReadOnlyList<string> HumanPhrases =
    [
        "اپراتور", "آدم واقعی", "انسان واقعی", "با یه آدم", "با یک آدم", "با یه انسان", "با یک انسان", "با مدیر", "با صاحب", "با خود فروشنده",
        "operator", "real person", "human agent", "talk to a human",
    ];

    /// <summary>The reason to hand off without generating, or null.</summary>
    public static string? Before(string turn, IReadOnlyList<string> tags, HandoffRules rules)
    {
        if (rules.VipTags.Count > 0 && tags.Any(t => rules.VipTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            return "vip";
        }

        var text = PersianNormalizer.Normalize(turn).ToLowerInvariant();
        if (rules.Keywords.Any(k => k.Length > 0 && text.Contains(PersianNormalizer.Normalize(k).ToLowerInvariant(), StringComparison.Ordinal)))
        {
            return "keyword";
        }

        return rules.HumanRequest && HumanPhrases.Any(p => text.Contains(PersianNormalizer.Normalize(p), StringComparison.Ordinal)) ? "human_request" : null;
    }

    public static string? After(Classification c, HandoffRules rules) => c.Intent switch
    {
        Intents.HumanRequest when rules.HumanRequest => "human_request",
        Intents.Complaint when rules.Complaint && c.Sentiment is "negative" => "complaint",
        Intents.DiscountRequest when rules.DiscountRequest => "discount_request",
        _ => null,
    };
}

/// <summary>What the pipeline decided for a customer turn. <see cref="Trace"/> is filled but not saved.</summary>
internal sealed record ReplyDecision(string Outcome, string? Reply, string? HandoffReason, string? SuggestedReply, string? UnansweredQuestion, AiTrace Trace);

/// <summary>A customer turn: the inbound messages since our last reply, and the conversation before them.</summary>
internal sealed record ReplyInput(
    ConversationSnapshot Conversation, Guid MessageId, string Turn, IReadOnlyList<MessageSnapshot> Before, string? Goal, Guid? FlowRunId, AiTrace? Previous);

/// <summary>
/// The reply pipeline (backend plan 10.2): handoff rules, classification, hybrid retrieval, generation with tools, the output guard with
/// one regeneration, and the confidence check. It decides; the caller sends, records usage and saves the trace.
/// </summary>
internal sealed class ReplyPipeline(
    IModelRouter router, IPromptRegistry prompts, IKnowledgeSearch knowledge, ICatalogQueries catalog, IOrderCommands orders, IClock clock,
    ILogger<ReplyPipeline> logger)
{
    public const string ClassifyPrompt = "intent.classify";
    public const string ReplyPrompt = "agent.reply";
    public const int FactCount = 5;
    public const int HistoryMessages = 10;
    private const int MaxModelCalls = 4;

    private static readonly Dictionary<string, string> ToneText = new(StringComparer.Ordinal)
    {
        [AgentTones.Friendly] = "friendly and warm, informal Persian (محاوره‌ای), short sentences, at most one emoji",
        [AgentTones.Formal] = "polite and formal Persian (رسمی), no emoji",
        [AgentTones.Playful] = "playful and energetic informal Persian, emoji are welcome",
    };

    public async Task<ReplyDecision> RunAsync(ReplyInput input, AgentProfile profile, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var conversation = input.Conversation;
        var turn = PersianNormalizer.Normalize(input.Turn);
        var trace = new AiTrace
        {
            Id = Uuid.NewV7(clock.UtcNow),
            WorkspaceId = conversation.WorkspaceId,
            ConversationId = conversation.Id,
            MessageId = input.MessageId,
            FlowRunId = input.FlowRunId,
            Goal = input.Goal is { Length: > 500 } g ? g[..500] : input.Goal,
            Input = turn.Length <= AiTrace.MaxText ? turn : turn[..AiTrace.MaxText],
            Threshold = profile.ConfidenceThreshold,
            CreatedAt = clock.UtcNow,
        };
        var usage = new Usage(trace);

        ReplyDecision Finish(string outcome, string? reply, string? reason, string? suggested, string? unanswered)
        {
            trace.Outcome = outcome;
            trace.HandoffReason = reason;
            var shown = reply ?? suggested;
            trace.Reply = shown is { Length: > AiTrace.MaxText } ? shown[..AiTrace.MaxText] : shown;
            trace.Models = usage.Models;
            trace.Prompts = usage.Prompts;
            trace.LatencyMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return new ReplyDecision(outcome, reply, reason, suggested, unanswered, trace);
        }

        if (HandoffPolicy.Before(turn, conversation.Tags, profile.Handoff) is { } early)
        {
            return Finish(AiReplyOutcomes.HandedOff, null, early, null, null);
        }

        // Classify. A classifier outage is not fatal: generation decides.
        var classification = Classification.Unknown;
        try
        {
            var prompt = prompts.Get(ClassifyPrompt);
            usage.Prompt(prompt);
            var response = await router.GetChatClient(AiTasks.Classify, conversation.WorkspaceId).GetResponseAsync(
                [new ChatMessage(ChatRole.System, prompt.Text), new ChatMessage(ChatRole.User, turn)], new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, ct);
            usage.Add(response);
            classification = Classification.Parse(response.Text);
        }
        catch (Exception ex) when (ex is AllRoutesFailedException or NoRouteException)
        {
            logger.LogWarning("Classifier unavailable for conversation {ConversationId}: {Error}", conversation.Id, ex.Message);
        }

        trace.Intent = classification.Intent;
        trace.Language = classification.Language;
        trace.Sentiment = classification.Sentiment;
        trace.ClassifierConfidence = classification.Confidence;
        if (HandoffPolicy.After(classification, profile.Handoff) is { } rule)
        {
            return Finish(AiReplyOutcomes.HandedOff, null, rule, null, null);
        }

        // Retrieve.
        var hits = await knowledge.SearchAsync(turn, FactCount, ct);
        trace.Facts = [.. hits.Select(h => new TraceFact(h.ChunkId, h.SourceId, h.SourceKind, h.Title, Math.Round(h.Score, 5), h.Similarity is { } s ? Math.Round(s, 4) : null))];

        // Generate, guard, and regenerate once.
        // A real message has an id; the test chat and the demo pass Guid.Empty and never save an order.
        var tools = new AgentTools(catalog, orders, conversation, live: input.MessageId != Guid.Empty);
        var template = prompts.Get(ReplyPrompt);
        usage.Prompt(template);
        var messages = new List<ChatMessage> { new(ChatRole.System, template.Render(Values(profile, conversation, hits, input.Goal, clock.UtcNow))) };
        messages.AddRange(History(input.Before));
        messages.Add(new ChatMessage(ChatRole.User, turn));

        GeneratedReply generated;
        IReadOnlyList<GuardIssue> issues = [];
        try
        {
            var client = router.GetChatClient(AiTasks.GenerateReply, conversation.WorkspaceId);
            generated = await GenerateAsync(client, messages, tools, usage, ct);
            for (var attempt = 1; ; attempt++)
            {
                if (generated.Reply is null)
                {
                    break;
                }

                issues = ReplyGuard.Check(generated.Reply, Guard(profile, turn, hits, tools));
                trace.GuardIssues.AddRange(issues.Select(i => new TraceGuardIssue(attempt, i.Kind, i.Detail.Length <= 200 ? i.Detail : i.Detail[..200])));
                if (issues.Count == 0 || attempt == 2)
                {
                    break;
                }

                messages.Add(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new { reply = generated.Reply })));
                messages.Add(new ChatMessage(ChatRole.User, "[system] Your answer was not sent because:\n" + ReplyGuard.Explain(issues) + "Answer the customer again, in the same JSON format."));
                generated = await GenerateAsync(client, messages, tools, usage, ct);
            }
        }
        catch (Exception ex) when (ex is AllRoutesFailedException or NoRouteException)
        {
            logger.LogWarning("Reply model unavailable for conversation {ConversationId}: {Error}", conversation.Id, ex.Message);
            trace.ToolCalls = tools.Calls;
            return Finish(AiReplyOutcomes.Failed, null, "ai_unavailable", null, null);
        }

        trace.ToolCalls = tools.Calls;
        trace.SelfConfidence = generated.Confidence;
        var unanswered = tools.UnansweredQuestion;

        if (tools.HandoffReason is not null)
        {
            return Finish(AiReplyOutcomes.HandedOff, null, "ai_request", generated.Reply, unanswered);
        }

        if (!generated.Answered || generated.Reply is null)
        {
            return Finish(AiReplyOutcomes.HandedOff, null, "missing_knowledge", null, unanswered ?? input.Turn.Trim());
        }

        if (issues.Count > 0)
        {
            return Finish(AiReplyOutcomes.HandedOff, null, "guard_" + issues[0].Kind, null, unanswered);
        }

        var confidence = Confidence(classification, generated, hits, tools.FoundData);
        trace.Confidence = confidence;
        if (confidence < profile.ConfidenceThreshold)
        {
            return Finish(AiReplyOutcomes.HandedOff, null, "low_confidence", generated.Reply, unanswered);
        }

        if (profile.Handoff.RepeatedLowConfidence && IsWeak(confidence, profile.ConfidenceThreshold)
            && input.Previous is { Outcome: AiReplyOutcomes.Sent, Confidence: { } previous } && IsWeak(previous, input.Previous.Threshold))
        {
            return Finish(AiReplyOutcomes.HandedOff, null, "repeated_low_confidence", generated.Reply, unanswered);
        }

        return Finish(AiReplyOutcomes.Sent, generated.Reply, null, null, unanswered);
    }

    /// <summary>
    /// Combined confidence (backend plan 10.2 step 8): half the model's own estimate, a quarter the classifier's, a quarter grounding.
    /// Grounding is 1 when a catalog tool returned data or the message is a greeting, else the best retrieved similarity (0.5 for a
    /// full-text-only hit, 0.3 with nothing retrieved).
    /// </summary>
    public static double Confidence(Classification classification, GeneratedReply reply, IReadOnlyList<KnowledgeHit> hits, bool toolData)
    {
        var grounding = toolData || classification.Intent == Intents.Greeting ? 1.0
            : hits.Count == 0 ? 0.3
            : hits.Max(h => h.Similarity ?? 0.5);
        return Math.Round((0.5 * reply.Confidence) + (0.25 * classification.Confidence) + (0.25 * Math.Clamp(grounding, 0, 1)), 4);
    }

    private static bool IsWeak(double confidence, double threshold) => confidence < threshold + 0.1;

    private async Task<GeneratedReply> GenerateAsync(IChatClient client, List<ChatMessage> messages, AgentTools tools, Usage usage, CancellationToken ct)
    {
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Tools = tools.Functions, ToolMode = ChatToolMode.Auto };
        for (var call = 1; ; call++)
        {
            if (call == MaxModelCalls)
            {
                // Last round: no more tools, an answer is required.
                options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
            }

            var response = await client.GetResponseAsync(messages, options, ct);
            usage.Add(response);
            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0 || call == MaxModelCalls)
            {
                return GeneratedReply.Parse(response.Text);
            }

            messages.AddRange(response.Messages);
            var results = new List<AIContent>();
            foreach (var c in calls)
            {
                results.Add(new FunctionResultContent(c.CallId, await tools.InvokeAsync(c, ct)));
            }

            messages.Add(new ChatMessage(ChatRole.Tool, results));
        }
    }

    private static GuardContext Guard(AgentProfile profile, string turn, IReadOnlyList<KnowledgeHit> hits, AgentTools tools)
    {
        var guard = new GuardContext { NeverSay = profile.NeverSay, CustomerText = turn, ReplyInCustomerLanguage = profile.ReplyInCustomerLanguage };
        foreach (var hit in hits)
        {
            guard.Allow(hit.Text);
        }

        foreach (var call in tools.Calls)
        {
            guard.Allow(call.Result);
        }

        foreach (var domain in profile.AllowedDomains)
        {
            guard.AllowedHosts.Add(domain);
        }

        return guard;
    }

    private static Dictionary<string, string> Values(AgentProfile profile, ConversationSnapshot conversation, IReadOnlyList<KnowledgeHit> hits, string? goal, DateTimeOffset now)
    {
        var facts = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            facts.Append('[').Append(i + 1).Append("] ").Append(hits[i].Title).Append(": ").AppendLine(hits[i].Text.ReplaceLineEndings(" "));
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = profile.Name,
            ["tone"] = ToneText.GetValueOrDefault(profile.Tone, ToneText[AgentTones.Friendly]),
            ["brand_voice"] = string.IsNullOrWhiteSpace(profile.BrandVoice) ? "-" : profile.BrandVoice,
            ["language"] = profile.ReplyInCustomerLanguage ? "Reply in the customer's language; Persian when they write Persian or Finglish." : "Always reply in Persian.",
            ["never_say"] = profile.NeverSay.Count == 0 ? "-" : string.Join(" | ", profile.NeverSay),
            ["goal"] = string.IsNullOrWhiteSpace(goal) ? "-" : goal,
            ["customer"] = conversation.ContactName ?? conversation.ContactUsername ?? "-",
            ["today"] = Jalali.FormatLong(now),
            ["facts"] = facts.Length == 0 ? "(no facts found)" : facts.ToString(),
        };
    }

    private static IEnumerable<ChatMessage> History(IReadOnlyList<MessageSnapshot> before) =>
        before.TakeLast(HistoryMessages).Select(m => new ChatMessage(
            m.Direction == MessageDirections.Inbound ? ChatRole.User : ChatRole.Assistant,
            string.IsNullOrWhiteSpace(m.Text) ? $"[{m.Kind}]" : m.Text));

    /// <summary>Tokens, cost and routes over every model call of a run.</summary>
    private sealed class Usage(AiTrace trace)
    {
        private readonly List<string> _models = [];
        private readonly List<string> _prompts = [];

        public string? Models => _models.Count == 0 ? null : Cut(string.Join(' ', _models.Distinct(StringComparer.Ordinal)), 400);

        public string? Prompts => _prompts.Count == 0 ? null : Cut(string.Join(' ', _prompts), 200);

        public void Prompt(PromptTemplate prompt) => _prompts.Add($"{prompt.Key}@{prompt.Version}");

        public void Add(ChatResponse response)
        {
            trace.InputTokens += response.Usage?.InputTokenCount ?? 0;
            trace.OutputTokens += response.Usage?.OutputTokenCount ?? 0;
            if (response.AdditionalProperties is { } props)
            {
                if (props.TryGetValue("pishkhan.route", out var route) && route is not null)
                {
                    _models.Add(route.ToString()!);
                }

                if (props.TryGetValue("pishkhan.cost_irr", out var cost) && cost is decimal irr)
                {
                    trace.CostIrr += irr;
                }
            }
        }

        private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
    }
}
