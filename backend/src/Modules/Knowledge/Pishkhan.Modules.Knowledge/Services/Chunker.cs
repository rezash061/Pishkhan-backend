using System.Text;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Persian;

namespace Pishkhan.Modules.Knowledge.Services;

public sealed record TextChunk(string Text, int TokenCount);

/// <summary>
/// Persian-aware chunking (backend plan 9.7): sentences end at <c>.</c>, <c>؟</c>, <c>?</c>, <c>!</c> or a line break; sentences are packed
/// into chunks of up to <see cref="MaxTokens"/> with about <see cref="OverlapTokens"/> of the previous chunk repeated at the start of the next.
/// Tokens are estimated from words, which is close enough for budgeting.
/// </summary>
public static class Chunker
{
    public const int MaxTokens = 450;
    public const int OverlapTokens = 50;

    public static int EstimateTokens(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Max(1, (int)Math.Ceiling(words * 1.4));
    }

    public static IReadOnlyList<string> Sentences(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (c == '\n')
            {
                Flush();
                continue;
            }

            current.Append(c);
            if (c is '.' or '؟' or '?' or '!')
            {
                Flush();
            }
        }

        Flush();
        return sentences;

        void Flush()
        {
            var sentence = current.ToString().Trim();
            if (sentence.Length > 0)
            {
                sentences.Add(sentence);
            }

            current.Clear();
        }
    }

    public static IReadOnlyList<TextChunk> Split(string text)
    {
        var chunks = new List<TextChunk>();
        var window = new List<(string Text, int Tokens)>();
        var tokens = 0;
        foreach (var sentence in Sentences(text).SelectMany(Long))
        {
            var size = EstimateTokens(sentence);
            if (tokens + size > MaxTokens && window.Count > 0)
            {
                chunks.Add(Emit(window));

                // Carry the tail of the chunk into the next one.
                var carried = new List<(string, int)>();
                var carriedTokens = 0;
                for (var i = window.Count - 1; i >= 0 && carriedTokens < OverlapTokens; i--)
                {
                    carried.Insert(0, window[i]);
                    carriedTokens += window[i].Tokens;
                }

                window = carriedTokens + size <= MaxTokens ? carried : [];
                tokens = window.Sum(w => w.Tokens);
            }

            window.Add((sentence, size));
            tokens += size;
        }

        if (window.Count > 0)
        {
            chunks.Add(Emit(window));
        }

        return chunks;
    }

    /// <summary>A sentence longer than a chunk is cut by words.</summary>
    private static IEnumerable<string> Long(string sentence)
    {
        if (EstimateTokens(sentence) <= MaxTokens)
        {
            yield return sentence;
            yield break;
        }

        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var per = (int)(MaxTokens / 1.4);
        for (var i = 0; i < words.Length; i += per)
        {
            yield return string.Join(' ', words.Skip(i).Take(per));
        }
    }

    private static TextChunk Emit(List<(string Text, int Tokens)> window) =>
        new(string.Join(' ', window.Select(w => w.Text)), window.Sum(w => w.Tokens));

    public static string Faq(string question, string answer) => $"پرسش: {question.Trim()}\nپاسخ: {answer.Trim()}";

    /// <summary>The product as facts the AI can quote: prices in Toman with Persian digits, stock per variant.</summary>
    public static string Product(ProductSummary p)
    {
        var text = new StringBuilder();
        text.Append("محصول: ").Append(p.Title).Append('.').Append('\n');
        if (p.Category is { Length: > 0 } category)
        {
            text.Append("دسته‌بندی: ").Append(category).Append('.').Append('\n');
        }

        if (p.Tags.Count > 0)
        {
            text.Append("برچسب‌ها: ").Append(string.Join("، ", p.Tags)).Append('.').Append('\n');
        }

        if (p.MinPrice is { } min)
        {
            text.Append("قیمت: ").Append(Price(min));
            if (p.MaxPrice is { } max && max.Amount != min.Amount)
            {
                text.Append(" تا ").Append(Price(max));
            }

            text.Append('.').Append('\n');
        }

        text.Append(p.InStock ? "موجود است." : "ناموجود است.").Append('\n');
        foreach (var v in p.Variants.Take(50))
        {
            var attributes = string.Join("، ", v.Attributes.Select(a => $"{a.Key} {a.Value}"));
            text.Append("مدل ").Append(attributes.Length > 0 ? attributes : v.Sku ?? "پیش‌فرض").Append(": ").Append(Price(v.Price)).Append("، ")
                .Append(v.InStock ? "موجود" : "ناموجود").Append('.').Append('\n');
        }

        if (p.Description is { Length: > 0 } description)
        {
            text.Append(description.Trim());
        }

        return text.ToString().Trim();
    }

    private static string Price(SharedKernel.Money money) => PersianMoney.FormatToman(money);
}
