using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pishkhan.AI;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Knowledge.Services;

/// <summary>What the model suggested for one caption, already cleaned and checked.</summary>
internal sealed record FeedSuggestion(string Kind, string? Title, string? Description, long? Price, bool PriceInCaption, IReadOnlyList<string> Tags, string? Question, string? Answer);

/// <summary>
/// Turns a post caption into product and FAQ suggestions with the <c>knowledge.feed_extract</c> prompt (task <c>summarize</c>). The caption is
/// the user message, never part of the system prompt. Prices come back in Toman and are kept in Rial; a price whose number is not in the
/// caption is marked, so a model never invents a price that goes live unchecked.
/// </summary>
internal sealed partial class FeedExtractor(IModelRouter router, IPromptRegistry prompts)
{
    public const string PromptKey = "knowledge.feed_extract";
    public const int MaxProductsPerPost = 5;
    public const int MaxFaqsPerPost = 5;

    [GeneratedRegex(@"\d+(?:[.,٬]\d{3})*", RegexOptions.CultureInvariant)]
    private static partial Regex Numbers();

    /// <exception cref="AllRoutesFailedException">No model answered.</exception>
    public async Task<IReadOnlyList<FeedSuggestion>> ExtractAsync(string caption, Guid workspaceId, CancellationToken ct)
    {
        var prompt = prompts.Get(PromptKey);
        var response = await router.GetChatClient(AiTasks.Summarize, workspaceId).GetResponseAsync(
            [new ChatMessage(ChatRole.System, prompt.Text), new ChatMessage(ChatRole.User, caption)], new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, ct);
        return Parse(response.Text, caption);
    }

