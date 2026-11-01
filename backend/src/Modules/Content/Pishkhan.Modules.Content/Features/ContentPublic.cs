using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Content.Domain;
using Pishkhan.Modules.Content.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Content.Features;

/// <summary>
/// A published entry as the website renders it. <c>body</c> is the block array (website <c>content/types.ts</c>), <c>data</c> the
/// kind-specific fields. <c>publishedAt</c> is the first publish, <c>updatedAt</c> the latest.
/// </summary>
public sealed record PublicEntry(
    string Kind, string Slug, string Title, string? Description, string? Category, string? Author, JsonElement Body, JsonElement Data,
    DateTimeOffset PublishedAt, DateTimeOffset UpdatedAt);

/// <summary>An entry in a list: no body.</summary>
public sealed record PublicEntrySummary(
    string Kind, string Slug, string Title, string? Description, string? Category, string? Author, JsonElement Data, DateTimeOffset PublishedAt, DateTimeOffset UpdatedAt);

public sealed record PublicCategory(string Kind, string Slug, string Title, string? Description);

public sealed record PublicRedirect(string From, string To, bool Permanent);

/// <summary>
/// The website's CMS reads (backend plan 9.16): published entries only, cached under the <c>content</c> tag (dropped on every publish),
/// plus the draft preview for a valid token.
/// </summary>
internal static class ContentPublic
{
    public const int MaxSearch = 100;
    public const int SearchResults = 20;

    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(1) };
    private static readonly string[] Tags = [CacheTags.Content];

    public static void Map(IEndpointRouteBuilder app)
    {
        var site = app.MapPublicApi().WithTags("Website");

        site.MapGet("/pages/{slug}", (string slug, ContentDbContext db, HybridCache cache, CancellationToken ct) => OneAsync(ContentKinds.Page, slug, db, cache, ct))
            .WithSummary("A published page (home, about, ...)").Produces<PublicEntry>().ProducesProblem(StatusCodes.Status404NotFound);
        site.MapGet("/legal/{slug}", (string slug, ContentDbContext db, HybridCache cache, CancellationToken ct) => OneAsync(ContentKinds.Legal, slug, db, cache, ct))
            .WithSummary("A published legal page (terms, privacy, ...); `data.version` is its version").Produces<PublicEntry>().ProducesProblem(StatusCodes.Status404NotFound);
        site.MapGet("/posts", PostsAsync)
            .WithSummary("Published blog posts, newest first, without bodies")
            .WithDescription("Filter by `category`. Paged with `cursor` (from `next`), `limit` up to 50.")
            .Produces<Page<PublicEntrySummary>>();
        site.MapGet("/posts/{slug}", (string slug, ContentDbContext db, HybridCache cache, CancellationToken ct) => OneAsync(ContentKinds.Post, slug, db, cache, ct))
            .WithSummary("A published blog post").Produces<PublicEntry>().ProducesProblem(StatusCodes.Status404NotFound);
        site.MapGet("/help", (string? category, ContentDbContext db, HybridCache cache, CancellationToken ct) => ListAsync(ContentKinds.Help, category, db, cache, ct))
            .WithSummary("Published help articles, without bodies; filter by `category`").Produces<IReadOnlyList<PublicEntrySummary>>();
        site.MapGet("/help/search", SearchAsync)
            .WithSummary("Search published help articles (title, description, body and `data.keywords`)")
            .WithDescription("Every word of `q` must match; Persian is normalised (Arabic ي/ك, digits, ZWNJ). Title matches first, at most 20.")
            .Produces<IReadOnlyList<PublicEntrySummary>>();
        site.MapGet("/help/{slug}", (string slug, ContentDbContext db, HybridCache cache, CancellationToken ct) => OneAsync(ContentKinds.Help, slug, db, cache, ct))
            .WithSummary("A published help article").Produces<PublicEntry>().ProducesProblem(StatusCodes.Status404NotFound);
        site.MapGet("/case-studies", (ContentDbContext db, HybridCache cache, CancellationToken ct) => ListAsync(ContentKinds.CaseStudy, null, db, cache, ct))
            .WithSummary("Published case studies, newest first, without bodies").Produces<IReadOnlyList<PublicEntrySummary>>();
        site.MapGet("/case-studies/{slug}", (string slug, ContentDbContext db, HybridCache cache, CancellationToken ct) => OneAsync(ContentKinds.CaseStudy, slug, db, cache, ct))
            .WithSummary("A published case study").Produces<PublicEntry>().ProducesProblem(StatusCodes.Status404NotFound);
        site.MapGet("/faqs", FaqsAsync)
            .WithSummary("Published FAQs (title is the question, description the answer); filter by `tag` in `data.tags`")
            .Produces<IReadOnlyList<PublicEntrySummary>>();
        site.MapGet("/categories", CategoriesAsync)
            .WithSummary("Blog or help categories (`kind`: blog, help), in display order").Produces<IReadOnlyList<PublicCategory>>();
        site.MapGet("/redirects", RedirectsAsync)
            .WithSummary("Website redirects, for the site's redirects() at build and its fallback").Produces<IReadOnlyList<PublicRedirect>>();
        site.MapGet("/preview", PreviewAsync)
            .WithSummary("An entry's current draft, for the site's preview mode (token from the back office)")
            .WithDescription("Not cached. 404 when the token is invalid or expired (tokens last one hour).")
            .Produces<PublicEntry>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static IQueryable<ContentEntry> Live(ContentDbContext db, string kind) =>
        db.Entries.AsNoTracking().Where(e => e.Kind == kind && e.Status == ContentStatuses.Published);

    private static PublicEntry Full(ContentEntry e, EntryContent c) => new(
        e.Kind, e.Slug, c.Title, c.Description, c.Category, c.Author, ContentAdmin.Parse(c.Body), ContentAdmin.Parse(c.Data),
        e.FirstPublishedAt ?? e.CreatedAt, e.PublishedAt ?? e.UpdatedAt);

    private static PublicEntrySummary Summary(ContentEntry e)
    {
        var c = e.Published!;
        return new(e.Kind, e.Slug, c.Title, c.Description, c.Category, c.Author, ContentAdmin.Parse(c.Data), e.FirstPublishedAt!.Value, e.PublishedAt!.Value);
    }

    private static async Task<IResult> OneAsync(string kind, string slug, ContentDbContext db, HybridCache cache, CancellationToken ct)
    {
        if (slug.Length > ContentEntry.MaxSlug)
        {
            return Error.NotFound().ToProblem();
        }

        slug = PersianNormalizer.Normalize(slug);
        var entry = await cache.GetOrCreateAsync(
            $"content:{kind}:{slug}",
            async token => await Live(db, kind).FirstOrDefaultAsync(e => e.Slug == slug, token) is { } e ? Full(e, e.Published!) : null,
            Entry, Tags, ct);
        return entry is null ? Error.NotFound().ToProblem() : Results.Ok(entry);
    }

    private static async Task<IResult> ListAsync(string kind, string? category, ContentDbContext db, HybridCache cache, CancellationToken ct)
    {
        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var list = await cache.GetOrCreateAsync(
            $"content:list:{kind}:{category}",
            async token =>
            {
                var query = Live(db, kind);
                if (category is not null)
                {
                    query = query.Where(e => e.LiveCategory == category);
                }

                var rows = await query.OrderByDescending(e => e.FirstPublishedAt).ThenByDescending(e => e.Id).Take(500).ToListAsync(token);
                return rows.Select(Summary).ToList();
            },
            Entry, Tags, ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> PostsAsync(string? category, string? cursor, int? limit, ContentDbContext db, HybridCache cache, CancellationToken ct)
    {
        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var take = Math.Clamp(limit ?? 12, 1, 50);
        if (cursor is { Length: > 200 })
        {
            cursor = null;
        }

        var page = await cache.GetOrCreateAsync(
            $"content:posts:{category}:{cursor}:{take}",
            async token =>
            {
                var query = Live(db, ContentKinds.Post);
                if (category is not null)
                {
                    query = query.Where(e => e.LiveCategory == category);
                }

                if (Cursor.TryRead(cursor, out var at, out var id))
                {
                    query = query.Where(e => EF.Functions.LessThan(ValueTuple.Create(e.FirstPublishedAt!.Value, e.Id), ValueTuple.Create(at, id)));
                }

                var rows = await query.OrderByDescending(e => e.FirstPublishedAt).ThenByDescending(e => e.Id).Take(take + 1).ToListAsync(token);
                var next = rows.Count > take ? Cursor.Write(rows[take - 1].FirstPublishedAt!.Value, rows[take - 1].Id) : null;
                return new Page<PublicEntrySummary>([.. rows.Take(take).Select(Summary)], next);
            },
            Entry, Tags, ct);
        return Results.Ok(page);
    }

    private static async Task<IResult> SearchAsync(string? q, ContentDbContext db, CancellationToken ct)
    {
        var words = PersianNormalizer.Normalize(q ?? string.Empty).ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Take(8)
            .ToList();
        if (words.Count == 0 || q!.Length > MaxSearch)
        {
            return Results.Ok(Array.Empty<PublicEntrySummary>());
        }

        var query = Live(db, ContentKinds.Help);
        foreach (var word in words)
        {
            query = query.Where(e => e.SearchText!.Contains(word));
        }

        var rows = await query.OrderByDescending(e => e.PublishedAt).Take(100).ToListAsync(ct);
        var ranked = rows
            .OrderByDescending(e => words.Count(w => PersianNormalizer.Normalize(e.Published!.Title).ToLowerInvariant().Contains(w, StringComparison.Ordinal)))
            .ThenByDescending(e => e.PublishedAt)
            .Take(SearchResults)
            .Select(Summary)
            .ToList();
        return Results.Ok(ranked);
    }

    private static async Task<IResult> FaqsAsync(string? tag, ContentDbContext db, HybridCache cache, CancellationToken ct)
    {
        var all = await cache.GetOrCreateAsync(
            "content:faqs",
            async token => (await Live(db, ContentKinds.Faq).OrderBy(e => e.FirstPublishedAt).ThenBy(e => e.Id).Take(500).ToListAsync(token)).Select(Summary).ToList(),
            Entry, Tags, ct);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Results.Ok(all);
        }

        return Results.Ok(all.Where(f => f.Data.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            && tags.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == tag)).ToList());
    }

    private static async Task<IResult> CategoriesAsync(string? kind, ContentDbContext db, HybridCache cache, CancellationToken ct)
    {
        if (!CategoryKinds.All.Contains(kind ?? string.Empty))
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["kind"] = ["validation.invalid"] }).ToProblem();
        }

        var list = await cache.GetOrCreateAsync(
            $"content:categories:{kind}",
            async token => (await db.Categories.AsNoTracking().Where(c => c.Kind == kind).OrderBy(c => c.SortOrder).ThenBy(c => c.Title).ToListAsync(token))
                .Select(c => new PublicCategory(c.Kind, c.Slug, c.Title, c.Description)).ToList(),
            Entry, Tags, ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> RedirectsAsync(ContentDbContext db, HybridCache cache, CancellationToken ct) =>
        Results.Ok(await cache.GetOrCreateAsync(
            "content:redirects",
            async token => (await db.Redirects.AsNoTracking().OrderBy(r => r.FromPath).ToListAsync(token)).Select(r => new PublicRedirect(r.FromPath, r.ToPath, r.Permanent)).ToList(),
            Entry, Tags, ct));

    private static async Task<IResult> PreviewAsync(string? token, ContentDbContext db, ISecretProtector protector, IClock clock, CancellationToken ct)
    {
        if (ContentAdmin.ReadPreviewToken(token, protector, clock) is not { } id || await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is not { } entry)
        {
            return Error.NotFound().ToProblem();
        }

        return Results.Ok(Full(entry, entry.Draft));
    }
}
