using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pishkhan.Persian;

namespace Pishkhan.Modules.Agent.Replies;

/// <summary>An amount found in text, in Toman. <see cref="Unit"/> is <c>toman</c>, <c>rial</c> or null when no currency was written.</summary>
public sealed record MoneyMention(string Text, long Toman, string? Unit);

/// <summary>
/// Finds amounts in Persian, Finglish and English text: digits in any script with thousands separators («۱٬۸۵۰٬۰۰۰», «1,850,000»,
/// «۱.۸۵۰.۰۰۰»), scale words («۱۸۵ هزار», «۱ میلیون و ۸۵۰ هزار») and number words («یک میلیون و هشتصد و پنجاه هزار»). An amount counts as
/// money when a currency follows it (تومان، تومن، ریال، toman, rial), when it uses a scale word and is at least 10,000, or when a price
/// word («قیمت», «مبلغ», «price») comes just before it and it is at least 1,000.
/// </summary>
public static class MoneyText
{
    private static readonly Regex Token = new(
        @"\d+(?:[,٬.]\d{3})+(?![\d])|\d+(?:[.٫]\d+)?|[\p{L}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Dictionary<string, long> Words = new(StringComparer.Ordinal)
    {
        ["صفر"] = 0, ["یک"] = 1, ["یه"] = 1, ["دو"] = 2, ["سه"] = 3, ["چهار"] = 4, ["پنج"] = 5, ["شش"] = 6, ["شیش"] = 6, ["هفت"] = 7, ["هشت"] = 8, ["نه"] = 9,
        ["ده"] = 10, ["یازده"] = 11, ["دوازده"] = 12, ["سیزده"] = 13, ["چهارده"] = 14, ["پانزده"] = 15, ["پونزده"] = 15, ["شانزده"] = 16, ["شونزده"] = 16,
        ["هفده"] = 17, ["هجده"] = 18, ["هیجده"] = 18, ["نوزده"] = 19,
        ["بیست"] = 20, ["سی"] = 30, ["چهل"] = 40, ["پنجاه"] = 50, ["شصت"] = 60, ["هفتاد"] = 70, ["هشتاد"] = 80, ["نود"] = 90,
        ["صد"] = 100, ["یکصد"] = 100, ["دویست"] = 200, ["سیصد"] = 300, ["چهارصد"] = 400, ["پانصد"] = 500, ["پونصد"] = 500, ["ششصد"] = 600,
        ["شیشصد"] = 600, ["هفتصد"] = 700, ["هشتصد"] = 800, ["نهصد"] = 900,
    };

    private static readonly Dictionary<string, long> Scales = new(StringComparer.Ordinal)
    {
        ["هزار"] = 1_000, ["میلیون"] = 1_000_000, ["ملیون"] = 1_000_000, ["میلیارد"] = 1_000_000_000,
        ["thousand"] = 1_000, ["million"] = 1_000_000, ["k"] = 1_000,
    };

    private static readonly Dictionary<string, string> Currencies = new(StringComparer.Ordinal)
    {
        ["تومان"] = "toman", ["تومن"] = "toman", ["تومنی"] = "toman", ["تومانی"] = "toman", ["toman"] = "toman", ["tomans"] = "toman", ["tooman"] = "toman", ["t"] = "toman",
        ["ریال"] = "rial", ["ریالی"] = "rial", ["rial"] = "rial", ["rials"] = "rial", ["irr"] = "rial", ["irt"] = "toman",
    };

    private static readonly HashSet<string> PriceWords = new(StringComparer.Ordinal) { "قیمت", "قیمتش", "مبلغ", "هزینه", "هزینه‌ی", "price", "cost", "ghimat", "gheymat" };

    public static IReadOnlyList<MoneyMention> Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = PersianDigits.ToLatin(PersianNormalizer.Normalize(text)).Replace(PersianNormalizer.Zwnj, ' ');
        var tokens = Token.Matches(normalized).Select(m => (m.Value.ToLowerInvariant(), m.Index, m.Length)).ToList();
        var result = new List<MoneyMention>();
        var i = 0;
        while (i < tokens.Count)
        {
            if (!IsNumber(tokens[i].Item1))
            {
                i++;
                continue;
            }

            var start = i;
            decimal total = 0, current = 0;
            var scaled = false;
            var any = false;
            while (i < tokens.Count)
            {
                var t = tokens[i].Item1;
                if (TryNumber(t, out var n))
                {
                    current += n;
                    any = true;
                }
                else if (Scales.TryGetValue(t, out var scale) && any)
                {
                    total += (current == 0 ? 1 : current) * scale;
                    current = 0;
                    scaled = true;
                }
                else if (t == "و" && i + 1 < tokens.Count && IsNumber(tokens[i + 1].Item1) && any)
                {
                    // «یک میلیون و هشتصد»: keep adding.
                }
                else
                {
                    break;
                }

                i++;
            }

            var value = (long)decimal.Truncate(total + current);
            var unit = i < tokens.Count && Currencies.TryGetValue(tokens[i].Item1, out var u) ? u : null;
            var priced = start > 0 && Enumerable.Range(Math.Max(0, start - 3), start - Math.Max(0, start - 3)).Any(k => PriceWords.Contains(tokens[k].Item1));
            if (unit is not null || (scaled && value >= 10_000) || (priced && value >= 1_000))
            {
                var end = unit is null ? tokens[i - 1].Index + tokens[i - 1].Length : tokens[i].Index + tokens[i].Length;
                var toman = unit == "rial" ? value / 10 : value;
                result.Add(new MoneyMention(normalized[tokens[start].Index..end], toman, unit));
            }

            if (unit is not null)
            {
                i++;
            }
        }

        return result;
    }

