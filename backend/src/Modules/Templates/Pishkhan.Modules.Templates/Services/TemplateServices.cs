using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Templates.Contracts;
using Pishkhan.Modules.Templates.Domain;
using Pishkhan.Modules.Templates.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Templates.Services;

/// <summary>
/// Checks a template's content. Issues name the item and the problem (<c>faqs[shipping-cost]: validation.text.length</c>); a draft with
/// issues can be saved, but not published.
/// </summary>
internal sealed partial class TemplateContentValidator(IAutomationTemplateValidator automation)
{
    public const int MaxFacts = 30;
    public const int MaxFaqs = 100;
    public const int MaxFlows = 20;
    public const int MaxRules = 30;
    public const int MaxQuestion = 500;
    public const int MaxAnswer = 4_000;
    public const int MaxLabel = 100;
    public const int MaxPrompt = 300;
    public const int MaxProfileName = 60;
    public const int MaxBrandVoice = 1_000;
    public const int MaxPhrase = 100;
    public const int MaxPhrases = 50;

    /// <summary>The AI agent's tones (Agent module, <c>AgentTones</c>).</summary>
    public static readonly IReadOnlySet<string> Tones = new HashSet<string>(StringComparer.Ordinal) { "friendly", "formal", "playful" };

    /// <summary>Workspace industries (Tenancy module, <c>Workspace.Industries</c>).</summary>
    public static readonly string[] Industries = ["clothing", "cosmetics", "jewelry", "beauty_clinic", "courses", "other"];

    [GeneratedRegex("^[a-z][a-z0-9_.-]{1,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public IReadOnlyList<string> Validate(TemplateContent? content)
    {
        if (content is null)
        {
            return ["content: validation.required"];
        }

        var issues = new List<string>();
        var facts = content.RequiredFacts ?? [];
        var faqs = content.Faqs ?? [];
        var flows = content.Flows ?? [];
        var rules = content.Rules ?? [];
        Count(issues, "requiredFacts", facts.Count, MaxFacts);
        Count(issues, "faqs", faqs.Count, MaxFaqs);
        Count(issues, "flows", flows.Count, MaxFlows);
        Count(issues, "rules", rules.Count, MaxRules);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in facts)
        {
            var label = $"requiredFacts[{f.Key}]";
            Key(issues, label, f.Key, keys);
            Text(issues, label + ".label", f.Label, MaxLabel);
            Text(issues, label + ".prompt", f.Prompt, MaxPrompt);
        }

        keys.Clear();
        foreach (var f in faqs)
        {
            var label = $"faqs[{f.Key}]";
            Key(issues, label, f.Key, keys);
            Text(issues, label + ".question", f.Question, MaxQuestion);
            Text(issues, label + ".answer", f.Answer, MaxAnswer);
        }

        if (content.AgentProfile is { } p)
        {
            Text(issues, "agentProfile.name", p.Name, MaxProfileName);
            if (!Tones.Contains(p.Tone ?? string.Empty))
            {
                issues.Add("agentProfile.tone: templates.tone_invalid");
            }

            if (p.BrandVoice is { Length: > MaxBrandVoice })
            {
                issues.Add("agentProfile.brandVoice: validation.text.length");
            }

            Phrases(issues, "agentProfile.neverSay", p.NeverSay);
            Phrases(issues, "agentProfile.handoffKeywords", p.HandoffKeywords);
        }

        issues.AddRange(automation.Validate(flows, rules));
        return issues;
    }

    private static void Count(List<string> issues, string field, int count, int max)
    {
        if (count > max)
        {
            issues.Add($"{field}: templates.too_many");
        }
    }

    private static void Key(List<string> issues, string label, string? key, HashSet<string> seen)
    {
        if (key is null || !KeyPattern().IsMatch(key) || !seen.Add(key))
        {
            issues.Add($"{label}: template.key_invalid");
        }
    }

    private static void Text(List<string> issues, string field, string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > max)
        {
            issues.Add($"{field}: validation.text.length");
        }
    }

