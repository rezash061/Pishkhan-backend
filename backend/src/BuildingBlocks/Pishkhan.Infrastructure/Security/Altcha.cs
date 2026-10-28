using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.SharedKernel;
using StackExchange.Redis;

namespace Pishkhan.Infrastructure.Security;

public sealed class AltchaOptions
{
    public const string Section = "Altcha";

    public bool Enabled { get; set; } = true;

    /// <summary>HMAC key (base64). Required when enabled.</summary>
    public string? HmacKey { get; set; }

    public int MaxNumber { get; set; } = 100_000;

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed record AltchaChallenge(string Algorithm, string Challenge, int MaxNumber, string Salt, string Signature);

/// <summary>
/// ALTCHA proof-of-work (self-hosted, no foreign CAPTCHA, overview section 10). The server issues a signed challenge;
/// the browser finds the number whose SHA-256 with the salt equals the challenge. Each solution is accepted once.
/// </summary>
public sealed class AltchaVerifier(IOptions<AltchaOptions> options, [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys, IClock clock)
{
    private byte[] Key => Convert.FromBase64String(options.Value.HmacKey ?? throw new InvalidOperationException("Altcha:HmacKey is not configured."));

    public bool Enabled => options.Value.Enabled;

    public AltchaChallenge CreateChallenge()
    {
        var o = options.Value;
        var expires = clock.UtcNow.Add(o.Lifetime).ToUnixTimeSeconds();
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)) + "?expires=" + expires;
        var number = RandomNumberGenerator.GetInt32(0, o.MaxNumber);
        var challenge = Sha256Hex(salt + number);
        return new AltchaChallenge("SHA-256", challenge, o.MaxNumber, salt, Sign(challenge));
    }

    public async Task<bool> VerifyAsync(string? payloadBase64)
    {
        if (!Enabled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(payloadBase64) || payloadBase64.Length > 2000)
        {
            return false;
        }

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(payloadBase64));
            var algorithm = json.GetProperty("algorithm").GetString();
            var challenge = json.GetProperty("challenge").GetString()!;
            var number = json.GetProperty("number").GetInt32();
            var salt = json.GetProperty("salt").GetString()!;
            var signature = json.GetProperty("signature").GetString()!;
            if (algorithm != "SHA-256" || !TryGetExpiry(salt, out var expires) || expires < clock.UtcNow.ToUnixTimeSeconds())
            {
                return false;
            }

            var validSignature = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(challenge)), Encoding.ASCII.GetBytes(signature));
            var validWork = string.Equals(Sha256Hex(salt + number), challenge, StringComparison.Ordinal);
            if (!validSignature || !validWork)
            {
                return false;
            }

            var ttl = TimeSpan.FromSeconds(Math.Max(1, expires - clock.UtcNow.ToUnixTimeSeconds()));
            return await redis.GetDatabase().StringSetAsync(keys.For("altcha", challenge), "1", ttl, When.NotExists);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetExpiry(string salt, out long expires)
    {
        expires = 0;
        var index = salt.IndexOf("expires=", StringComparison.Ordinal);
        return index >= 0 && long.TryParse(salt.AsSpan(index + 8).ToString().Split('&')[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out expires);
    }

    private string Sign(string challenge) => Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(challenge)));

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
