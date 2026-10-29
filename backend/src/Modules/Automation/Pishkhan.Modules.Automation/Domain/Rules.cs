using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Automation.Domain;

/// <summary>
/// What a rule listens for. Keyword triggers use <see cref="Match"/> with <see cref="Keywords"/> (or <see cref="Pattern"/>); comment
/// triggers add <see cref="Scope"/> and <see cref="PostId"/>; postback triggers use <see cref="Payload"/>.
/// <see cref="CooldownMinutes"/> stops a rule from firing again for the same conversation within that time.
/// </summary>
public sealed record TriggerConfig
{
    public string? Match { get; init; }

    public IReadOnlyList<string>? Keywords { get; init; }

    public string? Pattern { get; init; }

    public string? Scope { get; init; }

    public string? PostId { get; init; }

    public string? Payload { get; init; }

    public int? CooldownMinutes { get; init; }
}

/// <summary>One step a rule (or a flow's action node) performs. Which fields apply depends on <see cref="Kind"/> (<see cref="ActionKinds"/>).</summary>
public sealed record RuleAction
{
    public required string Kind { get; init; }

    /// <summary>send_message and private_reply. Text may hold variables such as <c>{{contact.first_name|دوست عزیز}}</c>.</summary>
    public OutboundContent? Message { get; init; }

    /// <summary>public_reply: variants, one picked at random per comment.</summary>
    public IReadOnlyList<string>? Replies { get; init; }

    public Guid? FlowId { get; init; }

    /// <summary>send_cards: the storefront card set.</summary>
    public Guid? CardSetId { get; init; }

    public string? Tag { get; init; }

    public string? Field { get; init; }

    public string? Value { get; init; }

    public string? Reason { get; init; }

    /// <summary>notify_team: the notification's title.</summary>
    public string? Title { get; init; }
}

/// <summary>An automation rule: one trigger, a list of actions, a priority (higher wins).</summary>
public sealed class AutomationRule : AggregateRoot<Guid>, ITenantOwned, ISoftDeletable
{
    private AutomationRule()
    {
    }

    public Guid WorkspaceId { get; private set; }

    /// <summary>Null: every channel account of the workspace.</summary>
    public Guid? ChannelAccountId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string TriggerKind { get; private set; } = string.Empty;

    public TriggerConfig Trigger { get; private set; } = new();

    public List<RuleAction> Actions { get; private set; } = [];

    /// <summary>The post a comment rule is bound to: the configured post, or for <c>next_post</c> the first new post that got a comment.</summary>
    public string? PostExternalId { get; private set; }

    public int Priority { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>Bumped on every change; part of <see cref="RulesChanged"/>.</summary>
    public int Revision { get; private set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public static AutomationRule Create(Guid workspaceId, RuleDefinition definition, DateTimeOffset now)
    {
        var rule = new AutomationRule { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, CreatedAt = now };
        rule.Apply(definition);
        return rule;
    }

    public void Update(RuleDefinition definition)
    {
        var rebind = definition.TriggerKind != TriggerKind || definition.Trigger.Scope != Trigger.Scope || definition.Trigger.PostId != Trigger.PostId;
        Apply(definition, keepBinding: !rebind);
    }

    /// <summary>Binds a <c>next_post</c> rule to its post. Returns false when it is already bound.</summary>
    public bool BindToPost(string mediaId)
    {
        if (PostExternalId is not null)
        {
            return false;
        }

        PostExternalId = mediaId;
        Revision++;
        return true;
    }

    public void Delete(DateTimeOffset now)
    {
        DeletedAt = now;
        Enabled = false;
        Revision++;
    }

    private void Apply(RuleDefinition d, bool keepBinding = false)
    {
        Name = d.Name.Trim();
        ChannelAccountId = d.ChannelAccountId;
        TriggerKind = d.TriggerKind;
        Trigger = d.Trigger;
        Actions = [.. d.Actions];
        Priority = d.Priority;
        Enabled = d.Enabled;
        if (!keepBinding)
        {
            PostExternalId = d.Trigger.Scope == CommentScopes.Post ? d.Trigger.PostId : null;
        }

        Revision++;
    }

    public RuleView ToView() => new(Id, ChannelAccountId, TriggerKind, Trigger, Actions, PostExternalId, Priority, CreatedAt);
}

public sealed record RuleDefinition(string Name, Guid? ChannelAccountId, string TriggerKind, TriggerConfig Trigger, IReadOnlyList<RuleAction> Actions, int Priority, bool Enabled);

/// <summary>The cached, read-only form of an enabled rule that the engine evaluates.</summary>
public sealed record RuleView(
    Guid Id, Guid? ChannelAccountId, string TriggerKind, TriggerConfig Trigger, IReadOnlyList<RuleAction> Actions, string? PostId, int Priority, DateTimeOffset CreatedAt)
{
    public TimeSpan Cooldown => TimeSpan.FromMinutes(Trigger.CooldownMinutes ?? (TriggerKind == TriggerKinds.OutsideBusinessHours ? 720 : 0));
}

/// <summary>
/// Keyword matching on Persian text. Both sides are normalized (Arabic ي and ك to Persian, Persian and Arabic-Indic digits to ASCII,
/// diacritics and kashida removed), lower-cased, and punctuation is dropped. Spaces and half-spaces (ZWNJ) are ignored when comparing,
/// so "می‌خوام", "می خوام" and "میخوام" are the same keyword.
/// </summary>
public static class KeywordMatcher
{
    private const int MaxPhraseWords = 4;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    public static bool Matches(TriggerConfig trigger, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var key = Key(text);
        var keywords = trigger.Keywords ?? [];
        return (trigger.Match ?? MatchModes.Contains) switch
        {
            MatchModes.Exact => keywords.Any(k => Compact(Key(k)) is { Length: > 0 } kw && Compact(key) == kw),
            MatchModes.Contains => keywords.Any(k => Compact(Key(k)) is { Length: > 0 } kw && Compact(key).Contains(kw, StringComparison.Ordinal)),
            MatchModes.AnyOf => keywords.Any(k => ContainsPhrase(key, Key(k))),
            MatchModes.Pattern => trigger.Pattern is { } pattern && CompilePattern(pattern) is { } regex && SafeIsMatch(regex, Compact(key)),
            _ => false,
        };
    }

    /// <summary>Matching form: normalized, lower case, punctuation and symbols as spaces, ZWNJ as a space, single spaces.</summary>
    public static string Key(string? text)
    {
        var normalized = PersianNormalizer.Normalize(text).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var separator = rune.Value == PersianNormalizer.Zwnj || Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune) || Rune.IsSymbol(rune)
                || category is UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate or UnicodeCategory.Format;
            if (separator)
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }

                continue;
            }

