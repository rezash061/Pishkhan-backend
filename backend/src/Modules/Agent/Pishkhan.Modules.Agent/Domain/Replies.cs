using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Agent.Domain;

public static class AgentTones
{
    public const string Friendly = "friendly";
    public const string Formal = "formal";
    public const string Playful = "playful";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Friendly, Formal, Playful };
}

/// <summary>When the agent hands a conversation to a person before generating (backend plan 10.2 step 4). Rules in code, not the model.</summary>
public sealed class HandoffRules
{
    /// <summary>The customer asks for a person (intent or built-in phrases such as «اپراتور»).</summary>
    public bool HumanRequest { get; set; } = true;

    /// <summary>A complaint with negative sentiment.</summary>
    public bool Complaint { get; set; } = true;

    /// <summary>A discount request: the agent does not negotiate (Phase 3 adds guardrails).</summary>
    public bool DiscountRequest { get; set; } = true;

    /// <summary>Two weak answers in a row (confidence under the threshold plus 0.1).</summary>
    public bool RepeatedLowConfidence { get; set; } = true;

    /// <summary>Words or phrases that always go to a person (matched on normalised text).</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>Contacts with any of these tags always go to a person.</summary>
    public List<string> VipTags { get; set; } = ["vip"];
}

/// <summary>The workspace's AI agent: persona, guardrails and handoff behaviour. A workspace without a row uses <see cref="Default"/>.</summary>
public sealed class AgentProfile : ITenantOwned
{
    public const int MaxName = 60;
    public const int MaxBrandVoice = 1_000;
    public const int MaxPhrase = 100;
    public const int MaxList = 50;
    public const int MaxHandoffMessage = 500;
    public const double DefaultThreshold = 0.6;
    public const string DefaultHandoffMessage = "سؤالتون رو به همکارم سپردم، به‌زودی جوابتون رو می‌ده 🙏";

    public Guid WorkspaceId { get; init; }

    /// <summary>The name the agent signs with, for example «دستیار فروشگاه».</summary>
    public string Name { get; set; } = "دستیار فروشگاه";

    /// <summary><see cref="AgentTones"/>.</summary>
    public string Tone { get; set; } = AgentTones.Friendly;

    /// <summary>How the business talks: free text from the owner, given to the model as style guidance.</summary>
    public string? BrandVoice { get; set; }

    /// <summary>Answer in the customer's language (Finglish or English) instead of always in Persian.</summary>
    public bool ReplyInCustomerLanguage { get; set; } = true;

    /// <summary>Phrases the agent must never write (checked on every reply).</summary>
    public List<string> NeverSay { get; set; } = [];

    /// <summary>Below this combined confidence the agent hands off with its reply as a suggestion.</summary>
    public double ConfidenceThreshold { get; set; } = DefaultThreshold;

    public HandoffRules Handoff { get; set; } = new();

    /// <summary>Sent to the customer when the agent hands off; null sends nothing.</summary>
    public string? HandoffMessage { get; set; } = DefaultHandoffMessage;

    /// <summary>Hosts a reply may link to besides the products' own links (for example the shop's site).</summary>
    public List<string> AllowedDomains { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static AgentProfile Default(Guid workspaceId) => new() { WorkspaceId = workspaceId };
}

/// <summary>A knowledge chunk the agent retrieved for a reply.</summary>
public sealed record TraceFact(Guid ChunkId, Guid SourceId, string SourceKind, string Title, double Score, double? Similarity);

/// <summary>A tool the model called: arguments and result as JSON (result cut at 4 KB), or the error.</summary>
public sealed record TraceToolCall(string Name, string Arguments, string? Result, string? Error);

/// <summary>Something the output guard rejected, per attempt (1 is the first answer, 2 the regenerated one).</summary>
public sealed record TraceGuardIssue(int Attempt, string Kind, string Detail);

/// <summary>One run of the reply pipeline for one customer message (backend plan 10.2), for the traces viewer and evals.</summary>
public sealed class AiTrace : ITenantOwned
{
    public const int MaxText = 4_000;

    public Guid Id { get; init; }

    public Guid WorkspaceId { get; init; }

    public Guid ConversationId { get; init; }

    /// <summary>The customer message answered (the last of the turn).</summary>
    public Guid MessageId { get; init; }

    public Guid? FlowRunId { get; init; }

    /// <summary>The flow's instruction when a flow handed the conversation to the agent.</summary>
    public string? Goal { get; init; }

    /// <summary>The customer's turn as the model saw it (normalised).</summary>
    public string Input { get; set; } = string.Empty;

    public string? Intent { get; set; }

    public string? Language { get; set; }

    public string? Sentiment { get; set; }

    public double? ClassifierConfidence { get; set; }

    public List<TraceFact> Facts { get; set; } = [];

    public List<TraceToolCall> ToolCalls { get; set; } = [];

    public List<TraceGuardIssue> GuardIssues { get; set; } = [];

    /// <summary>What was sent, or for a handoff the suggestion for the person.</summary>
    public string? Reply { get; set; }

    /// <summary>The model's own confidence (0..1).</summary>
    public double? SelfConfidence { get; set; }

    /// <summary>Combined confidence compared with the threshold.</summary>
    public double? Confidence { get; set; }

    public double Threshold { get; set; }

    /// <summary><c>AiReplyOutcomes</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Why it went to a person: <c>human_request</c>, <c>complaint</c>, <c>low_confidence</c>, <c>guard_price</c>, ...</summary>
    public string? HandoffReason { get; set; }

    /// <summary>Prompt keys and versions used, for example <c>intent.classify@2 agent.reply@1</c>.</summary>
    public string? Prompts { get; set; }

    /// <summary>Routes that answered (provider/model), space separated.</summary>
    public string? Models { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal CostIrr { get; set; }

    public int LatencyMs { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
