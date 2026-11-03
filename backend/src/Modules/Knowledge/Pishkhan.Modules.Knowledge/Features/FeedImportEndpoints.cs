using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Knowledge.Features;

/// <summary><c>maxPosts</c>: how many of the newest posts to read, 1 to 200 (default 50).</summary>
public sealed record FeedImportRequest(Guid ChannelAccountId, int? MaxPosts);

/// <summary><c>status</c>: running, completed or failed (<c>error</c>). <c>pending</c> counts the suggestions still waiting for review.</summary>
public sealed record FeedImportResponse(
    Guid Id, Guid ChannelAccountId, string Status, int MaxPosts, int PostsRead, int PostsAnalyzed, int PostsFailed, int ItemsFound, int Pending, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>
/// A suggestion from one post. <c>kind</c>: product (<c>title</c>, <c>description</c>, <c>price</c> in Rial, <c>tags</c>) or faq (<c>question</c>,
/// <c>answer</c>). <c>priceInCaption</c> false: the price is not written in the post, check it. <c>createdId</c>: the product or knowledge
/// source made when it was accepted.
/// </summary>
public sealed record FeedItemResponse(
    Guid Id, string Kind, string Status, string MediaId, string? Permalink, string? ImageUrl, string? Title, string? Description, Money? Price, bool PriceInCaption,
    IReadOnlyList<string> Tags, string? Question, string? Answer, Guid? CreatedId);

public sealed record FeedImportDetail(FeedImportResponse Import, IReadOnlyList<FeedItemResponse> Items);

/// <summary>The owner's corrections to a pending suggestion; fields left out are kept.</summary>
public sealed record FeedItemUpdate(string? Title, string? Description, Money? Price, IReadOnlyList<string>? Tags, string? Question, string? Answer);

/// <summary><c>activate</c> (products, default true): put a priced product on sale at once; otherwise it is saved as a catalog draft.</summary>
public sealed record FeedItemAccept(bool? Activate);

/// <summary>Accepting several at once. Each item is accepted on its own: <c>accepted</c> and <c>failed</c> say which.</summary>
public sealed record FeedItemsAccept(IReadOnlyList<Guid> ItemIds, bool? Activate);

public sealed record FeedItemsAcceptResult(IReadOnlyList<FeedItemResponse> Accepted, IReadOnlyDictionary<Guid, IReadOnlyList<string>> Failed);

/// <summary>Feed import (BE-2.5, gap G20): read past posts into suggestions the owner accepts, edits or rejects.</summary>
internal static class FeedImportEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/knowledge/feed-imports").WithTags("Knowledge");

        group.MapPost("/", StartAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Read the newest posts of an Instagram account into product and FAQ suggestions")
            .Produces<FeedImportResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("The last 20 imports, newest first")
            .Produces<IReadOnlyList<FeedImportResponse>>();

        group.MapGet("/{importId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("An import and its suggestions, pending first")
            .Produces<FeedImportDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{importId:guid}/items/{itemId:guid}", UpdateItemAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Correct a pending suggestion")
            .Produces<FeedItemResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{importId:guid}/items/{itemId:guid}/accept", AcceptItemAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Accept a suggestion: a catalog product, or an enabled FAQ")
            .Produces<FeedItemResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{importId:guid}/accept", AcceptManyAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Accept several suggestions (up to 100)")
            .Produces<FeedItemsAcceptResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{importId:guid}/items/{itemId:guid}/reject", RejectItemAsync)
            .RequireWorkspacePermission(Permissions.KnowledgeWrite)
            .WithSummary("Reject a suggestion")
            .Produces<FeedItemResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static FeedImportResponse ToResponse(FeedImport i, int pending) =>
        new(i.Id, i.ChannelAccountId, i.Status, i.MaxPostsToRead, i.PostsRead, i.PostsAnalyzed, i.PostsFailed, i.ItemsFound, pending, i.Error, i.CreatedAt, i.CompletedAt);

    internal static FeedItemResponse ToResponse(FeedImportItem x) =>
        new(x.Id, x.Kind, x.Status, x.MediaId, x.Permalink, x.ImageUrl, x.Title, x.Description, x.Price is { } p ? Money.Rial(p) : null, x.PriceInCaption, x.Tags,
            x.Question, x.Answer, x.CreatedId);

    private static async Task<IResult> StartAsync(
        Guid workspaceId, FeedImportRequest request, KnowledgeDbContext db, IChannelDirectory channels, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (request.MaxPosts is < 1 or > FeedImport.MaxPosts)
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["maxPosts"] = ["knowledge.feed_posts_range"] }).ToProblem();
        }

        if (await channels.GetAsync(request.ChannelAccountId, ct) is null)
        {
            return Error.NotFound("knowledge.channel_not_found").ToProblem();
        }

        if (await db.FeedImports.AnyAsync(i => i.Status == FeedImportStatuses.Running, ct))
        {
            return Error.Conflict("knowledge.feed_import_running").ToProblem();
        }

        var import = FeedImport.Start(workspaceId, request.ChannelAccountId, request.MaxPosts ?? FeedImport.DefaultPosts, caller.UserId, clock.UtcNow);
        db.FeedImports.Add(import);
        db.Publish(new FeedImportPageRequested(workspaceId, import.Id, null), import.Id.ToString(), workspaceId: workspaceId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("knowledge.feed_import_running").ToProblem();
        }

        return Results.Json(ToResponse(import, 0), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ListAsync(Guid workspaceId, KnowledgeDbContext db, CancellationToken ct)
    {
        var imports = await db.FeedImports.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(20).ToListAsync(ct);
        var ids = imports.Select(i => i.Id).ToList();
        var pending = await db.FeedImportItems.AsNoTracking()
            .Where(x => ids.Contains(x.ImportId) && x.Status == FeedItemStatuses.Pending)
            .GroupBy(x => x.ImportId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return Results.Ok(imports.Select(i => ToResponse(i, pending.GetValueOrDefault(i.Id))).ToList());
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid importId, KnowledgeDbContext db, CancellationToken ct)
    {
        var import = await db.FeedImports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == importId, ct);
        if (import is null)
        {
            return Error.NotFound().ToProblem();
        }

        var items = await db.FeedImportItems.AsNoTracking().Where(x => x.ImportId == importId)
            .OrderBy(x => x.Status == FeedItemStatuses.Pending ? 0 : 1).ThenBy(x => x.CreatedAt).Take(1_000).ToListAsync(ct);
        return Results.Ok(new FeedImportDetail(ToResponse(import, items.Count(x => x.Status == FeedItemStatuses.Pending)), [.. items.Select(ToResponse)]));
    }

    private static async Task<IResult> UpdateItemAsync(Guid workspaceId, Guid importId, Guid itemId, FeedItemUpdate request, KnowledgeDbContext db, IClock clock, CancellationToken ct)
    {
        var item = await db.FeedImportItems.FirstOrDefaultAsync(x => x.Id == itemId && x.ImportId == importId, ct);
        if (item is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (item.Status != FeedItemStatuses.Pending)
        {
            return Error.Conflict("knowledge.feed_item_not_pending").ToProblem();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Check(errors, "title", request.Title, FeedImportItem.MaxTitle);
        Check(errors, "description", request.Description, FeedImportItem.MaxDescription, allowEmpty: true);
        Check(errors, "question", request.Question, KnowledgeSource.MaxQuestion);
        Check(errors, "answer", request.Answer, KnowledgeSource.MaxAnswer);
        if (request.Price is { } price && (price.Amount <= 0 || price.Currency != Currency.Irr))
        {
            errors["price"] = ["validation.price.invalid"];
        }

        if (request.Tags is { Count: > FeedImportItem.MaxTags } || request.Tags?.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 50) == true)
        {
            errors["tags"] = ["validation.tags.invalid"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        item.Title = request.Title?.Trim() ?? item.Title;
        item.Description = request.Description is null ? item.Description : request.Description.Trim() is { Length: > 0 } d ? d : null;
        if (request.Price is { } newPrice)
        {
            // The owner set the price: it is theirs, not the model's.
            item.Price = newPrice.Amount;
            item.PriceInCaption = true;
        }

        item.Tags = request.Tags is { } tags ? [.. tags.Select(t => t.Trim())] : item.Tags;
        item.Question = request.Question?.Trim() ?? item.Question;
        item.Answer = request.Answer?.Trim() ?? item.Answer;
        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(item));
    }

    private static async Task<IResult> AcceptItemAsync(
        Guid workspaceId, Guid importId, Guid itemId, FeedItemAccept? request, KnowledgeDbContext db, ICatalogCommands catalog, ICurrentUser caller, IClock clock,
        CancellationToken ct)
    {
        var item = await db.FeedImportItems.FirstOrDefaultAsync(x => x.Id == itemId && x.ImportId == importId, ct);
        if (item is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (item.Status != FeedItemStatuses.Pending)
        {
            return Error.Conflict("knowledge.feed_item_not_pending").ToProblem();
        }

        var errors = await AcceptAsync(workspaceId, item, request?.Activate ?? true, db, catalog, caller, clock, ct);
        if (errors.Count > 0)
        {
            return Results.Problem(
                title: "knowledge.feed_item_invalid", detail: string.Join("; ", errors), statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "knowledge.feed_item_invalid", ["issues"] = errors });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(item));
    }

    private static async Task<IResult> AcceptManyAsync(
        Guid workspaceId, Guid importId, FeedItemsAccept request, KnowledgeDbContext db, ICatalogCommands catalog, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (request.ItemIds is not { Count: > 0 and <= 100 })
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["itemIds"] = ["validation.count"] }).ToProblem();
        }

        if (!await db.FeedImports.AnyAsync(i => i.Id == importId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        var ids = request.ItemIds.Distinct().ToList();
        var items = await db.FeedImportItems.Where(x => x.ImportId == importId && ids.Contains(x.Id)).ToListAsync(ct);
        var accepted = new List<FeedItemResponse>();
        var failed = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var id in ids)
        {
            if (items.FirstOrDefault(x => x.Id == id) is not { } item)
            {
                failed[id] = ["common.not_found"];
                continue;
            }

            if (item.Status != FeedItemStatuses.Pending)
            {
                failed[id] = ["knowledge.feed_item_not_pending"];
                continue;
            }

            // Products are saved by the Catalog module one at a time; each accepted item is saved right away so a later failure keeps it.
            var errors = await AcceptAsync(workspaceId, item, request.Activate ?? true, db, catalog, caller, clock, ct);
            if (errors.Count > 0)
            {
                failed[id] = errors;
                continue;
            }

            await db.SaveChangesAsync(ct);
            accepted.Add(ToResponse(item));
        }

        return Results.Ok(new FeedItemsAcceptResult(accepted, failed));
    }

    private static async Task<IResult> RejectItemAsync(Guid workspaceId, Guid importId, Guid itemId, KnowledgeDbContext db, IClock clock, CancellationToken ct)
    {
        var item = await db.FeedImportItems.FirstOrDefaultAsync(x => x.Id == itemId && x.ImportId == importId, ct);
        if (item is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (item.Status != FeedItemStatuses.Pending)
        {
            return Error.Conflict("knowledge.feed_item_not_pending").ToProblem();
        }

        item.Reject(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(item));
    }

    /// <summary>Creates the product (through Catalog) or the FAQ; returns error keys and leaves the item pending when it cannot.</summary>
    private static async Task<IReadOnlyList<string>> AcceptAsync(
        Guid workspaceId, FeedImportItem item, bool activate, KnowledgeDbContext db, ICatalogCommands catalog, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (item.Kind == FeedItemKinds.Product)
        {
            // A price the owner has not seen in the post is never put on sale automatically.
            var (productId, errors) = await catalog.CreateProductAsync(
                new NewProduct(item.Title ?? string.Empty, item.Description, item.Tags, item.Price, item.Permalink, activate && item.PriceInCaption), ct);
            if (productId is not { } id)
            {
                return errors;
            }

            item.Accept(id, now);
            return [];
        }

        if (string.IsNullOrWhiteSpace(item.Question) || string.IsNullOrWhiteSpace(item.Answer))
        {
            return ["answer: validation.knowledge.answer"];
        }

        var title = item.Question.Length <= KnowledgeSource.MaxTitle ? item.Question : item.Question[..KnowledgeSource.MaxTitle];
        var source = KnowledgeSource.Create(workspaceId, KnowledgeSourceKinds.Faq, title, item.Question, item.Answer, null, caller.UserId, now);
        db.Sources.Add(source);
        db.Publish(new KnowledgeSourceChanged(workspaceId, source.Id, source.Revision), workspaceId.ToString(), workspaceId: workspaceId);
        item.Accept(source.Id, now);
        return [];
    }

    private static void Check(Dictionary<string, string[]> errors, string field, string? value, int max, bool allowEmpty = false)
    {
        if (value is not null && ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Trim().Length > max))
        {
            errors[field] = ["validation.text.length"];
        }
    }
}
