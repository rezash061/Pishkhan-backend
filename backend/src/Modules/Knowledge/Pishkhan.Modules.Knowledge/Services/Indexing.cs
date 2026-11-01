using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pishkhan.AI;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Knowledge.Services;

/// <summary>Builds a source's chunks and embeddings in the caller's transaction.</summary>
internal sealed class KnowledgeIndexer(KnowledgeDbContext db, IModelRouter router, IClock clock, ILogger<KnowledgeIndexer> logger)
{
    private const int EmbedBatch = 32;

    public static IReadOnlyList<TextChunk> ChunksOf(KnowledgeSource source) => source.Kind switch
    {
        KnowledgeSourceKinds.Faq => [new TextChunk(Chunker.Faq(source.Question ?? source.Title, source.Body), Chunker.EstimateTokens(source.Body) + 10)],
        KnowledgeSourceKinds.Text => Chunker.Split(source.Title + ".\n" + source.Body),
        _ => Chunker.Split(source.Body),
    };

    public async Task IndexAsync(KnowledgeSource source, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var chunks = ChunksOf(source);
        var vectors = new List<ReadOnlyMemory<float>>(chunks.Count);
        string? model = null;
        try
        {
            var embedder = router.GetEmbeddingGenerator(source.WorkspaceId);
            foreach (var batch in chunks.Chunk(EmbedBatch))
            {
                var result = await embedder.GenerateAsync(batch.Select(c => PersianNormalizer.Normalize(c.Text)), cancellationToken: ct);
                vectors.AddRange(result.Select(e => e.Vector));
                model ??= result.FirstOrDefault()?.ModelId;
            }
        }
        catch (Exception ex) when (ex is AllRoutesFailedException or NoRouteException)
        {
            // Keep the previous chunks searchable; the owner can re-index once the provider answers.
            logger.LogWarning(ex, "Embedding source {SourceId} failed", source.Id);
            source.Status = KnowledgeStatuses.Failed;
            source.Error = ex is NoRouteException ? "knowledge.no_embedding_route" : "knowledge.embedding_failed";
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM knowledge.chunks WHERE source_id = {source.Id}", ct);
        for (var i = 0; i < chunks.Count; i++)
        {
            var id = Uuid.NewV7(now);
            var text = chunks[i].Text;
            var normalized = PersianNormalizer.Normalize(text);
            var vector = Literal(vectors[i].Span);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO knowledge.chunks (id, workspace_id, source_id, ordinal, text, normalized_text, token_count, embedding_model, created_at, embedding)
                VALUES ({id}, {source.WorkspaceId}, {source.Id}, {i}, {text}, {normalized}, {chunks[i].TokenCount}, {model}, {now}, CAST({vector} AS vector))
                """, ct);
        }

        source.Status = KnowledgeStatuses.Indexed;
        source.IndexedRevision = source.Revision;
        source.ChunkCount = chunks.Count;
        source.Error = null;
        source.IndexedAt = now;
    }

    /// <summary>pgvector's text form: <c>[0.1,0.2,...]</c>.</summary>
    public static string Literal(ReadOnlySpan<float> vector)
    {
        var text = new StringBuilder(vector.Length * 10).Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        return text.Append(']').ToString();
    }
}

/// <summary>AI group: (re)indexes a source when its revision is still the latest.</summary>
/// <remarks>
/// A file or URL source first gets its text: the document's (once it is clean) or the page's, fetched again on every re-index. When that
/// fails the source is <c>failed</c> with the reason and its previous chunks stay; the owner fixes it and re-indexes.
/// </remarks>
internal sealed class KnowledgeSourceChangedHandler(KnowledgeDbContext db, KnowledgeIndexer indexer, KnowledgeContentReader reader, ILogger<KnowledgeSourceChangedHandler> logger)
    : IdempotentHandler<KnowledgeSourceChanged, KnowledgeDbContext>(db)
{
    protected override async Task HandleOnceAsync(KnowledgeSourceChanged message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var source = await Db.Sources.FirstOrDefaultAsync(s => s.Id == message.SourceId, cancellationToken);
        if (source is null || source.Revision != message.Revision)
        {
            return;
        }

        if (source.Kind is KnowledgeSourceKinds.File or KnowledgeSourceKinds.Url)
        {
            try
            {
                var extracted = await reader.ReadAsync(source, cancellationToken);
                source.Body = extracted.Text;
                if (extracted.Title is { } title && source.Url is { } url && source.Title == KnowledgeSource.DefaultUrlTitle(url))
                {
                    source.Title = title.Length <= KnowledgeSource.MaxTitle ? title : title[..KnowledgeSource.MaxTitle];
                }
            }
            catch (KnowledgeExtractionException ex)
            {
                logger.LogInformation("Knowledge source {SourceId} gave no text: {Code}", source.Id, ex.Code);
                source.Status = KnowledgeStatuses.Failed;
                source.Error = ex.Code;
                return;
            }
        }

        await indexer.IndexAsync(source, cancellationToken);
    }
}

/// <summary>AI group: keeps a product's knowledge source in step with the catalog (draft and archived products are left out).</summary>
internal sealed class ProductKnowledgeHandler(KnowledgeDbContext db, ICatalogQueries catalog, KnowledgeIndexer indexer, IClock clock)
    : IdempotentHandler<ProductChanged, KnowledgeDbContext>(db)
{
    protected override async Task HandleOnceAsync(ProductChanged message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var source = await Db.Sources.FirstOrDefaultAsync(s => s.ProductId == message.ProductId, cancellationToken);
        var product = message.Change == ProductChangeKinds.Deleted ? null : await catalog.GetProductAsync(message.ProductId, cancellationToken);
        if (product is null || product.Status != ProductStatuses.Active)
        {
            if (source is not null)
            {
                await Db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM knowledge.chunks WHERE source_id = {source.Id}", cancellationToken);
                Db.Sources.Remove(source);
            }

            return;
        }

        var body = Chunker.Product(product);
        if (source is null)
        {
            source = KnowledgeSource.Create(message.WorkspaceId, KnowledgeSourceKinds.Product, Trim(product.Title), null, body, product.Id, null, clock.UtcNow);
            Db.Sources.Add(source);
        }
        else if (source.Body == body && source.Status == KnowledgeStatuses.Indexed)
        {
            return;
        }
        else
        {
            source.Title = Trim(product.Title);
            source.Body = body;
            source.Touch();
        }

        await indexer.IndexAsync(source, cancellationToken);
    }

    private static string Trim(string title) => title.Length <= KnowledgeSource.MaxTitle ? title : title[..KnowledgeSource.MaxTitle];
}

/// <summary>
/// Hybrid retrieval (backend plan 9.7): nearest chunks by cosine over embeddings, and full-text matches (simple configuration over
/// normalised text, any word), fused with Reciprocal Rank Fusion. Without an embedding (provider down) it falls back to full text.
/// </summary>
internal sealed class KnowledgeSearch(KnowledgeDbContext db, IModelRouter router, ITenantContext tenant, ILogger<KnowledgeSearch> logger) : IKnowledgeSearch
{
    private const int RrfK = 60;

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var workspaceId = tenant.WorkspaceId ?? throw new InvalidOperationException("Knowledge search needs a workspace.");
        var normalized = PersianNormalizer.Normalize(query);
        var candidates = Math.Clamp(limit * 4, 10, 50);
        var fused = new Dictionary<Guid, (double Score, double? Similarity)>();

        try
        {
            var embedding = (await router.GetEmbeddingGenerator(workspaceId).GenerateAsync([normalized], cancellationToken: cancellationToken))[0];
            var rank = 0;
            foreach (var (id, distance) in await QueryAsync(
                """
                SELECT c.id, (c.embedding <=> CAST(@q AS vector)) AS distance
                FROM knowledge.chunks c JOIN knowledge.sources s ON s.id = c.source_id
                WHERE c.workspace_id = @ws AND s.enabled AND c.embedding IS NOT NULL
                ORDER BY c.embedding <=> CAST(@q AS vector) LIMIT @k
                """,
                [new("q", KnowledgeIndexer.Literal(embedding.Vector.Span)), new("ws", workspaceId), new("k", candidates)],
                cancellationToken))
            {
                fused[id] = (1.0 / (RrfK + ++rank), 1 - distance);
            }
        }
        catch (Exception ex) when (ex is AllRoutesFailedException or NoRouteException)
        {
            logger.LogWarning("Knowledge search without embeddings for workspace {WorkspaceId}: {Error}", workspaceId, ex.Message);
        }

        if (TsQuery(normalized) is { } tsQuery)
        {
            var rank = 0;
            foreach (var (id, _) in await QueryAsync(
                """
                SELECT c.id, ts_rank(c.search, to_tsquery('simple', @q))::float8
                FROM knowledge.chunks c JOIN knowledge.sources s ON s.id = c.source_id
                WHERE c.workspace_id = @ws AND s.enabled AND c.search @@ to_tsquery('simple', @q)
                ORDER BY 2 DESC LIMIT @k
                """,
                [new("q", tsQuery), new("ws", workspaceId), new("k", candidates)],
                cancellationToken))
            {
                var score = 1.0 / (RrfK + ++rank);
                fused[id] = fused.TryGetValue(id, out var existing) ? (existing.Score + score, existing.Similarity) : (score, null);
            }
        }

        var top = fused.OrderByDescending(f => f.Value.Score).Take(limit).ToList();
        var ids = top.Select(t => t.Key).ToList();
        var rows = await db.Chunks.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Join(db.Sources.AsNoTracking(), c => c.SourceId, s => s.Id, (c, s) => new { c.Id, c.SourceId, s.Kind, s.Title, c.Text, s.ProductId })
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        return [.. top.Where(t => rows.ContainsKey(t.Key)).Select(t =>
        {
            var r = rows[t.Key];
            return new KnowledgeHit(r.Id, r.SourceId, r.Kind, r.Title, r.Text, t.Value.Score, t.Value.Similarity, r.ProductId);
        })];
    }

    /// <summary>Words of the normalised query OR-ed together; only letters and digits, so the query cannot inject tsquery syntax.</summary>
    public static string? TsQuery(string normalized)
    {
        var words = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]).ToLowerInvariant())
            .Where(w => w.Length >= 2).Distinct(StringComparer.Ordinal).Take(20).ToList();
        return words.Count == 0 ? null : string.Join(" | ", words);
    }

    private async Task<List<(Guid Id, double Value)>> QueryAsync(string sql, NpgsqlParameter[] parameters, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddRange(parameters);
            if (db.Database.CurrentTransaction?.GetDbTransaction() is NpgsqlTransaction transaction)
            {
                command.Transaction = transaction;
            }

            var result = new List<(Guid, double)>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add((reader.GetGuid(0), reader.GetDouble(1)));
            }

            return result;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }
}
