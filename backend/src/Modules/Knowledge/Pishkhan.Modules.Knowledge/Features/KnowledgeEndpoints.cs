using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Knowledge.Features;

/// <summary>
/// A FAQ needs <c>question</c> and <c>answer</c> (the title defaults to the question); a text source needs <c>title</c> and <c>body</c>.
/// A <c>file</c> source needs <c>fileId</c>: a clean upload with purpose <c>knowledge_document</c> (PDF, DOCX or plain text). A <c>url</c>
/// source needs an https <c>url</c> (the title defaults to the page's title). The text of files and pages is extracted in the background.
/// Product sources are written from the catalog and can only be enabled or disabled here.
/// </summary>
public sealed record SourceRequest(string Kind, string? Title, string? Question, string? Answer, string? Body, bool? Enabled, Guid? FileId = null, string? Url = null);

/// <summary>File and URL sources take only <c>title</c>, <c>enabled</c> and (URL) <c>url</c>; their text comes from the document or page.</summary>
public sealed record SourceUpdateRequest(string? Title, string? Question, string? Answer, string? Body, bool? Enabled, string? Url = null);

/// <summary>
/// <c>status</c>: pending (being indexed), indexed or failed (<c>error</c>); <c>indexedRevision</c> lags <c>revision</c> while pending.
/// For file and URL sources <c>body</c> is the extracted text (empty until the first extraction). Their errors:
/// <c>knowledge.file_unavailable</c>, <c>knowledge.file_type</c>, <c>knowledge.file_unreadable</c>, <c>knowledge.page_unreachable</c>,
/// <c>knowledge.page_too_large</c> (over 2 MB), <c>knowledge.page_type</c> and <c>knowledge.empty</c> (no text, for example a scanned PDF).
/// </summary>
public sealed record SourceResponse(
    Guid Id, string Kind, string Title, string? Question, string Body, Guid? ProductId, bool Enabled, string Status, int Revision, int? IndexedRevision,
    int ChunkCount, string? Error, DateTimeOffset? IndexedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? FileId, string? Url, string? FactKey = null);

public sealed record KnowledgeSearchRequest(string Query, int? Limit);

internal static class KnowledgeEndpoints
{
    public const int MaxSources = 5_000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/knowledge").WithTags("Knowledge");