    private static bool IsNumber(string token) => TryNumber(token, out _);

    private static bool TryNumber(string token, out decimal value)
    {
        if (Words.TryGetValue(token, out var word))
        {
            value = word;
            return true;
        }

        value = 0;
        if (token.Length == 0 || !char.IsAsciiDigit(token[0]))
        {
            return false;
        }

        // Groups of three after a separator are thousands («1.850.000»); otherwise «.» or «٫» is a decimal point («1.5 میلیون»).
        return Regex.IsMatch(token, @"^\d+(?:[,٬.]\d{3})+$", RegexOptions.None, TimeSpan.FromMilliseconds(50))
            ? decimal.TryParse(new string([.. token.Where(char.IsAsciiDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            : decimal.TryParse(token.Replace('٫', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}

public sealed record GuardIssue(string Kind, string Detail);

/// <summary>What a reply may contain: the facts and tool results it was given, and the profile's rules.</summary>
public sealed class GuardContext
{
    /// <summary>Amounts (Toman) found in retrieved facts and tool results: the only prices a reply may state.</summary>
    public HashSet<long> AllowedAmounts { get; } = [];

    public HashSet<string> AllowedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> NeverSay { get; init; } = [];

    /// <summary>The customer's turn, to check the reply's script.</summary>
    public string CustomerText { get; init; } = string.Empty;

    public bool ReplyInCustomerLanguage { get; init; } = true;

    public int MaxLength { get; init; } = ReplyGuard.MaxLength;

    /// <summary>Adds the amounts and links found in a fact or a tool result.</summary>
    public void Allow(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var m in MoneyText.Find(text))
        {
            AllowedAmounts.Add(m.Toman);
        }

        // Plain numbers too (a tool result's priceToman field), so an amount the model reformats still matches.
        foreach (Match n in Regex.Matches(PersianDigits.ToLatin(text), @"(?<![\d.])\d{4,15}(?![\d])", RegexOptions.None, TimeSpan.FromMilliseconds(200)))
        {
            if (long.TryParse(n.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            {
                AllowedAmounts.Add(v);
            }
        }

        foreach (var host in ReplyGuard.Hosts(text))
        {
            AllowedHosts.Add(host);
        }
    }
}

/// <summary>
/// Output guard (backend plan 10.2 step 7): prices only from facts and tools, the never-say list, the link allowlist, the length limit
/// and the script (Persian unless the customer wrote in another script).
/// </summary>
public static class ReplyGuard
{
    /// <summary>Instagram's DM text limit.</summary>
    public const int MaxLength = 1_000;

    private static readonly Regex Url = new(
        @"(?:https?://|www\.)[^\s<>()""'،]+|\b[a-z0-9][a-z0-9-]{0,62}(?:\.[a-z0-9-]{1,63})*\.(?:ir|com|net|org|shop|store|me|io|co|app)(?:/[^\s<>()""'،]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public static IReadOnlyList<GuardIssue> Check(string reply, GuardContext context)
    {
        var issues = new List<GuardIssue>();
        foreach (var m in MoneyText.Find(reply))
        {
            var ok = context.AllowedAmounts.Contains(m.Toman)
                || (m.Unit is null && m.Toman % 10 == 0 && context.AllowedAmounts.Contains(m.Toman / 10));
            if (!ok)
            {
                issues.Add(new GuardIssue("price", m.Text));
            }
        }

        var normalized = PersianNormalizer.Normalize(reply);
        foreach (var phrase in context.NeverSay)
        {
            if (phrase.Length > 0 && normalized.Contains(PersianNormalizer.Normalize(phrase), StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new GuardIssue("never_say", phrase));
            }
        }

        foreach (var host in Hosts(reply))
        {
            if (!context.AllowedHosts.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new GuardIssue("link", host));
            }
        }

        if (reply.Length > context.MaxLength)
        {
            issues.Add(new GuardIssue("length", reply.Length.ToString(CultureInfo.InvariantCulture)));
        }

        var expectPersian = !context.ReplyInCustomerLanguage || PersianShare(context.CustomerText) is null or >= 0.5;
        if (expectPersian && PersianShare(Url.Replace(reply, " ")) is < 0.5)
        {
            issues.Add(new GuardIssue("language", "fa"));
        }

        return issues;
    }

    /// <summary>Hosts of the links in the text, without <c>www.</c>.</summary>
    public static IEnumerable<string> Hosts(string text)
    {
        foreach (Match m in Url.Matches(text))
        {
            var value = m.Value.TrimEnd('.', ',', '!', '?', '؟');
            var withScheme = value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value : "https://" + value;
            if (Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            {
                yield return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            }
        }
    }

    /// <summary>The share of letters in Arabic script, or null when the text has no letters.</summary>
    public static double? PersianShare(string text)
    {
        int letters = 0, persian = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            letters++;
            if (c is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
            {
                persian++;
            }
        }

        return letters == 0 ? null : (double)persian / letters;
    }

    /// <summary>One line per issue, for the regeneration instruction.</summary>
    public static string Explain(IEnumerable<GuardIssue> issues)
    {
        var text = new StringBuilder();
        foreach (var issue in issues)
        {
            text.AppendLine(issue.Kind switch
            {
                "price" => $"- The amount «{issue.Detail}» is not in the facts or tool results. State only prices that a tool returned or a fact contains, or call search_products.",
                "never_say" => $"- Do not write «{issue.Detail}».",
                "link" => $"- Do not link to {issue.Detail}. Only use links from the facts or tool results.",
                "length" => $"- The reply is too long ({issue.Detail} characters). Keep it under {MaxLength}.",
                "language" => "- Write the reply in Persian.",
                _ => $"- {issue.Kind}: {issue.Detail}",
            });
        }

        return text.ToString();
    }
}
