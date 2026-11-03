using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pishkhan.AI;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Modules.Knowledge.Services;
using Pishkhan.Modules.Templates.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Knowledge.Features;

/// <summary>The owner's answer to one required fact, in their own words («ارسال تهران ۵۰ تومن، شهرستان ۸۰»).</summary>
public sealed record OnboardingDraftRequest(string FactKey, string Answer);

/// <summary>A FAQ written from the owner's answer, for them to check. <c>drafted</c>: <c>ai</c>, or <c>fallback</c> when no model was reachable.</summary>
public sealed record OnboardingDraftResponse(string FactKey, string Question, string Answer, string Drafted);

/// <summary>The FAQ as the owner approved it (they may have edited the draft).</summary>
public sealed record OnboardingConfirmRequest(string FactKey, string Question, string Answer);

public sealed record OnboardingConfirmResponse(SourceResponse Source, KnowledgeScoreResponse Score);

/// <summary>
/// The knowledge score and the onboarding interview (BE-1.9). The panel asks the owner each missing required fact (its <c>prompt</c>), the
/// AI turns the answer into a customer-facing FAQ, and only what the owner confirms is saved (enabled, with the fact's key).
/// </summary>
internal static class OnboardingEndpoints
{
    public const int MaxAnswer = 1_000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/knowledge").WithTags("Knowledge");

        group.MapGet("/score", ScoreAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("The knowledge score (0–100), the required facts still missing and what to do next")
            .Produces<KnowledgeScoreResponse>();

        group.MapPost("/onboarding/draft", DraftAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Turn the owner's answer to a required fact into a FAQ draft; nothing is saved")
            .Produces<OnboardingDraftResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/onboarding/confirm", ConfirmAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Save the approved FAQ for a required fact (replacing the fact's earlier FAQ) and return the new score")
            .Produces<OnboardingConfirmResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ScoreAsync(Guid workspaceId, KnowledgeScorer scorer, CancellationToken ct) =>
        Results.Ok(await scorer.ScoreAsync(workspaceId, ct));

    private static async Task<IResult> DraftAsync(
        Guid workspaceId, OnboardingDraftRequest request, IWorkspaceQueries workspaces, ITemplateQueries templates, OnboardingDrafter drafter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Answer) || request.Answer.Trim().Length > MaxAnswer)
        {
            return Invalid("answer", "validation.text.length");
        }

        if (await FactAsync(workspaceId, request.FactKey, workspaces, templates, ct) is not { } fact)
        {
            return Error.Unprocessable("knowledge.fact_unknown").ToProblem();
        }

        return Results.Ok(await drafter.DraftAsync(workspaceId, fact, request.Answer.Trim(), ct));
    }

    private static async Task<IResult> ConfirmAsync(
        Guid workspaceId, OnboardingConfirmRequest request, KnowledgeDbContext db, IWorkspaceQueries workspaces, ITemplateQueries templates, KnowledgeScorer scorer,
        ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Trim().Length > KnowledgeSource.MaxQuestion)
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

        if (await FactAsync(workspaceId, request.FactKey, workspaces, templates, ct) is not { } fact)
        {
            return Error.Unprocessable("knowledge.fact_unknown").ToProblem();
        }

        var question = request.Question.Trim();
        var answer = request.Answer.Trim();
        var title = question.Length <= KnowledgeSource.MaxTitle ? question : question[..KnowledgeSource.MaxTitle];

        // One FAQ per fact: a template's FAQ for the same fact (switched off, with blanks) is filled in rather than duplicated.
        var source = await db.Sources.FirstOrDefaultAsync(s => s.FactKey == fact.Key && s.Kind == KnowledgeSourceKinds.Faq, ct);
        if (source is null)
        {
            source = KnowledgeSource.Create(workspaceId, KnowledgeSourceKinds.Faq, title, question, answer, null, caller.UserId, clock.UtcNow);
            source.FactKey = fact.Key;
            db.Sources.Add(source);
        }
        else
        {
            source.Title = title;
            source.Question = question;
            source.Body = answer;
            source.Enabled = true;
            source.Touch();
        }

        db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Revision), workspaceId.ToString(), workspaceId: workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new OnboardingConfirmResponse(KnowledgeEndpoints.ToResponse(source), await scorer.ScoreAsync(workspaceId, ct)));
    }

    private static async Task<RequiredFact?> FactAsync(Guid workspaceId, string? key, IWorkspaceQueries workspaces, ITemplateQueries templates, CancellationToken ct)
    {
        var industry = await workspaces.GetIndustryAsync(workspaceId, ct) ?? "other";
        return (await templates.GetRequiredFactsAsync(industry, ct)).FirstOrDefault(f => f.Key == key);
    }

    private static IResult Invalid(string field, string code) =>
        Error.Validation("validation.failed", new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [code] }).ToProblem();
}

/// <summary>
/// Writes a customer-facing FAQ from the owner's answer with the <c>knowledge.onboarding</c> prompt (task <c>summarize</c>). The answer goes in
/// as the user message, never into the system prompt. When no model answers, the fact's prompt becomes the question and the owner's words
/// the answer, so onboarding never stops.
/// </summary>
internal sealed class OnboardingDrafter(IModelRouter router, IPromptRegistry prompts, ILogger<OnboardingDrafter> logger)
{
    public const string PromptKey = "knowledge.onboarding";

    public async Task<OnboardingDraftResponse> DraftAsync(Guid workspaceId, RequiredFact fact, string answer, CancellationToken ct)
    {
        try
        {
            var system = prompts.Get(PromptKey).Render(new Dictionary<string, string>(StringComparer.Ordinal) { ["fact"] = fact.Label, ["owner_question"] = fact.Prompt });
            var response = await router.GetChatClient(AiTasks.Summarize, workspaceId).GetResponseAsync(
                [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, answer)], new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, ct);
            if (Parse(response.Text) is { } draft)
            {
                return new OnboardingDraftResponse(fact.Key, draft.Question, draft.Answer, "ai");
            }

            logger.LogWarning("The onboarding draft for fact {FactKey} was not valid json; using the owner's words", fact.Key);
        }
        catch (Exception e) when (e is AllRoutesFailedException or NoRouteException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(e, "No model drafted the onboarding FAQ for fact {FactKey}; using the owner's words", fact.Key);
        }

        return Fallback(fact, answer);
    }

    public static OnboardingDraftResponse Fallback(RequiredFact fact, string answer) =>
        new(fact.Key, Clip(fact.Prompt, KnowledgeSource.MaxQuestion), Clip(answer, KnowledgeSource.MaxAnswer), "fallback");

    /// <summary>Reads <c>{"question": "...", "answer": "..."}</c>; null when either is missing or empty.</summary>
    public static (string Question, string Answer)? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String && q.GetString() is { } question && !string.IsNullOrWhiteSpace(question)
                && doc.RootElement.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { } answer && !string.IsNullOrWhiteSpace(answer))
            {
                return (Clip(question.Trim(), KnowledgeSource.MaxQuestion), Clip(answer.Trim(), KnowledgeSource.MaxAnswer));
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
