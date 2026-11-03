using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Automation.Engine;
using Pishkhan.Modules.Automation.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Automation.Features;

/// <summary>
/// Adds an industry template's flows and rules to a workspace (BE-2.9). Each item carries the template's key, kept after the owner deletes
/// it, so applying a template again adds only what the workspace never had. Flows are published at once (rules can only start published
/// flows); rules are added switched off, for the owner to read and switch on.
/// </summary>
internal sealed class AutomationTemplates(AutomationDbContext db, ICacheInvalidator invalidator, AutomationCache cache, IClock clock) : IAutomationTemplates
{
    public async Task<TemplateAutomationResult> ApplyAsync(
        Guid workspaceId, IReadOnlyList<TemplateFlow> flows, IReadOnlyList<TemplateRule> rules, CancellationToken cancellationToken)
    {
        var prepared = AutomationTemplateValidator.Prepare(flows, rules, out var errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Industry template is invalid: {string.Join(", ", errors)}");
        }

        // Deleted items count as had: the owner removed them on purpose.
        var hadFlows = await db.Flows.IgnoreQueryFilters([ModuleDbContext.SoftDeleteFilter])
            .Where(f => f.WorkspaceId == workspaceId && f.TemplateKey != null)
            .Select(f => new { Key = f.TemplateKey!, f.Id, Live = f.DeletedAt == null && f.PublishedVersion != null })
            .ToListAsync(cancellationToken);
        var hadRules = (await db.Rules.IgnoreQueryFilters([ModuleDbContext.SoftDeleteFilter])
            .Where(r => r.WorkspaceId == workspaceId && r.TemplateKey != null)
            .Select(r => r.TemplateKey!)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var now = clock.UtcNow;
        var flowIds = hadFlows.Where(f => f.Live).ToDictionary(f => f.Key, f => f.Id, StringComparer.Ordinal);
        var hadFlowKeys = hadFlows.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        int flowsCreated = 0, rulesCreated = 0, skipped = 0;

        foreach (var (key, name, graph) in prepared.Flows)
        {
            if (hadFlowKeys.Contains(key))
            {
                skipped++;
                continue;
            }

            var flow = Flow.Create(workspaceId, name, graph, now);
            flow.FromTemplate(key);
            var version = flow.Publish(now);
            db.Flows.Add(flow);
            db.FlowVersions.Add(version);
            db.Publish(new FlowPublished(workspaceId, flow.Id, version.Version), flow.Id.ToString());
            flowIds[key] = flow.Id;
            flowsCreated++;
        }

        foreach (var (key, definition, flowKey) in prepared.Rules)
        {
            if (hadRules.Contains(key))
            {
                skipped++;
                continue;
            }

            // A rule whose flow the owner deleted would start nothing: leave it out.
            Guid? flowId = null;
            if (flowKey is not null)
            {
                if (!flowIds.TryGetValue(flowKey, out var id))
                {
                    skipped++;
                    continue;
                }

                flowId = id;
            }

            var actions = definition.Actions.Select(a => a.Kind == ActionKinds.StartFlow ? a with { FlowId = flowId } : a).ToList();
            var rule = AutomationRule.Create(workspaceId, definition with { Actions = actions, Enabled = false }, now);
            rule.FromTemplate(key);
            db.Rules.Add(rule);
            db.Publish(new RulesChanged(workspaceId, rule.Id, rule.Revision), workspaceId.ToString());
            rulesCreated++;
        }

        if (flowsCreated + rulesCreated == 0)
        {
            return new TemplateAutomationResult(0, 0, skipped);
        }

        invalidator.Enqueue(db, AutomationCacheTags.Workspace(workspaceId));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // The same template was applied at the same moment: that apply added these items.
            db.ChangeTracker.Clear();
            return new TemplateAutomationResult(0, 0, prepared.Flows.Count + prepared.Rules.Count);
        }

        await cache.InvalidateLocalAsync(workspaceId, cancellationToken);
        return new TemplateAutomationResult(flowsCreated, rulesCreated, skipped);
    }
}

