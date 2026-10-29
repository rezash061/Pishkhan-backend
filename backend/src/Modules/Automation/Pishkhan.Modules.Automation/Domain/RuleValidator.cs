using Pishkhan.Modules.Automation.Contracts;

namespace Pishkhan.Modules.Automation.Domain;

/// <summary>Server-side checks of a rule definition. Returns (field, code) pairs; empty means valid.</summary>
public static class RuleValidator
{
    public const int MaxKeywords = 50;
    public const int MaxActions = 10;
    public const int MaxPublicReplyVariants = 20;

    public static IReadOnlyList<(string Field, string Code)> Validate(RuleDefinition rule)
    {
        var errors = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > 100)
        {
            errors.Add(("name", "validation.name.length"));
        }

        if (rule.Priority is < -1000 or > 1000)
        {
            errors.Add(("priority", "validation.priority.range"));
        }

        if (TriggerKinds.Later.Contains(rule.TriggerKind))
        {
            errors.Add(("triggerKind", "automation.trigger_unavailable"));
            return errors;
        }

        if (!TriggerKinds.Available.Contains(rule.TriggerKind))
        {
            errors.Add(("triggerKind", "automation.trigger_unknown"));
            return errors;
        }

        errors.AddRange(ValidateTrigger(rule));
        errors.AddRange(ValidateActions(rule));
        return errors;
    }

    private static IEnumerable<(string, string)> ValidateTrigger(RuleDefinition rule)
    {
        var t = rule.Trigger ?? new TriggerConfig();
        var keywordsRequired = rule.TriggerKind is TriggerKinds.DmKeyword or TriggerKinds.CommentKeyword;
        var keywordsAllowed = keywordsRequired || rule.TriggerKind is TriggerKinds.StoryReply or TriggerKinds.LiveComment;
        var hasKeywords = t.Keywords is { Count: > 0 } || !string.IsNullOrWhiteSpace(t.Pattern);

        if (keywordsRequired && !hasKeywords)
        {
            yield return ("trigger.keywords", "automation.keywords_required");
        }

        if (hasKeywords && !keywordsAllowed)
        {
            yield return ("trigger.keywords", "automation.keywords_not_allowed");
        }

        if (hasKeywords)
        {
            var match = t.Match ?? MatchModes.Contains;
            if (!MatchModes.All.Contains(match))
            {
                yield return ("trigger.match", "automation.match_invalid");
            }
            else if (match == MatchModes.Pattern)
            {
                if (string.IsNullOrWhiteSpace(t.Pattern) || t.Pattern.Length > 500 || KeywordMatcher.CompilePattern(t.Pattern) is null)
                {
                    yield return ("trigger.pattern", "automation.pattern_invalid");
                }
            }
            else if (t.Keywords is not { Count: > 0 and <= MaxKeywords } || t.Keywords.Any(k => KeywordMatcher.Compact(KeywordMatcher.Key(k)).Length == 0 || k.Length > 100))
            {
                yield return ("trigger.keywords", "automation.keywords_invalid");
            }
        }

        if (TriggerKinds.Comment.Contains(rule.TriggerKind))
        {
            var scope = t.Scope ?? CommentScopes.AllPosts;
            if (!CommentScopes.All.Contains(scope))
            {
                yield return ("trigger.scope", "automation.scope_invalid");
            }

            if (scope == CommentScopes.Post && string.IsNullOrWhiteSpace(t.PostId))
            {
                yield return ("trigger.postId", "automation.post_required");
            }

            // A post (and the next post) belongs to one account.
            if (scope is CommentScopes.Post or CommentScopes.NextPost && rule.ChannelAccountId is null)
            {
                yield return ("channelAccountId", "automation.channel_required");
            }
        }
        else if (t.Scope is not null || t.PostId is not null)
        {
            yield return ("trigger.scope", "automation.scope_not_allowed");
        }

        if (rule.TriggerKind == TriggerKinds.Postback && (string.IsNullOrWhiteSpace(t.Payload) || t.Payload.Length > 1000))
        {
            yield return ("trigger.payload", "automation.payload_required");
        }

        if (t.CooldownMinutes is < 0 or > 30 * 24 * 60)
        {
            yield return ("trigger.cooldownMinutes", "automation.cooldown_invalid");
        }
    }

    private static IEnumerable<(string, string)> ValidateActions(RuleDefinition rule)
    {
        var actions = rule.Actions ?? [];
        if (actions.Count is 0 or > MaxActions)
        {
            yield return ("actions", "automation.actions_count");
            yield break;
        }

        var comment = TriggerKinds.Comment.Contains(rule.TriggerKind);
        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var field = $"actions[{i}]";
            if (ActionKinds.Later.Contains(a.Kind))
            {
                yield return (field, "automation.action_unavailable");
                continue;
            }

            if (!ActionKinds.Available.Contains(a.Kind))
            {
                yield return (field, "automation.action_unknown");
                continue;
            }

            // A commenter has not opened a conversation: only the private reply (one per comment) may reach their DMs.
            if (comment && a.Kind is ActionKinds.SendMessage or ActionKinds.StartFlow)
            {
                yield return (field, "automation.action_needs_conversation");
            }

            if (!comment && a.Kind is ActionKinds.PrivateReply or ActionKinds.PublicReply)
            {
                yield return (field, "automation.action_needs_comment");
            }

            switch (a.Kind)
            {
                case ActionKinds.SendMessage or ActionKinds.PrivateReply:
                    if (a.Message is null || MessageValidator.Validate(a.Message) is not null)
                    {
                        yield return (field + ".message", a.Message is null ? "validation.message.empty" : MessageValidator.Validate(a.Message)!);
                    }

                    break;
                case ActionKinds.PublicReply:
                    if (a.Replies is not { Count: > 0 and <= MaxPublicReplyVariants } || a.Replies.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 300))
                    {
                        yield return (field + ".replies", "automation.replies_invalid");
                    }

                    break;
                case ActionKinds.StartFlow when a.FlowId is null:
                    yield return (field + ".flowId", "automation.flow_required");
                    break;
                case ActionKinds.AddTag or ActionKinds.RemoveTag when string.IsNullOrWhiteSpace(a.Tag) || a.Tag.Length > 50:
                    yield return (field + ".tag", "automation.tag_invalid");
                    break;
                case ActionKinds.SetField when string.IsNullOrWhiteSpace(a.Field) || a.Field.Length > 40 || a.Value is { Length: > 500 }:
                    yield return (field + ".field", "automation.field_invalid");
                    break;
                case ActionKinds.NotifyTeam when a.Title is { Length: > 100 }:
                    yield return (field + ".title", "automation.title_too_long");
                    break;
            }
        }

        if (actions.Count(a => a.Kind == ActionKinds.PrivateReply) > 1)
        {
            yield return ("actions", "automation.one_private_reply");
        }

        if (actions.Count(a => a.Kind == ActionKinds.PublicReply) > 1)
        {
            yield return ("actions", "automation.one_public_reply");
        }

        if (actions.Count(a => a.Kind == ActionKinds.StartFlow) > 1)
        {
            yield return ("actions", "automation.one_flow");
        }
    }
}
