using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Content.Domain;
using Pishkhan.Modules.Content.Persistence;
using Pishkhan.Modules.Content.Services;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Content.Features;

/// <summary>
/// A new entry (<c>kind</c>: page, post, help, case_study, legal, faq) or an edit of its draft. <c>body</c> is an array of blocks,
/// <c>data</c> an object of kind-specific fields (seoTitle, cover, keywords, version, consent, metrics, tags...). <c>revision</c> on an
/// edit is the draft revision the editor started from (409 <c>content.stale</c> when someone saved since).
/// </summary>
public sealed record EntryRequest(
    string? Kind, string Slug, string Title, string? Description, string? Category, string? Author, JsonElement? Body, JsonElement? Data, int? Revision);

/// <summary>
/// <c>status</c>: draft (never published), published (on the website) or archived (taken off). The fields are the draft;
/// <c>hasUnpublishedChanges</c> says whether it differs from the published copy.
/// </summary>
public sealed record EntryResponse(
    Guid Id, string Kind, string Slug, string Status, string Title, string? Description, string? Category, string? Author, JsonElement Body,
    JsonElement Data, int Revision, int? PublishedRevision, bool HasUnpublishedChanges, DateTimeOffset? FirstPublishedAt, DateTimeOffset? PublishedAt,
    DateTimeOffset? ScheduledAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record EntrySummary(
    Guid Id, string Kind, string Slug, string Status, string Title, string? Category, int Revision, bool HasUnpublishedChanges,
    DateTimeOffset? PublishedAt, DateTimeOffset? ScheduledAt, DateTimeOffset UpdatedAt);

public sealed record ListEntriesQuery(string? Kind, string? Status, string? Q, int? Page, int? PageSize);

/// <summary>Without <c>at</c> (or with a time that has passed) the draft is published now; a future <c>at</c> schedules it.</summary>
public sealed record PublishEntryRequest(DateTimeOffset? At);

public sealed record ArchiveEntryRequest(string? Reason);

/// <summary>Open <c>{site}/api/preview?token=...</c>; the site reads the draft with <c>GET /public/preview?token=</c> until <c>expiresAt</c>.</summary>
public sealed record PreviewTokenResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record CategoryRequest(string? Kind, string Slug, string Title, string? Description, int? SortOrder);

public sealed record CategoryResponse(Guid Id, string Kind, string Slug, string Title, string? Description, int SortOrder);

/// <summary><c>from</c> and <c>to</c> are site paths (<c>/old</c>); <c>to</c> may also be an https URL. Permanent is a 308, otherwise 307.</summary>
public sealed record RedirectRequest(string From, string To, bool? Permanent);

public sealed record RedirectResponse(Guid Id, string From, string To, bool Permanent, DateTimeOffset CreatedAt);

/// <summary>
/// The website CMS in the back office (admin plan 4.7): <c>/admin/v1/content</c>. ContentEditor (or SuperAdmin) writes; Support and
/// ReadOnly can read. Every change is audited; publishing refreshes the website through its revalidation webhook.
/// </summary>
internal static class ContentAdmin
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(1);
    internal const string PreviewPurpose = "content.preview";

    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.ContentEditor, StaffRoles.Support, StaffRoles.ReadOnly];

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Content");
        var read = admin.MapGroup("/content").RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var write = admin.MapGroup("/content").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.ContentEditor));

        read.MapGet("/entries", ListAsync)
            .WithSummary("CMS entries, most recently edited first")
            .WithDescription("Filter by `kind`, `status` and `q` (title or slug).")
            .Produces<PagedResult<EntrySummary>>()
            .WithStandardProblems();
        read.MapGet("/entries/{entryId:guid}", GetAsync)
            .WithSummary("One entry with its draft")
            .Produces<EntryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        write.MapPost("/entries", CreateAsync)
            .WithSummary("Create an entry (a draft)")
            .WithDescription("409 `content.slug_taken` when the kind already has the slug. Posts and help articles need an existing `category` of their kind.")
            .Produces<EntryResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
        write.MapPut("/entries/{entryId:guid}", UpdateAsync)
            .WithSummary("Save the draft (the website keeps showing the published copy)")
            .WithDescription("409 `content.stale` when `revision` is not the current one; 409 `content.slug_locked` when changing the slug of an entry that was published (add a redirect and a new entry instead).")
            .Produces<EntryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
        write.MapPost("/entries/{entryId:guid}/publish", PublishAsync)
            .WithSummary("Publish the draft now, or schedule it with `at`")
            .WithDescription("409 `content.consent_required` for a case study without `data.consent: true`; 409 `content.category_required`. The website refreshes within a minute.")
            .Produces<EntryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
        write.MapDelete("/entries/{entryId:guid}/schedule", UnscheduleAsync)
            .WithSummary("Cancel a scheduled publish")
            .Produces<EntryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        write.MapPost("/entries/{entryId:guid}/archive", ArchiveAsync)
            .WithSummary("Take an entry off the website (publish it again to restore it)")
            .Produces<EntryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();
        write.MapDelete("/entries/{entryId:guid}", DeleteAsync)
            .WithSummary("Delete an entry that was never published")
            .WithDescription("409 `content.was_published`: archive it instead.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        write.MapPost("/entries/{entryId:guid}/preview", PreviewAsync)
            .WithSummary("A preview token for the draft, valid one hour")
            .Produces<PreviewTokenResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        read.MapGet("/categories", ListCategoriesAsync)
            .WithSummary("Blog and help categories, in display order")
            .Produces<IReadOnlyList<CategoryResponse>>();
        write.MapPost("/categories", CreateCategoryAsync)
            .WithSummary("Add a blog or help category")
            .Produces<CategoryResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
        write.MapPut("/categories/{categoryId:guid}", UpdateCategoryAsync)
            .WithSummary("Rename or reorder a category (the slug is fixed)")
            .Produces<CategoryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();
        write.MapDelete("/categories/{categoryId:guid}", DeleteCategoryAsync)
            .WithSummary("Delete a category no entry uses")
            .WithDescription("409 `content.category_in_use`.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        read.MapGet("/redirects", ListRedirectsAsync)
            .WithSummary("Website redirects")
            .Produces<IReadOnlyList<RedirectResponse>>();
        write.MapPost("/redirects", CreateRedirectAsync)
            .WithSummary("Add a redirect")
            .WithDescription("409 `content.redirect_exists` when `from` already redirects.")
            .Produces<RedirectResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
        write.MapDelete("/redirects/{redirectId:guid}", DeleteRedirectAsync)
            .WithSummary("Remove a redirect")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static EntryResponse ToResponse(ContentEntry e) => new(
        e.Id, e.Kind, e.Slug, e.Status, e.Title, e.Description, e.Category, e.Author, Parse(e.Body), Parse(e.Data), e.Revision, e.PublishedRevision,
        e.HasUnpublishedChanges, e.FirstPublishedAt, e.PublishedAt, e.ScheduledAt, e.CreatedAt, e.UpdatedAt);

    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<IResult> ListAsync([AsParameters] ListEntriesQuery query, ContentDbContext db, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? 25, 1, 100);
        var entries = db.Entries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            entries = entries.Where(e => e.Kind == query.Kind);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            entries = entries.Where(e => e.Status == query.Status);
        }

        if (query.Q?.Trim() is { Length: > 0 } q)
        {
            var pattern = "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            entries = entries.Where(e => EF.Functions.ILike(e.Title, pattern) || EF.Functions.ILike(e.Slug, pattern));
        }

        var total = await entries.CountAsync(ct);
        var rows = await entries.OrderByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = rows.Select(e => new EntrySummary(
            e.Id, e.Kind, e.Slug, e.Status, e.Title, e.Category, e.Revision, e.HasUnpublishedChanges, e.PublishedAt, e.ScheduledAt, e.UpdatedAt)).ToList();
        return Results.Ok(new PagedResult<EntrySummary>(items, total, page, pageSize));
    }

    private static async Task<IResult> GetAsync(Guid entryId, ContentDbContext db, CancellationToken ct) =>
        await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entryId, ct) is { } entry ? Results.Ok(ToResponse(entry)) : Error.NotFound().ToProblem();

    /// <summary>Checks a request; returns the cleaned draft or the validation problem.</summary>
    private static async Task<(string Slug, EntryContent? Draft, IResult? Problem)> ReadAsync(string kind, EntryRequest request, ContentDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var slug = PersianNormalizer.Normalize(request.Slug ?? string.Empty).Trim();
        if (!ContentRules.IsSlug(slug))
        {
            errors["slug"] = ["validation.content.slug"];
        }

        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length is 0 or > ContentEntry.MaxTitle)
        {
            errors["title"] = [title.Length == 0 ? "validation.required" : "validation.too_long"];
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > ContentEntry.MaxDescription })
        {
            errors["description"] = ["validation.too_long"];
        }

        var author = string.IsNullOrWhiteSpace(request.Author) ? null : request.Author.Trim();
        if (author is { Length: > ContentEntry.MaxAuthor })
        {
            errors["author"] = ["validation.too_long"];
        }

        var category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        var categoryKind = ContentKinds.CategoryKind(kind);
        if (category is not null && (categoryKind is null || !await db.Categories.AnyAsync(c => c.Kind == categoryKind && c.Slug == category, ct)))
        {
            errors["category"] = ["validation.content.category"];
        }

        var (body, bodyError) = ContentRules.Body(request.Body);
        if (bodyError is not null)
        {
            errors["body"] = [bodyError];
        }

        var (data, dataError) = ContentRules.Data(request.Data);
        if (dataError is not null)
        {
            errors["data"] = [dataError];
        }

        return errors.Count > 0
            ? (slug, null, Error.Validation("validation.failed", errors).ToProblem())
            : (slug, new EntryContent(title, description, category, author, body!, data!), null);
    }

    private static async Task<IResult> CreateAsync(EntryRequest request, ContentDbContext db, ContentPublisher publisher, StaffActor staff, IClock clock, CancellationToken ct)
    {
        var kind = request.Kind ?? string.Empty;
        if (!ContentKinds.All.Contains(kind))
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["kind"] = ["validation.invalid"] }).ToProblem();
        }

        var (slug, draft, problem) = await ReadAsync(kind, request, db, ct);
        if (problem is not null)
        {
            return problem;
        }

        if (await db.Entries.AnyAsync(e => e.Kind == kind && e.Slug == slug, ct))
        {
            return Error.Conflict("content.slug_taken").ToProblem();
        }

        var entry = ContentEntry.Create(kind, slug, draft!, staff.Id, clock.UtcNow);
        db.Entries.Add(entry);
        publisher.Audit(entry, await staff.ActorAsync(ct), "content.create", null);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(entry), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        Guid entryId, EntryRequest request, ContentDbContext db, ContentPublisher publisher, StaffActor staff, CancellationToken ct)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null)
        {
            return Error.NotFound().ToProblem();
        }

        var (slug, draft, problem) = await ReadAsync(entry.Kind, request, db, ct);
        if (problem is not null)
        {
            return problem;
        }

        if (request.Revision is { } revision && revision != entry.Revision)
        {
            return Error.Conflict("content.stale").ToProblem();
        }

        if (slug != entry.Slug)
        {
            if (entry.FirstPublishedAt is not null)
            {
                return Error.Conflict("content.slug_locked").ToProblem();
            }

            if (await db.Entries.AnyAsync(e => e.Kind == entry.Kind && e.Slug == slug && e.Id != entry.Id, ct))
            {
                return Error.Conflict("content.slug_taken").ToProblem();
            }
        }

        entry.Edit(slug, draft!, staff.Id);
        publisher.Audit(entry, await staff.ActorAsync(ct), "content.update", null);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("content.stale").ToProblem();
        }

        return Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> PublishAsync(
        Guid entryId, PublishEntryRequest? request, ContentDbContext db, ContentPublisher publisher, StaffActor staff, IClock clock, CancellationToken ct)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (ContentPublisher.CannotPublish(entry) is { } code)
        {
            return Error.Conflict(code).ToProblem();
        }

        var actor = await staff.ActorAsync(ct);
        if (request?.At is { } at && at > clock.UtcNow)
        {
            entry.Schedule(at, staff.Id);
            publisher.Audit(entry, actor, "content.schedule", new { at });
        }
        else
        {
            publisher.Publish(entry, actor, staff.Id);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> UnscheduleAsync(Guid entryId, ContentDbContext db, ContentPublisher publisher, StaffActor staff, CancellationToken ct)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (entry.ScheduledAt is not null)
        {
            entry.Schedule(null, staff.Id);
            publisher.Audit(entry, await staff.ActorAsync(ct), "content.unschedule", null);
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> ArchiveAsync(
        Guid entryId, ArchiveEntryRequest? request, ContentDbContext db, ContentPublisher publisher, StaffActor staff, CancellationToken ct)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (request?.Reason is { Length: > 300 })
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["reason"] = ["validation.too_long"] }).ToProblem();
        }

        publisher.Archive(entry, await staff.ActorAsync(ct), staff.Id, request?.Reason);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> DeleteAsync(Guid entryId, ContentDbContext db, ContentPublisher publisher, StaffActor staff, CancellationToken ct)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (entry.FirstPublishedAt is not null)
        {
            return Error.Conflict("content.was_published").ToProblem();
        }

        db.Entries.Remove(entry);
        publisher.Audit(entry, await staff.ActorAsync(ct), "content.delete", null);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PreviewAsync(Guid entryId, ContentDbContext db, ISecretProtector protector, IClock clock, CancellationToken ct)
    {
        if (!await db.Entries.AnyAsync(e => e.Id == entryId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        var expires = clock.UtcNow.Add(PreviewLifetime);
        var token = protector.Protect($"{entryId:N}:{expires.ToUnixTimeSeconds()}", PreviewPurpose).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Results.Ok(new PreviewTokenResponse(token, expires));
    }

    /// <summary>The entry a preview token names, or null when it is malformed, forged or expired.</summary>
    internal static Guid? ReadPreviewToken(string? token, ISecretProtector protector, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1_000)
        {
            return null;
        }

        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            var plain = protector.Unprotect(base64, PreviewPurpose);
            var parts = plain.Split(':');
            return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var id) && long.TryParse(parts[1], out var expires)
                && DateTimeOffset.FromUnixTimeSeconds(expires) > clock.UtcNow
                ? id
                : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static CategoryResponse ToResponse(ContentCategory c) => new(c.Id, c.Kind, c.Slug, c.Title, c.Description, c.SortOrder);

    private static async Task<IResult> ListCategoriesAsync(string? kind, ContentDbContext db, CancellationToken ct)
    {
        var categories = db.Categories.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            categories = categories.Where(c => c.Kind == kind);
        }

        var rows = await categories.OrderBy(c => c.Kind).ThenBy(c => c.SortOrder).ThenBy(c => c.Title).ToListAsync(ct);
        return Results.Ok(rows.Select(ToResponse).ToList());
    }

    private static IResult? CheckCategory(CategoryRequest request, bool create)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (create && !CategoryKinds.All.Contains(request.Kind ?? string.Empty))
        {
            errors["kind"] = ["validation.invalid"];
        }

        if (create && !ContentRules.IsSlug(request.Slug?.Trim()))
        {
            errors["slug"] = ["validation.content.slug"];
        }

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > ContentCategory.MaxTitle)
        {
            errors["title"] = [string.IsNullOrWhiteSpace(request.Title) ? "validation.required" : "validation.too_long"];
        }

        if (request.Description is { Length: > ContentCategory.MaxDescription })
        {
            errors["description"] = ["validation.too_long"];
        }

        return errors.Count > 0 ? Error.Validation("validation.failed", errors).ToProblem() : null;
    }

    private static async Task<IResult> CreateCategoryAsync(CategoryRequest request, ContentDbContext db, ContentPublisher publisher, IClock clock, CancellationToken ct)
    {
        if (CheckCategory(request, create: true) is { } problem)
        {
            return problem;
        }

        var slug = request.Slug.Trim();
        if (await db.Categories.AnyAsync(c => c.Kind == request.Kind && c.Slug == slug, ct))
        {
            return Error.Conflict("content.slug_taken").ToProblem();
        }

        var category = ContentCategory.Create(request.Kind!, slug, request.Title.Trim(), request.Description?.Trim(), request.SortOrder ?? 0, clock.UtcNow);
        db.Categories.Add(category);
        publisher.SiteChanged(["content"], [request.Kind == CategoryKinds.Blog ? "/blog" : "/help"]);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(category), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateCategoryAsync(Guid categoryId, CategoryRequest request, ContentDbContext db, ContentPublisher publisher, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (CheckCategory(request, create: false) is { } problem)
        {
            return problem;
        }

        category.Title = request.Title.Trim();
        category.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        category.SortOrder = request.SortOrder ?? category.SortOrder;
        var root = category.Kind == CategoryKinds.Blog ? "/blog" : "/help";
        publisher.SiteChanged(["content"], [root, category.Kind == CategoryKinds.Blog ? $"/blog/category/{category.Slug}" : $"/help/{category.Slug}"]);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(category));
    }

    private static async Task<IResult> DeleteCategoryAsync(Guid categoryId, ContentDbContext db, ContentPublisher publisher, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null)
        {
            return Error.NotFound().ToProblem();
        }

        var kind = category.Kind == CategoryKinds.Blog ? ContentKinds.Post : ContentKinds.Help;
        if (await db.Entries.AnyAsync(e => e.Kind == kind && (e.Category == category.Slug || e.LiveCategory == category.Slug), ct))
        {
            return Error.Conflict("content.category_in_use").ToProblem();
        }

        db.Categories.Remove(category);
        publisher.SiteChanged(["content"], [category.Kind == CategoryKinds.Blog ? "/blog" : "/help"]);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static RedirectResponse ToResponse(ContentRedirect r) => new(r.Id, r.FromPath, r.ToPath, r.Permanent, r.CreatedAt);

    private static async Task<IResult> ListRedirectsAsync(ContentDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Redirects.AsNoTracking().OrderBy(r => r.FromPath).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<IResult> CreateRedirectAsync(RedirectRequest request, ContentDbContext db, ContentPublisher publisher, IClock clock, CancellationToken ct)
    {
        var from = request.From?.Trim() ?? string.Empty;
        var to = request.To?.Trim() ?? string.Empty;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!from.StartsWith('/') || from.StartsWith("//", StringComparison.Ordinal) || !ContentRules.SitePath().IsMatch(from) || from.Length > ContentRedirect.MaxPath)
        {
            errors["from"] = ["validation.content.path"];
        }

        if (!ContentRules.IsLink(to) || to.Length > ContentRedirect.MaxPath || to == from)
        {
            errors["to"] = ["validation.content.path"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (await db.Redirects.AnyAsync(r => r.FromPath == from, ct))
        {
            return Error.Conflict("content.redirect_exists").ToProblem();
        }

        var redirect = ContentRedirect.Create(from, to, request.Permanent ?? true, clock.UtcNow);
        db.Redirects.Add(redirect);
        publisher.SiteChanged(["content:redirects"], [from]);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(redirect), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteRedirectAsync(Guid redirectId, ContentDbContext db, ContentPublisher publisher, CancellationToken ct)
    {
        var redirect = await db.Redirects.FirstOrDefaultAsync(r => r.Id == redirectId, ct);
        if (redirect is null)
        {
            return Error.NotFound().ToProblem();
        }

        db.Redirects.Remove(redirect);
        publisher.SiteChanged(["content:redirects"], [redirect.FromPath]);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

/// <summary>The signed-in staff member, for audit entries.</summary>
internal sealed class StaffActor(ICurrentUser caller, IStaffDirectory staff)
{
    public Guid Id => caller.UserId;

    public async Task<AuditActor> ActorAsync(CancellationToken ct) => AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress);
}
