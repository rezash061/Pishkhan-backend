using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Knowledge.Domain;

public static class KnowledgeStatuses
{
    /// <summary>Waiting for (re)indexing.</summary>
    public const string Pending = "pending";
    public const string Indexed = "indexed";

    /// <summary>The last indexing failed (<see cref="KnowledgeSource.Error"/>); the previous chunks, if any, stay searchable.</summary>
    public const string Failed = "failed";
}

/// <summary>What the AI may answer from. Every change bumps <see cref="Revision"/>; indexing records the revision it built.</summary>
public sealed class KnowledgeSource : Entity<Guid>, ITenantOwned
{
    public const int MaxTitle = 200;
    public const int MaxQuestion = 300;
    public const int MaxAnswer = 2_000;
    public const int MaxBody = 50_000;
    public const int MaxUrl = 2_000;

    public Guid WorkspaceId { get; init; }

    /// <summary><see cref="KnowledgeSourceKinds"/>.</summary>
    public string Kind { get; init; } = KnowledgeSourceKinds.Faq;

    public string Title { get; set; } = string.Empty;

    /// <summary>The question of a FAQ entry.</summary>
    public string? Question { get; set; }

    /// <summary>A FAQ's answer, or the text of a text or product source; for a file or URL source, the text extracted from it.</summary>
    public string Body { get; set; } = string.Empty;

    public Guid? ProductId { get; init; }

    /// <summary>The uploaded document of a <c>file</c> source.</summary>
    public Guid? FileId { get; init; }

    /// <summary>The page of a <c>url</c> source.</summary>
    public string? Url { get; set; }

    /// <summary>A disabled source is not retrieved; its chunks stay.</summary>
    public bool Enabled { get; set; } = true;

    public string Status { get; set; } = KnowledgeStatuses.Pending;

    public int Revision { get; set; } = 1;

    public int? IndexedRevision { get; set; }

    public int ChunkCount { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? IndexedAt { get; set; }

    public Guid? CreatedBy { get; init; }

    public static KnowledgeSource Create(
        Guid workspaceId, string kind, string title, string? question, string body, Guid? productId, Guid? createdBy, DateTimeOffset now, Guid? fileId = null, string? url = null) => new()
    {
        FileId = fileId,
        Url = url,
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Kind = kind,
        Title = title,
        Question = question,
        Body = body,
        ProductId = productId,
        CreatedBy = createdBy,
        CreatedAt = now,
    };

    /// <summary>The title of a URL source added without one, until the page's own title replaces it.</summary>
    public static string DefaultUrlTitle(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>Marks the content changed; returns the new revision to index.</summary>
    public int Touch()
    {
        Revision++;
        Status = KnowledgeStatuses.Pending;
        return Revision;
    }
}

/// <summary>
/// A retrievable piece of a source. Besides these columns the table has <c>embedding vector(1024)</c> and a generated <c>search tsvector</c>
/// (simple configuration over <see cref="NormalizedText"/>), both written and read with SQL.
/// </summary>
public sealed class KnowledgeChunk : ITenantOwned
{
    public Guid Id { get; init; }

    public Guid WorkspaceId { get; init; }

    public Guid SourceId { get; init; }

    public int Ordinal { get; init; }

    public string Text { get; init; } = string.Empty;

    public string NormalizedText { get; init; } = string.Empty;

    public int TokenCount { get; init; }

    /// <summary>The embedding model, so a model change can find what to re-embed.</summary>
    public string? EmbeddingModel { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class UnansweredStatuses
{
    public const string Open = "open";

    /// <summary>The owner answered it; the answer became a FAQ source (<see cref="UnansweredQuestion.AnswerSourceId"/>).</summary>
    public const string Answered = "answered";

    public const string Dismissed = "dismissed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Open, Answered, Dismissed };
}

/// <summary>
/// A customer question the AI could not answer from the knowledge. Repeats of the same normalised question add to <see cref="Count"/>;
/// a question asked again after it was answered or dismissed is open again.
/// </summary>
public sealed class UnansweredQuestion : Entity<Guid>, ITenantOwned
{
    public const int MaxQuestion = 500;

    public Guid WorkspaceId { get; init; }

    /// <summary>The latest wording, as the customer wrote it.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Normalised (<c>PersianNormalizer</c>, lower case, no punctuation): the identity of the question in its workspace.</summary>
    public string Key { get; init; } = string.Empty;

    public int Count { get; set; } = 1;

    public string Status { get; set; } = UnansweredStatuses.Open;

    public DateTimeOffset FirstAskedAt { get; init; }

    public DateTimeOffset LastAskedAt { get; set; }

    public Guid LastConversationId { get; set; }

    public Guid? AnswerSourceId { get; set; }

    public static UnansweredQuestion Create(Guid workspaceId, string question, string key, Guid conversationId, DateTimeOffset askedAt) => new()
    {
        Id = Uuid.NewV7(askedAt),
        WorkspaceId = workspaceId,
        Question = question,
        Key = key,
        FirstAskedAt = askedAt,
        LastAskedAt = askedAt,
        LastConversationId = conversationId,
        CreatedAt = askedAt,
    };
}

/// <summary>A FAQ key of an industry template the workspace has received (BE-2.9), kept after the FAQ is deleted so it is never added twice.</summary>
public sealed class TemplateItem : ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public required string Key { get; init; }

    public Guid SourceId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
