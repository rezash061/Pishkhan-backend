using System.Text.RegularExpressions;

namespace Pishkhan.Persian;

/// <summary>Fixes common half-space (ZWNJ) mistakes in generated Persian text, for example "می خواهم" to "می‌خواهم".</summary>
public static partial class ZwnjFixer
{
    [GeneratedRegex(@"(^|\s)(ن?می)\s+(?=[؀-ۿ])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex MiPrefix();

    [GeneratedRegex(@"(?<=[؀-ۿ])\s+(ها|های|هایی|تر|ترین|ام|ات|اش|ایم|اید|اند)(?=$|[\s\p{P}])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Suffixes();

    public static string Fix(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = MiPrefix().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + PersianNormalizer.Zwnj);
        return Suffixes().Replace(result, m => PersianNormalizer.Zwnj + m.Groups[1].Value);
    }
}