/// <summary>Parses and checks a template's flows and rules; shared by the AdminApi (on save) and <see cref="AutomationTemplates"/> (on apply).</summary>
internal sealed partial class AutomationTemplateValidator : IAutomationTemplateValidator
{
    [GeneratedRegex("^[a-z][a-z0-9_.-]{1,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public IReadOnlyList<string> Validate(IReadOnlyList<TemplateFlow> flows, IReadOnlyList<TemplateRule> rules)
    {
        Prepare(flows, rules, out var errors);
        return errors;
    }

    internal sealed record Prepared(List<(string Key, string Name, FlowGraph Graph)> Flows, List<(string Key, RuleDefinition Definition, string? FlowKey)> Rules);

    internal static Prepared Prepare(IReadOnlyList<TemplateFlow> flows, IReadOnlyList<TemplateRule> rules, out List<string> errors)
    {
        errors = [];
        var result = new Prepared([], []);
        var flowKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in flows)
        {
            var label = $"flows[{f.Key}]";
            if (!KeyPattern().IsMatch(f.Key ?? string.Empty) || !flowKeys.Add(f.Key!))
            {
                errors.Add($"{label}: template.key_invalid");
                continue;
            }

            if (string.IsNullOrWhiteSpace(f.Name) || f.Name.Trim().Length > 100)
            {
                errors.Add($"{label}: validation.name.length");
                continue;
            }

            FlowGraph? graph;
            try
            {
                graph = f.Graph.Deserialize<FlowGraph>(EnvelopeJson.Options);
            }
            catch (JsonException)
            {
                graph = null;
            }

            if (graph?.Nodes is null || graph.Edges is null || graph.Nodes.Count == 0)
            {
                errors.Add($"{label}: flow.empty");
                continue;
            }

            if (graph.Nodes.Any(n => n.CardSetId is not null) || graph.Nodes.Any(n => n.Actions?.Any(a => a.FlowId is not null || a.CardSetId is not null) == true))
            {
                // Card sets and flows belong to a workspace: a template cannot point at them.
                errors.Add($"{label}: template.workspace_reference");
                continue;
            }

            var validation = FlowValidator.Validate(graph);
            if (!validation.IsValid)
            {
                errors.AddRange(validation.Errors.Select(e => $"{label}: {e.Code}"));
                continue;
            }

            result.Flows.Add((f.Key!, f.Name.Trim(), graph));
        }

        var ruleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            var label = $"rules[{r.Key}]";
            if (!KeyPattern().IsMatch(r.Key ?? string.Empty) || !ruleKeys.Add(r.Key!))
            {
                errors.Add($"{label}: template.key_invalid");
                continue;
            }

            if (!TryReadActions(r.Actions, out var actions, out var flowKey))
            {
                errors.Add($"{label}: template.actions_invalid");
                continue;
            }

            if (flowKey is not null && !flowKeys.Contains(flowKey))
            {
                errors.Add($"{label}: template.flow_key_unknown");
                continue;
            }

            if (actions.Any(a => a.CardSetId is not null || (a.Kind != ActionKinds.StartFlow && a.FlowId is not null)))
            {
                errors.Add($"{label}: template.workspace_reference");
                continue;
            }

            TriggerConfig? trigger;
            try
            {
                trigger = r.Trigger.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? new TriggerConfig() : r.Trigger.Deserialize<TriggerConfig>(EnvelopeJson.Options);
            }
            catch (JsonException)
            {
                trigger = null;
            }

            if (trigger is null || trigger.PostId is not null)
            {
                errors.Add($"{label}: template.trigger_invalid");
                continue;
            }

            var definition = new RuleDefinition(r.Name ?? string.Empty, null, r.TriggerKind ?? string.Empty, trigger, actions, r.Priority, Enabled: false);

            // The validator needs a flow id for start_flow; the real one is known only when the template is applied.
            var check = flowKey is null ? definition : definition with
            {
                Actions = [.. actions.Select(a => a.Kind == ActionKinds.StartFlow ? a with { FlowId = Guid.Empty } : a)],
            };
            var invalid = RuleValidator.Validate(check);
            if (invalid.Count > 0)
            {
                errors.AddRange(invalid.Select(e => $"{label}: {e.Code}"));
                continue;
            }

            result.Rules.Add((r.Key!, definition, flowKey));
        }

        return result;
    }

    /// <summary>Reads a template rule's actions; a start_flow action names its flow by <c>flowKey</c> instead of <c>flowId</c>.</summary>
    private static bool TryReadActions(JsonElement element, out List<RuleAction> actions, out string? flowKey)
    {
        actions = [];
        flowKey = null;
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        try
        {
            foreach (var item in element.EnumerateArray())
            {
                if (JsonNode.Parse(item.GetRawText()) is not JsonObject node)
                {
                    return false;
                }

                if (node["flowKey"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var key))
                {
                    if (flowKey is not null && flowKey != key)
                    {
                        return false;
                    }

                    flowKey = key;
                    node.Remove("flowKey");
                }

                if (node.Deserialize<RuleAction>(EnvelopeJson.Options) is not { } action)
                {
                    return false;
                }

                if (action.Kind == ActionKinds.StartFlow && (action.FlowId is not null || flowKey is null))
                {
                    return false;
                }

                actions.Add(action);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return true;
    }
}
