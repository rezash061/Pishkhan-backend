using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Knowledge.Domain;

public static class FeedImportStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";

    /// <summary>Instagram refused the read (for example the token was revoked); items found before stay for review.</summary>
    public const string Failed = "failed";
}

public static class FeedItemKinds
{
    public const string Product = "product";
    public const string Faq = "faq";
}

public static class FeedItemStatuses
{
    /// <summary>Found, waiting for the owner. Nothing the AI can see.</summary>
    public const string Pending = "pending";

    /// <summary>The owner confirmed it: a catalog product or an enabled FAQ was created.</summary>
    public const string Accepted = "accepted";

    public const string Rejected = "rejected";
}

/// <summary>
/// Reading past posts into drafts (BE-2.5, gap G20). Posts are read a page at a time; each caption goes to a model that suggests products
/// and FAQs. They wait as <see cref="FeedImportItem"/>s until the owner accepts them, so nothing reaches customers without confirmation.
/// </summary>
public sealed class FeedImport : Entity<Guid>, ITenantOwned
{
    public const int DefaultPosts = 50;
    public const int MaxPosts = 200;
    public const int PageSize = 10;

    public Guid WorkspaceId { get; init; }

    public Guid ChannelAccountId { get; init; }

    public int MaxPostsToRead { get; init; }

    public string Status { get; private set; } = FeedImportStatuses.Running;

    public int PostsRead { get; private set; }

    /// <summary>Posts with a caption the model was asked about.</summary>
    public int PostsAnalyzed { get; private set; }

    public int ItemsFound { get; private set; }

    /// <summary>Posts the model could not be asked about (no route answered); they are skipped, not retried.</summary>
    public int PostsFailed { get; private set; }

    public string? Error { get; private set; }

    public Guid? CreatedBy { get; init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static FeedImport Start(Guid workspaceId, Guid channelAccountId, int maxPosts, Guid? userId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        ChannelAccountId = channelAccountId,
        MaxPostsToRead = Math.Clamp(maxPosts, 1, MaxPosts),
        CreatedBy = userId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public int Remaining => Math.Max(0, MaxPostsToRead - PostsRead);

    public void Read(int posts, int analyzed, int failed, int items, DateTimeOffset now)
    {
        PostsRead += posts;
        PostsAnalyzed += analyzed;
        PostsFailed += failed;
        ItemsFound += items;
        UpdatedAt = now;
    }

    public void Complete(DateTimeOffset now)
    {
        Status = FeedImportStatuses.Completed;
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = FeedImportStatuses.Failed;
        Error = error.Length <= 100 ? error : error[..100];
        CompletedAt = now;
        UpdatedAt = now;
    }
}

/// <summary>
/// A product or FAQ suggested from one post. A product's <see cref="Price"/> is in Rial; <see cref="PriceInCaption"/> is false when the
/// number the model gave does not appear in the caption, so the owner checks it before accepting.
/// </summary>
public sealed class FeedImportItem : Entity<Guid>, ITenantOwned
{
    public const int MaxTitle = 200;
    public const int MaxDescription = 2_000;
    public const int MaxTags = 10;

    public Guid WorkspaceId { get; init; }

    public Guid ImportId { get; init; }

    public required string Kind { get; init; }

    public required string MediaId { get; init; }

    public string? Permalink { get; init; }

    /// <summary>The post's image (or video thumbnail) on Instagram's CDN, for the review screen; not copied.</summary>
    public string? ImageUrl { get; init; }

    public string Status { get; private set; } = FeedItemStatuses.Pending;

    public string? Title { get; set; }

    public string? Description { get; set; }

    public long? Price { get; set; }

    public bool PriceInCaption { get; set; }

    public List<string> Tags { get; set; } = [];

    public string? Question { get; set; }

    public string? Answer { get; set; }

    /// <summary>The product or knowledge source created when it was accepted.</summary>
    public Guid? CreatedId { get; private set; }

    public static FeedImportItem Suggest(FeedImport import, string kind, string mediaId, string? permalink, string? imageUrl, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = import.WorkspaceId,
        ImportId = import.Id,
        Kind = kind,
        MediaId = mediaId,
        Permalink = permalink,
        ImageUrl = imageUrl,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Accept(Guid createdId, DateTimeOffset now)
    {
        Status = FeedItemStatuses.Accepted;
        CreatedId = createdId;
        UpdatedAt = now;
    }

    public void Reject(DateTimeOffset now)
    {
        Status = FeedItemStatuses.Rejected;
        UpdatedAt = now;
    }
}
