using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Knowledge.Features;

/// <summary><c>status</c>: open, answered (<c>answerSourceId</c> is the FAQ made from it) or dismissed. <c>count</c>: how often it was asked.</summary>
public sealed record UnansweredResponse(
    Guid Id, string Question, int Count, string Status, DateTimeOffset FirstAskedAt, DateTimeOffset LastAskedAt, Guid LastConversationId, Guid? AnswerSourceId);

/// <summary>Answer once: <c>answer</c> becomes a FAQ. <c>question</c> replaces the customer's wording in the FAQ when given.</summary>
public sealed record UnansweredAnswerRequest(string Answer, string? Question);

public sealed record UnansweredAnswerResponse(UnansweredResponse Question, SourceResponse Source);

/// <summary>
/// Questions the AI could not answer (backend plan 9.7). Answering one adds a FAQ that is indexed in the background, so the agent can
/// answer it next time.
/// </summary>
internal static class UnansweredEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/knowledge/unanswered").WithTags("Knowledge");

        group.MapGet("/", ListAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Unanswered questions, most recently asked first; status defaults to open")
            .Produces<Page<UnansweredResponse>>()
            .WithStandardProblems();

        group.MapPost("/{questionId:guid}/answer", AnswerAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Answer once: the answer is saved as a FAQ and the question is marked answered")
            .Produces<UnansweredAnswerResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        group.MapPost("/{questionId:guid}/dismiss", DismissAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Hide a question that needs no answer (it opens again if asked again)")
            .Produces<UnansweredResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static UnansweredResponse ToResponse(UnansweredQuestion q) =>
        new(q.Id, q.Question, q.Count, q.Status, q.FirstAskedAt, q.LastAskedAt, q.LastConversationId, q.AnswerSourceId);

    private static async Task<IResult> ListAsync(Guid workspaceId, string? status, string? cursor, int? limit, KnowledgeDbContext db, CancellationToken ct)
    {
        status ??= UnansweredStatuses.Open;
        if (!UnansweredStatuses.All.Contains(status))
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["status"] = ["validation.knowledge.unanswered_status"] }).ToProblem();
        }

        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.Unanswered.AsNoTracking().Where(q => q.Status == status);
        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(q => EF.Functions.LessThan(ValueTuple.Create(q.LastAskedAt, q.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(q => q.LastAskedAt).ThenByDescending(q => q.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].LastAskedAt, rows[take - 1].Id) : null;
        return Results.Ok(new Page<UnansweredResponse>([.. rows.Take(take).Select(ToResponse)], next));
    }

    private static async Task<IResult> AnswerAsync(
        Guid workspaceId, Guid questionId, UnansweredAnswerRequest request, KnowledgeDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var question = await db.Unanswered.FirstOrDefaultAsync(q => q.Id == questionId, ct);
        if (question is null)
        {
            return Error.NotFound().ToProblem();
        }

        var text = string.IsNullOrWhiteSpace(request.Question) ? question.Question.Trim() : request.Question.Trim();
        if (text.Length > KnowledgeSource.MaxQuestion)
        {
            text = string.IsNullOrWhiteSpace(request.Question) ? text[..KnowledgeSource.MaxQuestion] : text;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (text.Length > KnowledgeSource.MaxQuestion)
        {
            errors["question"] = ["validation.knowledge.question"];
        }

        if (string.IsNullOrWhiteSpace(request.Answer) || request.Answer.Trim().Length > KnowledgeSource.MaxAnswer)
        {
            errors["answer"] = ["validation.knowledge.answer"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (question.Status == UnansweredStatuses.Answered)
        {
            return Error.Conflict("knowledge.unanswered.already_answered").ToProblem();
        }

        if (await db.Sources.CountAsync(ct) >= KnowledgeEndpoints.MaxSources)
        {
            return Error.Conflict("knowledge.limit").ToProblem();
        }

        var title = text.Length <= KnowledgeSource.MaxTitle ? text : text[..KnowledgeSource.MaxTitle];
        var source = KnowledgeSource.Create(workspaceId, KnowledgeSourceKinds.Faq, title, text, request.Answer.Trim(), null, caller.UserId, clock.UtcNow);
        db.Sources.Add(source);
        question.Status = UnansweredStatuses.Answered;
        question.AnswerSourceId = source.Id;
        db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Revision), workspaceId.ToString(), workspaceId: workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Json(new UnansweredAnswerResponse(ToResponse(question), KnowledgeEndpoints.ToResponse(source)), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DismissAsync(Guid workspaceId, Guid questionId, KnowledgeDbContext db, CancellationToken ct)
    {
        var question = await db.Unanswered.FirstOrDefaultAsync(q => q.Id == questionId, ct);
        if (question is null)
        {
            return Error.NotFound().ToProblem();
        }

        question.Status = UnansweredStatuses.Dismissed;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(question));
    }
}
