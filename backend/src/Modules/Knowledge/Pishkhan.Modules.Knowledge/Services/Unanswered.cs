using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Knowledge.Services;

/// <summary>AI group: keeps the questions the agent could not answer, one row per normalised question.</summary>
internal sealed class UnansweredQuestionHandler(KnowledgeDbContext db) : IdempotentHandler<UnansweredQuestionRaised, KnowledgeDbContext>(db)
{
    protected override async Task HandleOnceAsync(UnansweredQuestionRaised message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var question = message.Question.Trim();
        var key = KeyOf(question);
        if (key.Length == 0)
        {
            return;
        }

        if (question.Length > UnansweredQuestion.MaxQuestion)
        {
            question = question[..UnansweredQuestion.MaxQuestion];
        }

        var existing = await Db.Unanswered.FirstOrDefaultAsync(q => q.Key == key, cancellationToken);
        if (existing is null)
        {
            Db.Unanswered.Add(UnansweredQuestion.Create(message.WorkspaceId, question, key, message.ConversationId, message.AskedAt));
            return;
        }

        existing.Count++;
        existing.Status = UnansweredStatuses.Open;
        if (message.AskedAt > existing.LastAskedAt)
        {
            existing.Question = question;
            existing.LastAskedAt = message.AskedAt;
            existing.LastConversationId = message.ConversationId;
        }
    }

    /// <summary>Letters and digits of the normalised text, lower case, single spaces.</summary>
    public static string KeyOf(string text)
    {
        var words = PersianNormalizer.Normalize(text)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]).ToLowerInvariant())
            .Where(w => w.Length > 0);
        var key = string.Join(' ', words);
        return key.Length <= UnansweredQuestion.MaxQuestion ? key : key[..UnansweredQuestion.MaxQuestion];
    }
}
