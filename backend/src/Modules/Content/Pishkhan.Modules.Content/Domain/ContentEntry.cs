using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Content.Domain;

/// <summary>What a CMS entry is; each kind has its own pages on the website.</summary>
public static class ContentKinds
{
    /// <summary>A marketing page by slug (<c>home</c>, <c>about</c>, ...), made of blocks.</summary>
    public const string Page = "page";

    /// <summary>A blog post (<c>/blog/{slug}</c>), in one blog category.</summary>
    public const string Post = "post";

    /// <summary>A help center article (<c>/help/{category}/{slug}</c>), in one help category.</summary>
    public const string Help = "help";

    /// <summary>A case study (<c>/case-studies/{slug}</c>). Published only with the seller's recorded consent.</summary>
    public const string CaseStudy = "case_study";

    /// <summary>A legal page (<c>/{slug}</c>: terms, privacy, ...), with a version.</summary>
    public const string Legal = "legal";

    /// <summary>A question and answer: <c>title</c> is the question, <c>description</c> the answer; <c>data.tags</c> places it (for example <c>landing</c>).</summary>
    public const string Faq = "faq";

    public static readonly IReadOnlyList<string> All = [Page, Post, Help, CaseStudy, Legal, Faq];

    /// <summary>Kinds that belong to a category, and the category kind they use.</summary>
    public static string? CategoryKind(string kind) => kind switch
    {
        Post => CategoryKinds.Blog,
        Help => CategoryKinds.Help,
        _ => null,
    };
}

public static class CategoryKinds
{
    public const string Blog = "blog";
    public const string Help = "help";

    public static readonly IReadOnlyList<string> All = [Blog, Help];
}

/// <summary>
/// Draft: never published. Published: on the website (the draft may hold newer, unpublished edits). Archived: taken off the website.
/// </summary>
public static class ContentStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";

    public static readonly IReadOnlyList<string> All = [Draft, Published, Archived];
}

/// <summary>The editable part of an entry. <see cref="Body"/> is a JSON array of blocks; <see cref="Data"/> a JSON object of kind-specific fields.</summary>
public sealed record EntryContent(string Title, string? Description, string? Category, string? Author, string Body, string Data);

/// <summary>
/// One CMS entry (backend plan 9.16). Editors change the draft; publishing copies the draft to the published copy the website reads, so a
/// live page never shows half-done edits. <see cref="ScheduledAt"/> publishes the draft later (the platform job checks every minute).
/// </summary>
public sealed class ContentEntry : Entity<Guid>
{
    public const int MaxSlug = 120;
    public const int MaxTitle = 200;
    public const int MaxDescription = 2_000;
    public const int MaxAuthor = 100;

    private ContentEntry()
    {
    }

    public string Kind { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public string Status { get; private set; } = ContentStatuses.Draft;

    // Draft.
    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? Category { get; private set; }

    public string? Author { get; private set; }

    public string Body { get; private set; } = "[]";

    public string Data { get; private set; } = "{}";

    /// <summary>Counts draft saves; <see cref="PublishedRevision"/> is the one on the website.</summary>
    public int Revision { get; private set; }

    // Published copy (null until the first publish).
    public EntryContent? Published { get; private set; }

    public int? PublishedRevision { get; private set; }

    /// <summary>The published copy's category, as a column for the website's lists.</summary>
    public string? LiveCategory { get; private set; }

    /// <summary>Normalised plain text of the published copy, for help search.</summary>
    public string? SearchText { get; private set; }

    public DateTimeOffset? FirstPublishedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? ScheduledAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public bool HasUnpublishedChanges => PublishedRevision != Revision;

    public EntryContent Draft => new(Title, Description, Category, Author, Body, Data);

    public static ContentEntry Create(string kind, string slug, EntryContent draft, Guid staffId, DateTimeOffset now)
    {
        var entry = new ContentEntry { Id = Uuid.NewV7(now), Kind = kind, Slug = slug, CreatedBy = staffId, CreatedAt = now };
        entry.Edit(slug, draft, staffId);
        return entry;
    }

    public void Edit(string slug, EntryContent draft, Guid staffId)
    {
        Slug = slug;
        (Title, Description, Category, Author, Body, Data) = (draft.Title, draft.Description, draft.Category, draft.Author, draft.Body, draft.Data);
        Revision++;
        UpdatedBy = staffId;
    }

    public void Schedule(DateTimeOffset? at, Guid staffId)
    {
        ScheduledAt = at;
        UpdatedBy = staffId;
    }

    public void Publish(string searchText, Guid staffId, DateTimeOffset now)
    {
        Published = Draft;
        PublishedRevision = Revision;
        LiveCategory = Category;
        SearchText = searchText;
        Status = ContentStatuses.Published;
        FirstPublishedAt ??= now;
        PublishedAt = now;
        ScheduledAt = null;
        UpdatedBy = staffId;
    }

    /// <summary>Takes it off the website. The published copy is kept, so publishing again restores it with the current draft.</summary>
    public void Archive(Guid staffId)
    {
        Status = ContentStatuses.Archived;
        ScheduledAt = null;
        UpdatedBy = staffId;
    }
}

/// <summary>A blog or help category, in display order.</summary>
public sealed class ContentCategory : Entity<Guid>
{
    public const int MaxTitle = 100;
    public const int MaxDescription = 500;

    private ContentCategory()
    {
    }

    public string Kind { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public static ContentCategory Create(string kind, string slug, string title, string? description, int sortOrder, DateTimeOffset now) =>
        new() { Id = Uuid.NewV7(now), Kind = kind, Slug = slug, Title = title, Description = description, SortOrder = sortOrder, CreatedAt = now };
}

/// <summary>A website redirect (the site loads the table into its redirects).</summary>
public sealed class ContentRedirect : Entity<Guid>
{
    public const int MaxPath = 300;

    private ContentRedirect()
    {
    }

    public string FromPath { get; private set; } = string.Empty;

    public string ToPath { get; private set; } = string.Empty;

    public bool Permanent { get; private set; }

    public static ContentRedirect Create(string from, string to, bool permanent, DateTimeOffset now) =>
        new() { Id = Uuid.NewV7(now), FromPath = from, ToPath = to, Permanent = permanent, CreatedAt = now };
}
