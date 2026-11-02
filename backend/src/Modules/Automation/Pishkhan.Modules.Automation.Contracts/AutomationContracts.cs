using Pishkhan.Contracts;

namespace Pishkhan.Modules.Automation.Contracts;

/// <summary>What starts a rule (backend plan 9.5).</summary>
public static class TriggerKinds
{
    public const string DmKeyword = "dm_keyword";
    public const string CommentKeyword = "comment_keyword";

    /// <summary>Every comment on the post (or on every post), whatever its text.</summary>
    public const string CommentAny = "comment_any";
    public const string StoryReply = "story_reply";
    public const string StoryMention = "story_mention";
    public const string LiveComment = "live_comment";
    public const string PostShare = "post_share";
    public const string NewConversation = "new_conversation";

    /// <summary>A button or quick reply with a matching payload.</summary>
    public const string Postback = "postback";
    public const string OutsideBusinessHours = "outside_business_hours";

    /// <summary>A flow's form was completed (BE-1.7, with form submissions).</summary>
    public const string FormCompleted = "form_completed";

    /// <summary>An order changed status (Phase 2, with Orders).</summary>
    public const string OrderStatusChanged = "order_status_changed";

    public static readonly IReadOnlySet<string> Available = new HashSet<string>(StringComparer.Ordinal)
    {
        DmKeyword, CommentKeyword, CommentAny, StoryReply, StoryMention, LiveComment, PostShare, NewConversation, Postback, OutsideBusinessHours,
    };

    public static readonly IReadOnlySet<string> Later = new HashSet<string>(StringComparer.Ordinal) { FormCompleted, OrderStatusChanged };

    public static readonly IReadOnlySet<string> Comment = new HashSet<string>(StringComparer.Ordinal) { CommentKeyword, CommentAny, LiveComment };
}

public static class MatchModes
{
    /// <summary>The whole message is the keyword.</summary>
    public const string Exact = "exact";

    /// <summary>The message contains the phrase (spacing and half-spaces ignored).</summary>
    public const string Contains = "contains";

    /// <summary>The message contains any of the keywords as a whole word or phrase.</summary>
    public const string AnyOf = "any_of";

    /// <summary>A safe pattern: <c>*</c> for any text, <c>|</c> between alternatives. No full regular expressions.</summary>
    public const string Pattern = "pattern";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Exact, Contains, AnyOf, Pattern };
}

public static class CommentScopes
{
    public const string AllPosts = "all";
    public const string Post = "post";

    /// <summary>The first post published after the rule was created; the rule binds to it on its first comment.</summary>
    public const string NextPost = "next_post";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { AllPosts, Post, NextPost };
}

public static class ActionKinds
{
    public const string SendMessage = "send_message";
    public const string PrivateReply = "private_reply";

    /// <summary>A public reply under the comment, picked at random from the rule's list so replies do not look like spam.</summary>
    public const string PublicReply = "public_reply";

    /// <summary>A carousel of a storefront card set (Catalog), built from current prices and stock when sent.</summary>
    public const string SendCards = "send_cards";
    public const string StartFlow = "start_flow";
    public const string AddTag = "add_tag";
    public const string RemoveTag = "remove_tag";
    public const string SetField = "set_field";
    public const string Handoff = "handoff";
    public const string PauseAi = "pause_ai";
    public const string NotifyTeam = "notify_team";

    /// <summary>Follow-up timer (BE-1.8).</summary>
    public const string ScheduleFollowUp = "schedule_followup";

    /// <summary>Phone capture with consent (Phase 2).</summary>
    public const string CapturePhone = "capture_phone";

    public static readonly IReadOnlySet<string> Available = new HashSet<string>(StringComparer.Ordinal)
    {
        SendMessage, SendCards, PrivateReply, PublicReply, StartFlow, AddTag, RemoveTag, SetField, Handoff, PauseAi, NotifyTeam,
    };

    public static readonly IReadOnlySet<string> Later = new HashSet<string>(StringComparer.Ordinal) { ScheduleFollowUp, CapturePhone };
}

/// <summary>Rules of a workspace changed: hosts drop their compiled rule sets (the cache tag is invalidated in the same transaction).</summary>
[IntegrationEvent(Topics.RulesChanged)]
public sealed record RulesChanged(Guid WorkspaceId, Guid RuleId, int Revision) : IIntegrationEvent;

[IntegrationEvent(Topics.FlowPublished)]
public sealed record FlowPublished(Guid WorkspaceId, Guid FlowId, int Version) : IIntegrationEvent;

/// <summary>
/// A flow run with questions completed: <see cref="Answers"/> holds each answered question's variable and the customer's normalised answer.
/// The CRM stores it as a form submission of the flow.
/// </summary>
[IntegrationEvent(Topics.FormSubmitted)]
public sealed record FormSubmitted(
    Guid WorkspaceId, Guid FlowId, int FlowVersion, string FlowName, Guid RunId, Guid ConversationId, IReadOnlyDictionary<string, string> Answers,
    DateTimeOffset SubmittedAt) : IIntegrationEvent;

/// <summary>Post a public reply under a comment (outbound group, paced per account). Keyed by media id.</summary>
[IntegrationEvent(Topics.CommentReplyRequested)]
public sealed record CommentReplyRequested(Guid ChannelAccountId, string CommentId, string MediaId, string Text, Guid RuleId) : IIntegrationEvent;

/// <summary>Process one page of a post's existing comments for a backlog run, then ask for the next page.</summary>
[IntegrationEvent(Topics.BacklogPageRequested)]
public sealed record BacklogPageRequested(Guid BacklogRunId, string? After) : IIntegrationEvent;

/// <summary>In-process API of the Automation module, scoped to the current workspace.</summary>
public interface IFlowQueries
{
    /// <summary>Whether the flow exists in the current workspace (deleted flows do not).</summary>
    Task<bool> FlowExistsAsync(Guid flowId, CancellationToken cancellationToken);
}

/// <summary>A flow an industry template suggests (BE-2.9): <see cref="Graph"/> in the same JSON shape as the flows API.</summary>
public sealed record TemplateFlow(string Key, string Name, System.Text.Json.JsonElement Graph);

/// <summary>
/// A rule an industry template suggests (BE-2.9): <see cref="Trigger"/> and <see cref="Actions"/> in the same JSON shape as the rules API,
/// except that a <c>start_flow</c> action names its flow by the template's <c>flowKey</c>.
/// </summary>
public sealed record TemplateRule(string Key, string Name, string TriggerKind, System.Text.Json.JsonElement Trigger, System.Text.Json.JsonElement Actions, int Priority);

/// <summary>What applying a template's automations did.</summary>
public sealed record TemplateAutomationResult(int FlowsCreated, int RulesCreated, int Skipped);

/// <summary>Adds an industry template's flows and rules to a workspace (BE-2.9).</summary>
public interface IAutomationTemplates
{
    /// <summary>
    /// Adds each flow and rule whose key the workspace has never had (deleted ones included, so applying again never brings back what the
    /// owner removed). Flows are published so rules can start them; rules are added disabled for the owner to review and switch on.
    /// Throws <see cref="InvalidOperationException"/> when the template itself is invalid.
    /// </summary>
    Task<TemplateAutomationResult> ApplyAsync(Guid workspaceId, IReadOnlyList<TemplateFlow> flows, IReadOnlyList<TemplateRule> rules, CancellationToken cancellationToken);
}
