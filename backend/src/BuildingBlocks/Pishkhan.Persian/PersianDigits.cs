using System.Text;

namespace Pishkhan.Persian;

public static class PersianDigits
{
    private const char PersianZero = '۰';
    private const char ArabicZero = '٠';

    /// <summary>Converts Persian (۰-۹) and Arabic-Indic (٠-٩) digits to ASCII digits. Other characters are kept.</summary>
    public static string ToLatin(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return string.Create(input.Length, input, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = ToLatin(source[i]);
            }
        });
    }

    public static char ToLatin(char c) => c switch
    {
        >= PersianZero and <= '۹' => (char)('0' + (c - PersianZero)),
        >= ArabicZero and <= '٩' => (char)('0' + (c - ArabicZero)),
        _ => c,
    };

    /// <summary>Converts ASCII and Arabic-Indic digits to Persian digits (for generated messages).</summary>
    public static string ToPersian(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            sb.Append(c switch
            {
                >= '0' and <= '9' => (char)(PersianZero + (c - '0')),
                >= ArabicZero and <= '٩' => (char)(PersianZero + (c - ArabicZero)),
                _ => c,
            });
        }

        return sb.ToString();
    }

    public static bool IsDigit(char c) => c is >= '0' and <= '9' or >= PersianZero and <= '۹' or >= ArabicZero and <= '٩';

    /// <summary>Keeps only digits (any script), converted to ASCII.</summary>
    public static string DigitsOnly(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (IsDigit(c))
            {
                sb.Append(ToLatin(c));
            }
        }

        return sb.ToString();
    }
}