    private static void Phrases(List<string> issues, string field, IReadOnlyList<string>? phrases)
    {
        if (phrases is { } list && (list.Count > MaxPhrases || list.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > MaxPhrase)))
        {
            issues.Add($"{field}: validation.invalid");
        }
    }
}

/// <summary>What applying a template added.</summary>
internal sealed record ApplyOutcome(int FaqsAdded, int FlowsAdded, int RulesAdded, bool ProfileApplied, int Skipped);

/// <summary>
/// Applies a published template to the current workspace through the Knowledge, Automation and Agent modules. Each module keeps the
/// template's keys, so a repeated or interrupted apply adds only what is missing; nothing the owner changed or deleted is touched.
/// </summary>
internal sealed class TemplateApplier(IKnowledgeTemplates knowledge, IAutomationTemplates automation, IAgentTemplates agent)
{
    public async Task<ApplyOutcome> ApplyAsync(Guid workspaceId, TemplateContent content, Guid? userId, CancellationToken cancellationToken)
    {
        // Flows and rules first: they are the part most likely to be refused, and FAQs added before a failure would be applied again harmlessly.
        var automationResult = await automation.ApplyAsync(workspaceId, content.Flows ?? [], content.Rules ?? [], cancellationToken);
        var (faqsAdded, faqsSkipped) = await knowledge.ApplyFaqsAsync(workspaceId, content.Faqs ?? [], userId, cancellationToken);
        var profileApplied = content.AgentProfile is { } profile && await agent.ApplyProfileAsync(workspaceId, profile, cancellationToken);
        return new ApplyOutcome(
            faqsAdded, automationResult.FlowsCreated, automationResult.RulesCreated, profileApplied, automationResult.Skipped + faqsSkipped);
    }
}

internal sealed class TemplateQueries(TemplatesDbContext db) : ITemplateQueries
{
    public async Task<IReadOnlyList<RequiredFact>> GetRequiredFactsAsync(string industry, CancellationToken cancellationToken)
    {
        var content = await db.Templates.AsNoTracking()
            .Where(t => t.Industry == industry && t.Status == TemplateStatuses.Published)
            .Select(t => t.Content)
            .FirstOrDefaultAsync(cancellationToken);
        return content?.RequiredFacts ?? [];
    }
}

/// <summary>
/// Adds the built-in templates (embedded <c>Seeds/*.json</c>) for every industry that has no template yet, published as version 1.
/// Run by the Migrator; staff edit them afterwards in the back office, so an existing industry is never overwritten.
/// </summary>
internal sealed class TemplateSeeder(TemplatesDbContext db, IClock clock) : ITemplateSeeder
{
    public async Task<int> SeedAsync(CancellationToken cancellationToken)
    {
        var existing = (await db.Templates.AsNoTracking().Select(t => t.Industry).Distinct().ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var now = clock.UtcNow;
        var added = 0;
        foreach (var seed in BuiltInTemplates.Load().Where(s => !existing.Contains(s.Industry)))
        {
            var template = IndustryTemplate.Create(seed.Industry, seed.Name, seed.Description, seed.Content, null, now);
            template.Publish(null, now);
            db.Templates.Add(template);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return added;
    }
}

/// <summary>Seeds the built-in industry templates (the Migrator's <c>migrate</c> command).</summary>
public interface ITemplateSeeder
{
    /// <summary>Returns how many templates were added.</summary>
    Task<int> SeedAsync(CancellationToken cancellationToken);
}

/// <summary>A built-in template as stored in <c>Seeds/{industry}.json</c>.</summary>
internal sealed record TemplateSeed(string Industry, string Name, string? Description, TemplateContent Content);

internal static class BuiltInTemplates
{
    public static IReadOnlyList<TemplateSeed> Load()
    {
        var assembly = typeof(BuiltInTemplates).Assembly;
        var seeds = new List<TemplateSeed>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Pishkhan.Modules.Templates.Seeds.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            seeds.Add(System.Text.Json.JsonSerializer.Deserialize<TemplateSeed>(stream, Pishkhan.Contracts.EnvelopeJson.Options)
                ?? throw new InvalidOperationException($"Seed {name} is empty"));
        }

        return seeds;
    }
}