    /// <summary>Reads <c>{"products":[{title,description,price,tags}],"faqs":[{question,answer}]}</c>; anything malformed is dropped.</summary>
    public static IReadOnlyList<FeedSuggestion> Parse(string? json, string caption)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var numbers = CaptionNumbers(caption);
        var result = new List<FeedSuggestion>();
        if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in products.EnumerateArray().Take(MaxProductsPerPost))
            {
                if (Text(p, "title", FeedImportItem.MaxTitle) is not { } title)
                {
                    continue;
                }

                var toman = Toman(p);
                var rial = toman is > 0 and < 100_000_000_000 ? toman * 10 : null;
                var tags = p.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim().TrimStart('#'))
                        .Where(x => x.Length is > 0 and <= 50).Distinct(StringComparer.Ordinal).Take(FeedImportItem.MaxTags).ToList()
                    : [];
                result.Add(new FeedSuggestion(
                    FeedItemKinds.Product, title, Text(p, "description", FeedImportItem.MaxDescription), rial, toman is { } v && InCaption(v, numbers), tags, null, null));
            }
        }

        if (root.TryGetProperty("faqs", out var faqs) && faqs.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in faqs.EnumerateArray().Take(MaxFaqsPerPost))
            {
                if (Text(f, "question", KnowledgeSource.MaxQuestion) is { } question && Text(f, "answer", KnowledgeSource.MaxAnswer) is { } answer)
                {
                    result.Add(new FeedSuggestion(FeedItemKinds.Faq, null, null, null, false, [], question, answer));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The numbers written in the caption, Persian digits and thousands separators included. Shops often write «۴۵۰ تومن» for 450,000
    /// Toman, so a price matches a caption number as it is, or times a thousand or a million.
    /// </summary>
    public static IReadOnlySet<long> CaptionNumbers(string caption)
    {
        var latin = PersianDigits.ToLatin(caption);
        var set = new HashSet<long>();
        foreach (Match m in Numbers().Matches(latin))
        {
            if (long.TryParse(m.Value.Replace(",", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal).Replace("٬", string.Empty, StringComparison.Ordinal),
                NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                set.Add(n);
            }
        }

        return set;
    }

    public static bool InCaption(long toman, IReadOnlySet<long> numbers) =>
        numbers.Contains(toman) || (toman % 1_000 == 0 && numbers.Contains(toman / 1_000)) || (toman % 1_000_000 == 0 && numbers.Contains(toman / 1_000_000));

    private static long? Toman(JsonElement product)
    {
        if (!product.TryGetProperty("price", out var price))
        {
            return null;
        }

        return price.ValueKind switch
        {
            JsonValueKind.Number when price.TryGetInt64(out var n) => n,
            JsonValueKind.Number when price.TryGetDecimal(out var d) => (long)Math.Round(d),
            JsonValueKind.String when long.TryParse(
                PersianDigits.ToLatin(price.GetString()!).Replace(",", string.Empty, StringComparison.Ordinal).Replace("٬", string.Empty, StringComparison.Ordinal).Trim(),
                NumberStyles.None, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    private static string? Text(JsonElement e, string name, int max) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } text
            ? (text.Length <= max ? text : text[..max])
            : null;
}

/// <summary>
/// AI group: one page of an import. Reads up to <see cref="FeedImport.PageSize"/> posts, asks the model about each caption, saves the
/// suggestions, and asks for the next page until the import has read its posts or the feed ends.
/// </summary>
internal sealed class FeedImportPageHandler(
    KnowledgeDbContext db, IChannelMediaReader media, FeedExtractor extractor, IClock clock, ILogger<FeedImportPageHandler> logger)
    : IdempotentHandler<FeedImportPageRequested, KnowledgeDbContext>(db)
{
    protected override async Task HandleOnceAsync(FeedImportPageRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var import = await Db.FeedImports.FirstOrDefaultAsync(i => i.Id == message.ImportId, cancellationToken);
        if (import is not { Status: FeedImportStatuses.Running } || import.Remaining == 0)
        {
            return;
        }

        var now = clock.UtcNow;
        var page = await media.ListMediaAsync(import.ChannelAccountId, message.After, Math.Min(FeedImport.PageSize, import.Remaining), cancellationToken);
        if (page.Value is not { } posts)
        {
            if (page.Retryable)
            {
                // Rate limited or Instagram is down: throwing lets the consumer retry this page later.
                throw new InvalidOperationException($"Feed page not readable yet: {page.ErrorCode}");
            }

            import.Fail(page.ErrorCode ?? "channel.unavailable", now);
            return;
        }

        int analyzed = 0, failed = 0, found = 0;
        foreach (var post in posts.Items)
        {
            if (string.IsNullOrWhiteSpace(post.Caption))
            {
                continue;
            }

            IReadOnlyList<FeedSuggestion> suggestions;
            try
            {
                suggestions = await extractor.ExtractAsync(post.Caption, import.WorkspaceId, cancellationToken);
                analyzed++;
            }
            catch (Exception e) when (e is AllRoutesFailedException or NoRouteException)
            {
                logger.LogWarning("Feed import {ImportId}: post {MediaId} not analyzed: {Error}", import.Id, post.Id, e.Message);
                failed++;
                continue;
            }

            var image = (post.MediaType == "VIDEO" ? post.ThumbnailUrl : post.MediaUrl ?? post.ThumbnailUrl)?.ToString();
            foreach (var s in suggestions)
            {
                Db.FeedImportItems.Add(new FeedImportItem
                {
                    Id = Uuid.NewV7(now),
                    WorkspaceId = import.WorkspaceId,
                    ImportId = import.Id,
                    Kind = s.Kind,
                    MediaId = post.Id,
                    Permalink = post.Permalink?.ToString(),
                    ImageUrl = image,
                    Title = s.Title,
                    Description = s.Description,
                    Price = s.Price,
                    PriceInCaption = s.PriceInCaption,
                    Tags = [.. s.Tags],
                    Question = s.Question,
                    Answer = s.Answer,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                found++;
            }
        }

        import.Read(posts.Items.Count, analyzed, failed, found, now);
        if (import.Remaining > 0 && posts.NextCursor is { } next && posts.Items.Count > 0)
        {
            Db.Publish(new FeedImportPageRequested(import.WorkspaceId, import.Id, next), import.Id.ToString(), workspaceId: import.WorkspaceId);
        }
        else
        {
            import.Complete(now);
        }
    }
}
