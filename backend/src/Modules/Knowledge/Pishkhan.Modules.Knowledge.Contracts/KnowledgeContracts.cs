using Pishkhan.Contracts;

namespace Pishkhan.Modules.Knowledge.Contracts;

public static class KnowledgeSourceKinds
{
    /// <summary>A question and its answer, one chunk.</summary>
    public const string Faq = "faq";

    /// <summary>Free text (policies, about the shop), split into chunks.</summary>
    public const string Text = "text";

    /// <summary>Written from a catalog product, kept in step by <c>catalog.product-changed.v1</c>.</summary>
    public const string Product = "product";

    /// <summary>An uploaded document (PDF, DOCX or plain text, purpose <c>knowledge_document</c>), its text extracted once it is scanned clean.</summary>
    public const string File = "file";

    /// <summary>A public web page, fetched through the SSRF-safe client; re-indexing fetches it again.</summary>
    public const string Url = "url";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Faq, Text, Product, File, Url };
}

/// <summary>A source was created, changed or asked to re-index; the AI group (re)builds its chunks and embeddings.</summary>
[IntegrationEvent(Topics.KnowledgeSourceChanged)]
public sealed record KnowledgeSourceChanged(Guid WorkspaceId, Guid SourceId, int Revision) : IIntegrationEvent;

/// <summary>One retrieved chunk. <see cref="Score"/> is the fused rank score (higher is better); the ids make every answer traceable.</summary>
public sealed record KnowledgeHit(Guid ChunkId, Guid SourceId, string SourceKind, string Title, string Text, double Score, double? Similarity, Guid? ProductId);

/// <summary>Hybrid retrieval over the current workspace's enabled sources (backend plan 9.7).</summary>
public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}
