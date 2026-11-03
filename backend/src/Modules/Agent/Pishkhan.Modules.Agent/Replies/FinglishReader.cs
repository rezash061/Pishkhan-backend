using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Pishkhan.AI;
using Pishkhan.Persian;

namespace Pishkhan.Modules.Agent.Replies;

/// <summary>
/// How the pipeline reads a customer turn. <see cref="Persian"/> is the text in Persian script, used for classification and retrieval;
/// for a Persian or English turn it is the turn itself. <see cref="Method"/>: <c>none</c>, <c>rules</c> or <c>model</c>.
/// </summary>
internal sealed record TurnReading(string Original, string Persian, bool IsFinglish, string Method)
{
    /// <summary>The classifier's input: a Finglish turn with its Persian reading, so the model sees both.</summary>
    public string ForClassifier => IsFinglish ? $"{Original}\n(Persian: {Persian})" : Original;
}

/// <summary>
/// Finglish before classification and retrieval (backend plan 10.4, gap G14). The letter rules and lexicon of <see cref="Finglish"/> come
/// first; when they knew less than <see cref="MinCoverage"/> of the words, a model writes it in Persian (prompt
/// <c>text.transliterate</c>, task <c>classify</c>), cached per word sequence for 30 days. A model outage falls back to the rules.
/// </summary>
internal sealed class FinglishReader(IModelRouter router, IPromptRegistry prompts, HybridCache cache, ILogger<FinglishReader> logger)
{
    public const string PromptKey = "text.transliterate";
    public const double MinCoverage = 0.6;
    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromDays(30), LocalCacheExpiration = TimeSpan.FromHours(1) };

    /// <summary>The reading without a model: the letter rules and lexicon only (eval runs, and the fallback).</summary>
    public static TurnReading ReadWithRules(string turn) =>
        Finglish.Detect(turn).IsFinglish ? new TurnReading(turn, PersianNormalizer.Normalize(Finglish.Transliterate(turn).Persian), true, "rules") : new TurnReading(turn, turn, false, "none");

    public async Task<TurnReading> ReadAsync(string turn, Guid? workspaceId, CancellationToken ct)
    {
        if (!Finglish.Detect(turn).IsFinglish)
        {
            return new TurnReading(turn, turn, false, "none");
        }

        var rules = Finglish.Transliterate(turn);
        if (rules.Coverage >= MinCoverage)
        {
            return new TurnReading(turn, PersianNormalizer.Normalize(rules.Persian), true, "rules");
        }

        // Cached per word sequence: the same Finglish text reads the same, whichever workspace sent it.
        var key = "finglish:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(' ', turn.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)))))[..32];
        try
        {
            // Only a model's answer is cached: a failure throws, and HybridCache does not cache exceptions.
            var fromModel = await cache.GetOrCreateAsync(
                key,
                (router, prompts, turn, workspaceId),
                static async (s, token) => await TransliterateAsync(s.router, s.prompts, s.turn, s.workspaceId, token),
                Entry,
                cancellationToken: ct);
            return new TurnReading(turn, PersianNormalizer.Normalize(fromModel), true, "model");
        }
        catch (Exception e) when (e is AllRoutesFailedException or NoRouteException or KeyNotFoundException or InvalidDataException)
        {
            logger.LogWarning("Finglish transliteration by model unavailable, using the rules: {Error}", e.Message);
            return new TurnReading(turn, PersianNormalizer.Normalize(rules.Persian), true, "rules");
        }
    }

    private static async Task<string> TransliterateAsync(IModelRouter router, IPromptRegistry prompts, string turn, Guid? workspaceId, CancellationToken ct)
    {
        var prompt = prompts.Get(PromptKey);
        var response = await router.GetChatClient(AiTasks.Classify, workspaceId).GetResponseAsync(
            [new ChatMessage(ChatRole.System, prompt.Text), new ChatMessage(ChatRole.User, turn)], new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, ct);
        return Read(response.Text) ?? throw new InvalidDataException("The model's transliteration was not valid json");
    }

    /// <summary>Reads <c>{"persian": "..."}</c>; null when missing, empty or longer than 2,000 characters.</summary>
    public static string? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("persian", out var p)
                && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 and <= 2_000 } text && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
