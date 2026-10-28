using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pishkhan.Infrastructure.Security;

/// <summary>RFC 6238 TOTP (SHA-1, 6 digits, 30 s), compatible with Google Authenticator and similar apps.</summary>
public static class Totp
{
    private const int Digits = 6;
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string ToBase32(byte[] secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((secret.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in secret)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static string ProvisioningUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={(int)Step.TotalSeconds}";

    public static string Compute(byte[] secret, DateTimeOffset time)
    {
        var counter = time.ToUnixTimeSeconds() / (long)Step.TotalSeconds;
        return ComputeForCounter(secret, counter);
    }

    /// <summary>Accepts the current step and one step either side (clock drift). Returns the matched step so callers can reject reuse.</summary>
    public static bool Verify(byte[] secret, string? code, DateTimeOffset now, out long matchedStep)
    {
        matchedStep = 0;
        if (code is null)
        {
            return false;
        }

        var normalized = Persian.PersianDigits.ToLatin(code.Trim());
        if (normalized.Length != Digits || !normalized.All(char.IsAsciiDigit))
        {
            return false;
        }

        var current = now.ToUnixTimeSeconds() / (long)Step.TotalSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var candidate = ComputeForCounter(secret, current + offset);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(normalized)))
            {
                matchedStep = current + offset;
                return true;
            }
        }

        return false;
    }

    private static string ComputeForCounter(byte[] secret, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
#pragma warning disable CA5350 // RFC 6238 default; required for authenticator app compatibility
        var hash = HMACSHA1.HashData(secret, message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }
}
