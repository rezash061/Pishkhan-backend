using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Knowledge.Services;

internal sealed class KnowledgeTemplates(KnowledgeDbContext db, IClock clock) : IKnowledgeTemplates
{
    public async Task<(int Created, int Skipped)> ApplyFaqsAsync(Guid workspaceId, IReadOnlyList<TemplateFaq> faqs, Guid? userId, CancellationToken cancellationToken)
    {
        var keys = faqs.Select(f => f.Key).ToList();
        var had = (await db.TemplateItems.Where(t => keys.Contains(t.Key)).Select(t => t.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var now = clock.UtcNow;
        var created = 0;
        foreach (var faq in faqs.Where(f => !had.Contains(f.Key)).DistinctBy(f => f.Key))
        {
            var question = Clip(faq.Question.Trim(), KnowledgeSource.MaxQuestion);
            var source = KnowledgeSource.Create(workspaceId, KnowledgeSourceKinds.Faq, Clip(question, KnowledgeSource.MaxTitle), question, Clip(faq.Answer.Trim(), KnowledgeSource.MaxAnswer), null, userId, now);

            // The answer is a suggestion with blanks for the owner's own details: the AI must not use it until the owner says it is right.
            source.Enabled = false;
            source.FactKey = faq.FactKey is { Length: > 0 and <= 100 } fact ? fact : null;
            db.Sources.Add(source);
            db.TemplateItems.Add(new TemplateItem { WorkspaceId = workspaceId, Key = faq.Key, SourceId = source.Id, CreatedAt = now });
            db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Revision), workspaceId.ToString(), workspaceId: workspaceId);
            created++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (created, faqs.Count - created);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