            sb.Append(rune.ToString());
        }

        return sb.ToString().TrimEnd();
    }

    public static string Compact(string key) => key.Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>True when one to four consecutive words of the text, joined without spaces, equal the phrase without spaces.</summary>
    private static bool ContainsPhrase(string textKey, string phraseKey)
    {
        var phrase = Compact(phraseKey);
        if (phrase.Length == 0)
        {
            return false;
        }

        var words = textKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var joined = string.Empty;
            for (var n = 0; n < MaxPhraseWords && i + n < words.Length; n++)
            {
                joined += words[i + n];
                if (joined == phrase)
                {
                    return true;
                }

                if (joined.Length >= phrase.Length)
                {
                    break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Compiles a pattern: alternatives separated by <c>|</c>, <c>*</c> for any text, everything else literal and normalized like keywords.
    /// The result is a non-backtracking regular expression, so no input can make it slow. Null when the pattern is empty.
    /// </summary>
    public static Regex? CompilePattern(string pattern)
    {
        var alternatives = pattern.Split('|')
            .Select(alt => string.Join(".*", alt.Split('*').Select(part => Regex.Escape(Compact(Key(part))))))
            .Where(alt => alt.Replace(".*", string.Empty, StringComparison.Ordinal).Length > 0)
            .ToList();
        return alternatives.Count == 0
            ? null
            : new Regex("^(?:" + string.Join("|", alternatives) + ")$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternTimeout);
    }

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>An inbound DM as the rule evaluator sees it.</summary>
public sealed record DmInput(Guid ChannelAccountId, string Kind, string? Text, string? Payload, bool IsNewConversation);

/// <summary>A comment as the rule evaluator sees it.</summary>
public sealed record CommentInput(Guid ChannelAccountId, string Kind, string MediaId, string? Text);

/// <summary>
/// Picks rules for an event (backend plan 9.5). Candidates are ordered by priority (higher first), then by how specific the trigger is
/// (a postback payload or an exact keyword beats "contains"), then by age (older first). The engine takes the first candidate that
/// passes its runtime checks (cooldown, post binding); only one rule fires per event.
/// </summary>
public static class RuleEvaluator
{
    public static IReadOnlyList<RuleView> DmCandidates(IEnumerable<RuleView> rules, DmInput dm) =>
        Order(rules.Where(r => AppliesTo(r, dm.ChannelAccountId) && MatchesDm(r, dm)));

    /// <summary>Rules for a customer who wrote outside business hours, when no keyword rule answered.</summary>
    public static IReadOnlyList<RuleView> OutsideHoursCandidates(IEnumerable<RuleView> rules, Guid channelAccountId) =>
        Order(rules.Where(r => r.TriggerKind == TriggerKinds.OutsideBusinessHours && AppliesTo(r, channelAccountId)));

    /// <summary>
    /// Comment rules for this comment. A <c>next_post</c> rule that is not bound yet is a candidate for any post; the engine binds it
    /// when the post is newer than the rule.
    /// </summary>
    public static IReadOnlyList<RuleView> CommentCandidates(IEnumerable<RuleView> rules, CommentInput comment) =>
        Order(rules.Where(r => AppliesTo(r, comment.ChannelAccountId) && MatchesComment(r, comment)));

    public static bool NeedsPostBinding(RuleView rule) => rule.Trigger.Scope == CommentScopes.NextPost && rule.PostId is null;

    private static bool AppliesTo(RuleView rule, Guid channelAccountId) => rule.ChannelAccountId is null || rule.ChannelAccountId == channelAccountId;

    private static bool MatchesDm(RuleView rule, DmInput dm)
    {
        var t = rule.Trigger;
        return rule.TriggerKind switch
        {
            TriggerKinds.Postback => dm.Payload is not null && t.Payload is not null && string.Equals(dm.Payload, t.Payload, StringComparison.Ordinal),
            TriggerKinds.DmKeyword => dm.Payload is null && KeywordMatcher.Matches(t, dm.Text),
            TriggerKinds.StoryReply => dm.Kind == InboundKinds.StoryReply && OptionalKeywords(t, dm.Text),
            TriggerKinds.StoryMention => dm.Kind == InboundKinds.StoryMention,
            TriggerKinds.PostShare => dm.Kind == InboundKinds.Share,
            TriggerKinds.NewConversation => dm.IsNewConversation,
            _ => false,
        };
    }

    private static bool MatchesComment(RuleView rule, CommentInput comment)
    {
        var t = rule.Trigger;
        var kindMatches = rule.TriggerKind switch
        {
            TriggerKinds.CommentKeyword => comment.Kind == CommentKinds.Comment && KeywordMatcher.Matches(t, comment.Text),
            TriggerKinds.CommentAny => comment.Kind == CommentKinds.Comment,
            TriggerKinds.LiveComment => comment.Kind == CommentKinds.LiveComment && OptionalKeywords(t, comment.Text),
            _ => false,
        };
        if (!kindMatches)
        {
            return false;
        }

        return (t.Scope ?? CommentScopes.AllPosts) switch
        {
            CommentScopes.AllPosts => true,
            CommentScopes.Post => rule.PostId == comment.MediaId,
            CommentScopes.NextPost => rule.PostId is null || rule.PostId == comment.MediaId,
            _ => false,
        };
    }

    private static bool OptionalKeywords(TriggerConfig t, string? text) =>
        (t.Keywords is null or { Count: 0 } && t.Pattern is null) || KeywordMatcher.Matches(t, text);

    private static IReadOnlyList<RuleView> Order(IEnumerable<RuleView> rules) =>
        [.. rules.OrderByDescending(r => r.Priority).ThenBy(Specificity).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)];

    /// <summary>Lower is more specific.</summary>
    private static int Specificity(RuleView rule) => rule.TriggerKind switch
    {
        TriggerKinds.Postback => 0,
        TriggerKinds.DmKeyword or TriggerKinds.CommentKeyword or TriggerKinds.LiveComment when rule.Trigger.Match == MatchModes.Exact => 1,
        TriggerKinds.StoryReply or TriggerKinds.StoryMention or TriggerKinds.PostShare => 2,
        TriggerKinds.DmKeyword or TriggerKinds.CommentKeyword or TriggerKinds.LiveComment => 3,
        TriggerKinds.CommentAny => 4,
        TriggerKinds.NewConversation => 5,
        _ => 6,
    };
}

/// <summary>
/// Fills <c>{{name}}</c> and <c>{{name|fallback}}</c> in message text. Known names: <c>contact.first_name</c>, <c>contact.name</c>,
/// <c>contact.username</c>, <c>comment.username</c>, <c>comment.text</c>, <c>flow.&lt;variable&gt;</c>, <c>field.&lt;name&gt;</c>.
/// An unknown or empty value becomes the fallback (or nothing).
/// </summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*(?:\|([^}]*))?\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    public static string Render(string template, IReadOnlyDictionary<string, string?> values) =>
        Placeholder().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrWhiteSpace(v) ? v : m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty);

    public static OutboundContent Render(OutboundContent content, IReadOnlyDictionary<string, string?> values) => content with
    {
        Text = content.Text is null ? null : Render(content.Text, values),
        Cards = content.Cards?.Select(c => c with { Title = Render(c.Title, values), Subtitle = c.Subtitle is null ? null : Render(c.Subtitle, values) }).ToList(),
    };

    public static string? FirstName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}
