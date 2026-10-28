using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pishkhan.Infrastructure.Storage;

namespace Pishkhan.Modules.Channels.Instagram;

/// <summary>Verification of Meta webhook signatures and <c>signed_request</c> callbacks. Constant-time comparisons only.</summary>
public static class InstagramSignatures
{
    private const string Prefix = "sha256=";

    /// <summary>Checks <c>X-Hub-Signature-256: sha256=&lt;hex HMAC-SHA256(app secret, raw body)&gt;</c>.</summary>
    public static bool VerifyWebhook(ReadOnlySpan<byte> body, string? header, string appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || header is null || !header.StartsWith(Prefix, StringComparison.Ordinal) || header.Length != Prefix.Length + 64)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body, expected);
        Span<byte> given = stackalloc byte[32];
        return Convert.FromHexString(header.AsSpan(Prefix.Length), given, out _, out var written) == System.Buffers.OperationStatus.Done
            && written == 32
            && CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public static string Sign(ReadOnlySpan<byte> body, string appSecret) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body));

    /// <summary>Parses a <c>signed_request</c> (<c>base64url(sig).base64url(json)</c>, HMAC-SHA256 over the payload part). Null when invalid.</summary>
    public static JsonElement? ParseSignedRequest(string? signedRequest, string appSecret)
    {
        if (string.IsNullOrEmpty(signedRequest) || string.IsNullOrEmpty(appSecret))
        {
            return null;
        }

        var dot = signedRequest.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == signedRequest.Length - 1)
        {
            return null;
        }

        try
        {
            var signature = Base64Url.Decode(signedRequest[..dot]);
            var payloadPart = signedRequest[(dot + 1)..];
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.ASCII.GetBytes(payloadPart));
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(Base64Url.Decode(payloadPart));
            return doc.RootElement.TryGetProperty("algorithm", out var alg) && string.Equals(alg.GetString(), "HMAC-SHA256", StringComparison.OrdinalIgnoreCase)
                ? doc.RootElement.Clone()
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
