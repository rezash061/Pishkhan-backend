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

    public Guid WorkspaceId { get; init; }

    /// <summary><see cref="KnowledgeSourceKinds"/>.</summary>
    public string Kind { get; init; } = KnowledgeSourceKinds.Faq;

    public string Title { get; set; } = string.Empty;

    /// <summary>The question of a FAQ entry.</summary>
    public string? Question { get; set; }

    /// <summary>A FAQ's answer, or the text of a text or product source.</summary>
    public string Body { get; set; } = string.Empty;

    public Guid? ProductId { get; init; }

    /// <summary>A disabled source is not retrieved; its chunks stay.</summary>
    public bool Enabled { get; set; } = true;

    public string Status { get; set; } = KnowledgeStatuses.Pending;

    public int Revision { get; set; } = 1;

    public int? IndexedRevision { get; set; }

    public int ChunkCount { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? IndexedAt { get; set; }

    public Guid? CreatedBy { get; init; }

    public static KnowledgeSource Create(Guid workspaceId, string kind, string title, string? question, string body, Guid? productId, Guid? createdBy, DateTimeOffset now) => new()
    {
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
