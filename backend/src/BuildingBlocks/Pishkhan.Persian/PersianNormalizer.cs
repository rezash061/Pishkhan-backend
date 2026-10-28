using System.Text;

namespace Pishkhan.Persian;

/// <summary>
/// Normalizes Persian text for matching, search and AI input. The original text is always stored next to the
/// normalized copy; this output is never shown to users.
/// Rules (backend plan section 3.2):
/// Arabic letters to Persian (ي ى ك ة ۀ أ إ ٱ), Persian and Arabic-Indic digits to ASCII, kashida and diacritics removed,
/// ZWNJ and spaces normalized, letters repeated more than twice collapsed to two, punctuation unified.
/// </summary>
public static class PersianNormalizer
{
    public const char Zwnj = '‌';

    public static string Normalize(string? input) => Normalize(input, NormalizerOptions.Default);

    public static string Normalize(string? input, NormalizerOptions options)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Unicode compatibility composition folds presentation forms (ﻻ, ﺑ ...) to their base letters.
        var text = input.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        var pendingZwnj = false;

        foreach (var raw in text)
        {
            var c = MapChar(raw, options);
            if (c == '\0')
            {
                continue;
            }

            if (c == ' ')
            {
                pendingSpace = sb.Length > 0;
                pendingZwnj = false;
                continue;
            }

            if (c == Zwnj)
            {
                // A ZWNJ is meaningful only between two joining letters; next to spaces it is dropped.
                if (!pendingSpace && sb.Length > 0 && IsPersianLetter(sb[^1]))
                {
                    pendingZwnj = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            if (pendingZwnj)
            {
                if (IsPersianLetter(c))
                {
                    sb.Append(Zwnj);
                }

                pendingZwnj = false;
            }

            sb.Append(c);
        }

        var result = sb.ToString();
        if (options.CollapseRepeatedLetters)
        {
            result = CollapseRepeats(result, options.MaxRepeat);
        }

        return result;
    }

    private static char MapChar(char c, NormalizerOptions options)
    {
        switch (c)
        {
            // Arabic to Persian letters
            case 'ي': // ي Arabic yeh
            case 'ى': // ى Alef maksura
            case 'ی' when false:
                return 'ی'; // ی
            case 'ك': // ك Arabic kaf
                return 'ک'; // ک
            case 'ة': // ة teh marbuta
                return 'ه'; // ه
            case 'ۀ': // ۀ heh with yeh above
                return 'ه';
            case 'أ': // أ
            case 'إ': // إ
            case 'ٱ': // ٱ
                return 'ا'; // ا
            case 'ؤ': // ؤ
                return 'و'; // و
            case 'ئ': // ئ (kept as yeh for matching)
                return 'ی';
            case 'ە': // ە ae
                return 'ه';

            // Kashida (tatweel) is decorative
            case 'ـ':
                return '\0';

            // Whitespace family
            case '\t':
            case '\n':
            case '\r':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case ' ':
            case '　':
                return ' ';

            // Zero-width joiner-like characters that users type instead of ZWNJ
            case '‌':
                return Zwnj;
            case '‍': // ZWJ
            case '​': // zero width space
            case '﻿': // BOM
            case '­': // soft hyphen
            case '‎': // LRM
            case '‏': // RLM
            case '‪':
            case '‫':
            case '‬':
            case '‭':
            case '‮':
            case '⁦':
            case '⁧':
            case '⁨':
            case '⁩':
                return '\0';

            // Punctuation unification
            case '،': // ،
                return options.UnifyPunctuation ? ',' : c;
            case '؛': // ؛
                return options.UnifyPunctuation ? ';' : c;
            case '؟': // ؟
                return options.UnifyPunctuation ? '?' : c;
            case '٪': // ٪
                return options.UnifyPunctuation ? '%' : c;
            case '۔': // ۔
                return options.UnifyPunctuation ? '.' : c;
            case '«': // «
            case '»': // »
            case '“':
            case '”':
            case '„':
                return options.UnifyPunctuation ? '"' : c;
            case '‘':
            case '’':
                return options.UnifyPunctuation ? '\'' : c;
            case '–':
            case '—':
            case '−':
                return options.UnifyPunctuation ? '-' : c;
            case '٫': // ٫ Arabic decimal separator
                return '.';
            case '٬': // ٬ Arabic thousands separator
                return ',';
        }

        if (PersianDigits.IsDigit(c))
        {
            return options.LatinDigits ? PersianDigits.ToLatin(c) : c;
        }

        // Arabic diacritics (harakat), superscript alef, hamza above/below marks
        if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or >= 'ۖ' and <= 'ۭ')
        {
            return '\0';
        }

        if (options.LowercaseLatin && c is >= 'A' and <= 'Z')
        {
            return char.ToLowerInvariant(c);
        }

        if (char.IsControl(c))
        {
            return '\0';
        }

        return c;
    }

    private static string CollapseRepeats(string input, int maxRepeat)
    {
        if (input.Length <= maxRepeat)
        {
            return input;
        }

        var sb = new StringBuilder(input.Length);
        var run = 0;
        var previous = '\0';
        foreach (var c in input)
        {
            run = c == previous ? run + 1 : 1;
            previous = c;

            // Digits are never collapsed: "1000" must stay "1000".
            if (run > maxRepeat && char.IsLetter(c))
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    public static bool IsPersianLetter(char c) =>
        c is >= 'ء' and <= 'غ' or >= 'ف' and <= 'ي' or 'پ' or 'چ' or 'ژ' or 'ک' or 'گ' or 'ی' or 'آ';

    /// <summary>True if most letters in the text are in the Arabic script block.</summary>
    public static bool IsMostlyPersianScript(string text)
    {
        var persian = 0;
        var latin = 0;
        foreach (var c in text)
        {
            if (IsPersianLetter(c))
            {
                persian++;
            }
            else if (char.IsAsciiLetter(c))
            {
                latin++;
            }
        }

        return persian > 0 && persian >= latin;
    }
}

public sealed record NormalizerOptions
{
    public static readonly NormalizerOptions Default = new();

    public bool LatinDigits { get; init; } = true;

    public bool UnifyPunctuation { get; init; } = true;

    public bool CollapseRepeatedLetters { get; init; } = true;

    public int MaxRepeat { get; init; } = 2;

    public bool LowercaseLatin { get; init; } = true;
}
