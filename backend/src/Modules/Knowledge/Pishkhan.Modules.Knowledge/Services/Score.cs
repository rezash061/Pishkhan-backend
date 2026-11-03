using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Modules.Templates.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Knowledge.Services;

/// <summary>A required fact and whether an enabled source answers it.</summary>
public sealed record FactStatus(string Key, string Label, string Prompt, bool Covered, Guid? SourceId);

/// <summary>One part of the score: <c>facts</c>, <c>products</c> or <c>unanswered</c>, with the points it earned out of <c>max</c>.</summary>
public sealed record ScorePart(string Key, int Points, int Max);

public sealed record ProductStats(int Active, int Priced, int InStock);

/// <summary>
/// The knowledge score, 0 to 100 (backend plan 9.7). <c>suggestions</c> are message keys for the next steps, most useful first:
/// <c>knowledge.suggest.answer_facts</c>, <c>knowledge.suggest.add_faqs</c>, <c>knowledge.suggest.add_products</c>,
/// <c>knowledge.suggest.add_prices</c>, <c>knowledge.suggest.add_stock</c>, <c>knowledge.suggest.answer_unanswered</c>.
/// </summary>
public sealed record KnowledgeScoreResponse(
    int Score, string Industry, IReadOnlyList<ScorePart> Parts, IReadOnlyList<FactStatus> Facts, ProductStats? Products, int OpenUnanswered,
    IReadOnlyList<string> Suggestions);

/// <summary>
/// Computes the knowledge score from three parts:
/// <list type="bullet">
/// <item><b>facts</b>: the share of the industry template's required facts answered by an enabled source (its <c>factKey</c>); without a
/// template, the number of enabled FAQs (10 or more earns the full part).</item>
/// <item><b>products</b>: the share of active products that are priced and in stock. Service industries (beauty_clinic, courses) without
/// products skip this part and its points go to facts.</item>
/// <item><b>unanswered</b>: full when no customer question waits for an answer from the last 7 days, losing a tenth for each open one.</item>
/// </list>
/// </summary>
internal sealed class KnowledgeScorer(KnowledgeDbContext db, IWorkspaceQueries workspaces, ITemplateQueries templates, ICatalogQueries catalog, IClock clock)
{
    public const int FactsPoints = 60;
    public const int ProductsPoints = 25;
    public const int UnansweredPoints = 15;
    public const int FaqsForFullPoints = 10;
    public const int UnansweredWindowDays = 7;

    /// <summary>Industries that sell services, so a missing catalog is normal.</summary>
    public static readonly IReadOnlySet<string> ServiceIndustries = new HashSet<string>(StringComparer.Ordinal) { "beauty_clinic", "courses" };

    public async Task<KnowledgeScoreResponse> ScoreAsync(Guid workspaceId, CancellationToken ct)
    {
        var industry = await workspaces.GetIndustryAsync(workspaceId, ct) ?? "other";
        var required = await templates.GetRequiredFactsAsync(industry, ct);
        var answered = await db.Sources.AsNoTracking()
            .Where(s => s.Enabled && s.FactKey != null)
            .Select(s => new { s.FactKey, s.Id })
            .ToListAsync(ct);
        var bySource = answered.GroupBy(a => a.FactKey!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var facts = required
            .Select(f => new FactStatus(f.Key, f.Label, f.Prompt, bySource.ContainsKey(f.Key), bySource.TryGetValue(f.Key, out var id) ? id : null))
            .OrderBy(f => f.Covered)
            .ToList();
        var enabledFaqs = required.Count == 0 ? await db.Sources.CountAsync(s => s.Enabled && s.Kind == KnowledgeSourceKinds.Faq, ct) : 0;

        var stats = await catalog.GetStatsAsync(ct);
        var since = clock.UtcNow.AddDays(-UnansweredWindowDays);
        var open = await db.Unanswered.CountAsync(u => u.Status == UnansweredStatuses.Open && u.LastAskedAt >= since, ct);

        return Compute(industry, facts, enabledFaqs, new ProductStats(stats.Active, stats.Priced, stats.InStock), open);
    }

    /// <summary>The scoring itself, without I/O (unit tested).</summary>
    public static KnowledgeScoreResponse Compute(string industry, IReadOnlyList<FactStatus> facts, int enabledFaqs, ProductStats products, int openUnanswered)
    {
        var productsApply = products.Active > 0 || !ServiceIndustries.Contains(industry);
        var factsMax = productsApply ? FactsPoints : FactsPoints + ProductsPoints;

        var factShare = facts.Count > 0 ? (double)facts.Count(f => f.Covered) / facts.Count : Math.Min(enabledFaqs, FaqsForFullPoints) / (double)FaqsForFullPoints;
        var factPoints = (int)Math.Round(factShare * factsMax, MidpointRounding.AwayFromZero);

        var productPoints = 0;
        if (productsApply && products.Active > 0)
        {
            var share = (Math.Min(products.Priced, products.Active) + Math.Min(products.InStock, products.Active)) / (2.0 * products.Active);
            productPoints = (int)Math.Round(share * ProductsPoints, MidpointRounding.AwayFromZero);
        }

        var unansweredPoints = (int)Math.Round(Math.Max(0, 1 - (openUnanswered / 10.0)) * UnansweredPoints, MidpointRounding.AwayFromZero);

        var parts = new List<ScorePart> { new("facts", factPoints, factsMax) };
        if (productsApply)
        {
            parts.Add(new ScorePart("products", productPoints, ProductsPoints));
        }

        parts.Add(new ScorePart("unanswered", unansweredPoints, UnansweredPoints));

        var suggestions = new List<string>();
        if (facts.Count > 0 && facts.Any(f => !f.Covered))
        {
            suggestions.Add("knowledge.suggest.answer_facts");
        }
        else if (facts.Count == 0 && enabledFaqs < FaqsForFullPoints)
        {
            suggestions.Add("knowledge.suggest.add_faqs");
        }

        if (productsApply)
        {
            if (products.Active == 0)
            {
                suggestions.Add("knowledge.suggest.add_products");
            }
            else
            {
                if (products.Priced < products.Active)
                {
                    suggestions.Add("knowledge.suggest.add_prices");
                }

                if (products.InStock < products.Active)
                {
                    suggestions.Add("knowledge.suggest.add_stock");
                }
            }
        }

        if (openUnanswered > 0)
        {
            suggestions.Add("knowledge.suggest.answer_unanswered");
        }

        return new KnowledgeScoreResponse(
            Math.Clamp(parts.Sum(p => p.Points), 0, 100), industry, parts, facts, productsApply ? products : null, openUnanswered, suggestions);
    }
}