        group.MapGet("/sources", ListAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Knowledge sources, newest first; filter by kind, text or status")
            .Produces<Page<SourceResponse>>();

        group.MapPost("/sources", CreateAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Add a FAQ, a text, an uploaded document or a web page; it is indexed in the background")
            .Produces<SourceResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        group.MapGet("/sources/{sourceId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .Produces<SourceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/sources/{sourceId:guid}", UpdateAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Change a source; a changed text is re-indexed")
            .Produces<SourceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        group.MapDelete("/sources/{sourceId:guid}", DeleteAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Delete a FAQ, text, document or page with its chunks (product sources follow the catalog)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/sources/{sourceId:guid}/reindex", ReindexAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Build the source's chunks and embeddings again (a web page is fetched again)")
            .Produces<SourceResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/search", SearchAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("What the AI would retrieve for a question (debug)")
            .Produces<IReadOnlyList<KnowledgeHit>>()
            .WithStandardProblems();
    }

    internal static SourceResponse ToResponse(KnowledgeSource s) => new(
        s.Id, s.Kind, s.Title, s.Question, s.Body, s.ProductId, s.Enabled, s.Status, s.Revision, s.IndexedRevision, s.ChunkCount, s.Error, s.IndexedAt,
        s.CreatedAt, s.UpdatedAt, s.FileId, s.Url, s.FactKey);

    private static async Task<IResult> ListAsync(
        Guid workspaceId, string? kind, string? q, string? status, string? cursor, int? limit, KnowledgeDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.Sources.AsNoTracking();
        if (!string.IsNullOrEmpty(kind))
        {
            query = query.Where(s => s.Kind == kind);
        }

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(s => s.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(s => EF.Functions.ILike(s.Title, pattern) || (s.Question != null && EF.Functions.ILike(s.Question, pattern)));
        }

        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(s => EF.Functions.LessThan(ValueTuple.Create(s.CreatedAt, s.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].CreatedAt, rows[take - 1].Id) : null;
        return Results.Ok(new Page<SourceResponse>([.. rows.Take(take).Select(ToResponse)], next));
    }

    private static Dictionary<string, string[]> Validate(string kind, string? title, string? question, string? body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (title is { } t && (string.IsNullOrWhiteSpace(t) || t.Trim().Length > KnowledgeSource.MaxTitle))
        {
            errors["title"] = ["validation.knowledge.title"];
        }

        if (kind == KnowledgeSourceKinds.Faq)
        {
            if (string.IsNullOrWhiteSpace(question) || question.Trim().Length > KnowledgeSource.MaxQuestion)
            {
                errors["question"] = ["validation.knowledge.question"];
            }

            if (string.IsNullOrWhiteSpace(body) || body.Trim().Length > KnowledgeSource.MaxAnswer)
            {
                errors["answer"] = ["validation.knowledge.answer"];
            }
        }
        else
        {
            if (title is null)
            {
                errors["title"] = ["validation.knowledge.title"];
            }

            if (string.IsNullOrWhiteSpace(body) || body.Trim().Length > KnowledgeSource.MaxBody)
            {
                errors["body"] = ["validation.knowledge.body"];
            }
        }

        return errors;
    }

    private static async Task<IResult> CreateAsync(
        Guid workspaceId, SourceRequest request, KnowledgeDbContext db, IFileService files, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (request.Kind is KnowledgeSourceKinds.File or KnowledgeSourceKinds.Url)
        {
            return await CreateExtractedAsync(workspaceId, request, db, files, caller, clock, ct);
        }

        if (request.Kind is not (KnowledgeSourceKinds.Faq or KnowledgeSourceKinds.Text))
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["kind"] = ["validation.knowledge.kind"] }).ToProblem();
        }

        var body = request.Kind == KnowledgeSourceKinds.Faq ? request.Answer : request.Body;
        var title = request.Kind == KnowledgeSourceKinds.Faq ? request.Title ?? request.Question?.Trim() : request.Title;
        if (Validate(request.Kind, title, request.Question, body) is { Count: > 0 } errors)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (await db.Sources.CountAsync(ct) >= MaxSources)
        {
            return Error.Conflict("knowledge.limit").ToProblem();
        }

        var source = KnowledgeSource.Create(
            workspaceId, request.Kind, Clip(title!.Trim(), KnowledgeSource.MaxTitle), request.Kind == KnowledgeSourceKinds.Faq ? request.Question!.Trim() : null, body!.Trim(), null,
            caller.UserId, clock.UtcNow);
        source.Enabled = request.Enabled ?? true;
        db.Sources.Add(source);
        db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Revision), workspaceId.ToString(), workspaceId: workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(source), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>A document or page: checked here, its text read by the AI group.</summary>
    private static async Task<IResult> CreateExtractedAsync(
        Guid workspaceId, SourceRequest request, KnowledgeDbContext db, IFileService files, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Title is { } t && (string.IsNullOrWhiteSpace(t) || t.Trim().Length > KnowledgeSource.MaxTitle))
        {
            errors["title"] = ["validation.knowledge.title"];
        }

