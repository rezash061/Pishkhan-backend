using Pishkhan.Contracts;

namespace Pishkhan.Modules.Agent.Contracts;

/// <summary>
/// The AI agent could not answer a customer's question from the workspace's knowledge (backend plan 9.7). The Knowledge module keeps it
/// as an unanswered question the owner can answer once; repeats of the same normalised question are counted. Keyed by workspace id.
/// </summary>
[IntegrationEvent(Topics.UnansweredQuestionRaised)]
public sealed record UnansweredQuestionRaised(Guid WorkspaceId, Guid ConversationId, Guid MessageId, string Question, DateTimeOffset AskedAt) : IIntegrationEvent;

public static class AiReplyOutcomes
{
    /// <summary>The reply was queued to the customer.</summary>
    public const string Sent = "sent";

    /// <summary>The conversation went to a person (a handoff rule, low confidence, the guard, or missing knowledge).</summary>
    public const string HandedOff = "handed_off";

    /// <summary>Nothing was sent: the AI was unavailable or the plan's AI limit was reached and the conversation was handed off.</summary>
    public const string Failed = "failed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Sent, HandedOff, Failed };
}

/// <summary>The AI agent's persona an industry template suggests (BE-2.9). <see cref="Tone"/> is friendly, formal or playful.</summary>
public sealed record TemplateAgentProfile(string Name, string Tone, string? BrandVoice, IReadOnlyList<string> NeverSay, IReadOnlyList<string> HandoffKeywords);

/// <summary>Sets up the AI agent from an industry template (BE-2.9).</summary>
public interface IAgentTemplates
{
    /// <summary>Saves the profile only when the workspace has not set one up yet; false when it already had one (nothing changes).</summary>
    Task<bool> ApplyProfileAsync(Guid workspaceId, TemplateAgentProfile profile, CancellationToken cancellationToken);
}
