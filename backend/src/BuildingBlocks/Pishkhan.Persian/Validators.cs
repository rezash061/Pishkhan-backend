using Pishkhan.SharedKernel;

namespace Pishkhan.Persian;

/// <summary>Validators for Iranian identifiers. All accept Persian and Arabic-Indic digits.</summary>
public static class IranianMobile
{
    public static bool IsValid(string? input) => PhoneNumber.TryParse(input, out _);

    /// <summary>Returns the E.164 form (+989xxxxxxxxx) or null.</summary>
    public static string? ToE164(string? input) => PhoneNumber.TryParse(input, out var phone) ? phone.Value.E164 : null;
}

public static class NationalId
{
    /// <summary>Validates the 10-digit Iranian national code (کد ملی) checksum.</summary>
    public static bool IsValid(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var digits = PersianDigits.ToLatin(input.Trim()).Replace("-", string.Empty, StringComparison.Ordinal);
        if (digits.Length is < 8 or > 10 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        digits = digits.PadLeft(10, '0');
        if (digits.Distinct().Count() == 1)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (digits[i] - '0') * (10 - i);
        }

        var remainder = sum % 11;
        var check = digits[9] - '0';
        return remainder < 2 ? check == remainder : check == 11 - remainder;
    }
}

public static class BankCard
{
    /// <summary>Validates a 16-digit Iranian bank card (Shetab) with the Luhn checksum.</summary>
    public static bool IsValid(string? input)
    {
        var digits = Clean(input);
        return digits is { Length: 16 } && Luhn(digits);
    }

    public static string? Clean(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var cleaned = PersianDigits.ToLatin(input).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return cleaned.All(char.IsAsciiDigit) ? cleaned : null;
    }

    public static bool Luhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    /// <summary>Last four digits, for display and receipts.</summary>
    public static string? Last4(string? input) => Clean(input) is { Length: >= 4 } d ? d[^4..] : null;

    /// <summary>Masked form 6037-99**-****-1234.</summary>
    public static string? Mask(string? input) =>
        Clean(input) is { Length: 16 } d ? $"{d[..4]}-{d[4..6]}**-****-{d[^4..]}" : null;

    /// <summary>Returns the issuing bank by BIN (first six digits), or null if unknown.</summary>
    public static IranianBank? GetBank(string? input)
    {
        var digits = Clean(input);
        if (digits is null || digits.Length < 6)
        {
            return null;
        }

        return IranianBanks.ByBin.TryGetValue(digits[..6], out var bank) ? bank : null;
    }
}

public static class Sheba
{
    /// <summary>Validates an Iranian IBAN (شبا): IR + 24 digits, ISO 13616 mod 97.</summary>
    public static bool IsValid(string? input)
    {
        var iban = Clean(input);
        if (iban is null || iban.Length != 26 || !iban.StartsWith("IR", StringComparison.Ordinal) || !iban[2..].All(char.IsAsciiDigit))
        {
            return false;
        }

        // Move the first four characters to the end; letters become numbers (I=18, R=27).
        var rearranged = iban[4..] + "1827" + iban[2..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            remainder = ((remainder * 10) + (c - '0')) % 97;
        }

        return remainder == 1;
    }

    public static string? Clean(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var s = PersianDigits.ToLatin(input).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (s.All(char.IsAsciiDigit) && s.Length == 24)
        {
            s = "IR" + s;
        }

        return s;
    }

    /// <summary>The bank code is digits 5-7 of the IBAN (the three digits after the check digits and the leading zero).</summary>
    public static IranianBank? GetBank(string? input)
    {
        var iban = Clean(input);
        if (!IsValid(iban))
        {
            return null;
        }

        var code = iban![5..7];
        return IranianBanks.BySheba.TryGetValue(code, out var bank) ? bank : null;
    }
}

public static partial class PostalCode
{
    // Iranian postal codes: the first four digits avoid 0 and 2, the fifth avoids 0, 2 and 5, the rest avoid 2,
    // and a code never starts with four identical digits.
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?!(\d)\1{3})[13-9]{4}[1346-9][013-9]{5}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    /// <summary>Validates a 10-digit Iranian postal code (کد پستی).</summary>
    public static bool IsValid(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var digits = PersianDigits.ToLatin(input.Trim()).Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        return Pattern().IsMatch(digits);
    }
}
