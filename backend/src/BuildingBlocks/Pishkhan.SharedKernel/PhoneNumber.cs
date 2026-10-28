using System.Diagnostics.CodeAnalysis;

namespace Pishkhan.SharedKernel;

/// <summary>
/// An Iranian mobile number in E.164 form (<c>+989xxxxxxxxx</c>).
/// Accepts Persian and Arabic-Indic digits, spaces, dashes, and the forms 09.., 9.., 989.., 00989.., +989...
/// </summary>
public readonly record struct PhoneNumber
{
    private PhoneNumber(string e164) => E164 = e164;

    public string E164 { get; }

    /// <summary>The national form, for example 09121234567.</summary>
    public string National => "0" + E164[3..];

    /// <summary>Masked for logs and UI, for example +98912***4567.</summary>
    public string Masked => E164[..6] + "***" + E164[^4..];

    public static bool TryParse(string? input, [NotNullWhen(true)] out PhoneNumber? phone)
    {
        phone = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 32)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[input.Length];
        var length = 0;
        var plus = false;
        foreach (var raw in input)
        {
            var c = ToAsciiDigit(raw);
            if (c is >= '0' and <= '9')
            {
                buffer[length++] = c;
            }
            else if (c == '+' && length == 0 && !plus)
            {
                plus = true;
            }
            else if (c is ' ' or '-' or '(' or ')' or '‌' or '‏' or '‎')
            {
                // separators and direction marks are ignored
            }
            else
            {
                return false;
            }
        }

        var digits = new string(buffer[..length]);
        string? national = null;
        if (digits.StartsWith("0098", StringComparison.Ordinal))
        {
            national = digits[4..];
        }
        else if (digits.StartsWith("98", StringComparison.Ordinal) && digits.Length == 12)
        {
            national = digits[2..];
        }
        else if (!plus && digits.StartsWith('0') && digits.Length == 11)
        {
            national = digits[1..];
        }
        else if (!plus && digits.Length == 10)
        {
            national = digits;
        }

        if (national is null || national.Length != 10 || national[0] != '9')
        {
            return false;
        }

        phone = new PhoneNumber("+98" + national);
        return true;
    }

    public static PhoneNumber Parse(string input) =>
        TryParse(input, out var phone) ? phone.Value : throw new DomainException("validation.phone.invalid");

    public override string ToString() => Masked;

    internal static char ToAsciiDigit(char c) => c switch
    {
        >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
        >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
        _ => c,
    };
}