        string? url = null;
        if (request.Kind == KnowledgeSourceKinds.File)
        {
            if (request.FileId is not { } fileId || await files.GetCleanAsync(fileId, ct) is not { Purpose: FilePurposes.KnowledgeDocument })
            {
                errors["fileId"] = ["validation.knowledge.file"];
            }
        }
        else if (ValidUrl(request.Url) is { } valid)
        {
            url = valid;
        }
        else
        {
            errors["url"] = ["validation.knowledge.url"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (await db.Sources.CountAsync(ct) >= MaxSources)
        {
            return Error.Conflict("knowledge.limit").ToProblem();
        }

        var title = request.Title?.Trim() ?? (url is null ? "سند" : KnowledgeSource.DefaultUrlTitle(url));
        var source = KnowledgeSource.Create(
            workspaceId, request.Kind, Clip(title, KnowledgeSource.MaxTitle), null, string.Empty, null, caller.UserId, clock.UtcNow, request.FileId, url);
        source.Enabled = request.Enabled ?? true;
        db.Sources.Add(source);
        db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Revision), workspaceId.ToString(), workspaceId: workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(source), statusCode: StatusCodes.Status201Created);
    }

    /// <summary>An absolute https URL that the SSRF guard accepts (no IP literals or private hosts), normalised; null otherwise.</summary>
    private static string? ValidUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > KnowledgeSource.MaxUrl || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            SafeHttpClient.EnsureAllowed(uri, null);
            return uri.AbsoluteUri;
        }
        catch (SafeHttpException)
        {
            return null;
        }
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid sourceId, KnowledgeDbContext db, CancellationToken ct) =>
        await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct) is { } source ? Results.Ok(ToResponse(source)) : Error.NotFound().ToProblem();

    private static async Task<IResult> UpdateAsync(Guid workspaceId, Guid sourceId, SourceUpdateRequest request, KnowledgeDbContext db, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (source.Kind == KnowledgeSourceKinds.Product)
        {
            if (request.Title is not null || request.Question is not null || request.Answer is not null || request.Body is not null || request.Url is not null)
            {
                return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["body"] = ["validation.knowledge.product_managed"] }).ToProblem();
            }
        }
        else if (source.Kind is KnowledgeSourceKinds.File or KnowledgeSourceKinds.Url)
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (request.Question is not null || request.Answer is not null || request.Body is not null || (request.Url is not null && source.Kind == KnowledgeSourceKinds.File))
            {
                errors["body"] = ["validation.knowledge.extracted"];
            }

            if (request.Title is { } t && (string.IsNullOrWhiteSpace(t) || t.Trim().Length > KnowledgeSource.MaxTitle))
            {
                errors["title"] = ["validation.knowledge.title"];
            }

            var url = request.Url is null || source.Kind == KnowledgeSourceKinds.File ? source.Url : ValidUrl(request.Url);
            if (url is null && source.Kind == KnowledgeSourceKinds.Url)
            {
                errors["url"] = ["validation.knowledge.url"];
            }

            if (errors.Count > 0)
            {
                return Error.Validation("validation.failed", errors).ToProblem();
            }

            if (request.Title is { } title)
            {
                source.Title = Clip(title.Trim(), KnowledgeSource.MaxTitle);
            }

            if (url != source.Url)
            {
                source.Url = url;
                db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Touch()), workspaceId.ToString(), workspaceId: workspaceId);
            }
        }
        else
        {
            var title = request.Title ?? source.Title;
            var question = source.Kind == KnowledgeSourceKinds.Faq ? request.Question ?? source.Question : null;
            var body = (source.Kind == KnowledgeSourceKinds.Faq ? request.Answer : request.Body) ?? source.Body;
            if (Validate(source.Kind, title, question, body) is { Count: > 0 } errors)
            {
                return Error.Validation("validation.failed", errors).ToProblem();
            }

            var changed = title.Trim() != source.Title || question?.Trim() != source.Question || body.Trim() != source.Body;
            source.Title = Clip(title.Trim(), KnowledgeSource.MaxTitle);
            source.Question = question?.Trim();
            source.Body = body.Trim();
            if (changed)
            {
                db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Touch()), workspaceId.ToString(), workspaceId: workspaceId);
            }
        }

        if (request.Enabled is { } enabled)
        {
            source.Enabled = enabled;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(source));
    }

    private static async Task<IResult> DeleteAsync(Guid workspaceId, Guid sourceId, KnowledgeDbContext db, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (source.Kind == KnowledgeSourceKinds.Product)
        {
            return Error.Conflict("knowledge.product_managed").ToProblem();
        }

        await db.Chunks.Where(c => c.SourceId == sourceId).ExecuteDeleteAsync(ct);
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ReindexAsync(Guid workspaceId, Guid sourceId, KnowledgeDbContext db, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null)
        {
            return Error.NotFound().ToProblem();
        }

        db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Touch()), workspaceId.ToString(), workspaceId: workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(source), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> SearchAsync(Guid workspaceId, KnowledgeSearchRequest request, IKnowledgeSearch search, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 500)
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["query"] = ["validation.knowledge.query"] }).ToProblem();
        }

        return Results.Ok(await search.SearchAsync(request.Query, Math.Clamp(request.Limit ?? 5, 1, 20), ct));
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
