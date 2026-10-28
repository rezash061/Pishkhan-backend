using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Pishkhan.Infrastructure.Security;

/// <summary>
/// Envelope encryption for secrets at rest (Instagram tokens, PSP keys, SMS keys, bank cards): a random 256-bit data key per record
/// encrypts the value with AES-256-GCM; the data key is wrapped by a key-encryption key (KEK). The output names the KEK,
/// so KEKs can be rotated. Production KEKs come from Vault transit or the environment; never from the repository.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext, string purpose);

    string Unprotect(string protectedValue, string purpose);
}

/// <summary>HMAC "blind index" for exact-match lookups on encrypted columns (phone numbers, card numbers).</summary>
public interface IBlindIndexer
{
    string Compute(string normalizedValue, string purpose);
}

public sealed class SecretProtectionOptions
{
    public const string Section = "Secrets";

    /// <summary>Id of the active KEK used for new values.</summary>
    public string ActiveKeyId { get; set; } = "dev-1";

    /// <summary>KEKs by id, each 32 bytes base64. Older keys stay listed for decryption.</summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    /// <summary>32-byte base64 key for blind indexes. Rotating it requires re-indexing.</summary>
    public string? BlindIndexKey { get; set; }
}

public sealed class EnvelopeSecretProtector : ISecretProtector, IBlindIndexer
{
    private const byte Version = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly SecretProtectionOptions _options;
    private readonly Dictionary<string, byte[]> _keks;
    private readonly byte[] _blindIndexKey;

    public EnvelopeSecretProtector(IOptions<SecretProtectionOptions> options)
    {
        _options = options.Value;
        _keks = _options.Keys.ToDictionary(k => k.Key, k => Convert.FromBase64String(k.Value), StringComparer.Ordinal);
        if (!_keks.TryGetValue(_options.ActiveKeyId, out var active) || active.Length != KeySize)
        {
            throw new InvalidOperationException($"Secrets:Keys:{_options.ActiveKeyId} must be a 32-byte base64 key.");
        }

        _blindIndexKey = _options.BlindIndexKey is { Length: > 0 } b ? Convert.FromBase64String(b) : throw new InvalidOperationException("Secrets:BlindIndexKey is not configured.");
    }

    public string Protect(string plaintext, string purpose)
    {
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var aad = Encoding.UTF8.GetBytes(purpose);
            var (wrapNonce, wrapped, wrapTag) = Encrypt(_keks[_options.ActiveKeyId], dataKey, aad);
            var (nonce, cipher, tag) = Encrypt(dataKey, Encoding.UTF8.GetBytes(plaintext), aad);

            var keyId = Encoding.ASCII.GetBytes(_options.ActiveKeyId);
            var buffer = new byte[1 + 1 + keyId.Length + NonceSize + KeySize + TagSize + NonceSize + TagSize + 4 + cipher.Length];
            var span = buffer.AsSpan();
            span[0] = Version;
            span[1] = (byte)keyId.Length;
            var offset = 2;
            Write(span, ref offset, keyId);
            Write(span, ref offset, wrapNonce);
            Write(span, ref offset, wrapped);
            Write(span, ref offset, wrapTag);
            Write(span, ref offset, nonce);
            Write(span, ref offset, tag);
            BinaryPrimitives.WriteInt32BigEndian(span[offset..], cipher.Length);
            offset += 4;
            Write(span, ref offset, cipher);
            return Convert.ToBase64String(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public string Unprotect(string protectedValue, string purpose)
    {
        var span = Convert.FromBase64String(protectedValue).AsSpan();
        if (span[0] != Version)
        {
            throw new CryptographicException("Unknown secret format version.");
        }

        var keyIdLength = span[1];
        var offset = 2;
        var keyId = Encoding.ASCII.GetString(Read(span, ref offset, keyIdLength));
        if (!_keks.TryGetValue(keyId, out var kek))
        {
            throw new CryptographicException($"Key {keyId} is not available.");
        }

        var wrapNonce = Read(span, ref offset, NonceSize);
        var wrapped = Read(span, ref offset, KeySize);
        var wrapTag = Read(span, ref offset, TagSize);
        var nonce = Read(span, ref offset, NonceSize);
        var tag = Read(span, ref offset, TagSize);
        var length = BinaryPrimitives.ReadInt32BigEndian(span[offset..]);
        offset += 4;
        var cipher = Read(span, ref offset, length);

        var aad = Encoding.UTF8.GetBytes(purpose);
        var dataKey = Decrypt(kek, wrapNonce, wrapped, wrapTag, aad);
        try
        {
            return Encoding.UTF8.GetString(Decrypt(dataKey, nonce, cipher, tag, aad));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public string Compute(string normalizedValue, string purpose) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_blindIndexKey, Encoding.UTF8.GetBytes(purpose + "\n" + normalizedValue)));

    private static (byte[] Nonce, byte[] Cipher, byte[] Tag) Encrypt(byte[] key, byte[] plaintext, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, aad);
        return (nonce, cipher, tag);
    }

    private static byte[] Decrypt(byte[] key, byte[] nonce, byte[] cipher, byte[] tag, byte[] aad)
    {
        var plaintext = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plaintext, aad);
        return plaintext;
    }

    private static void Write(Span<byte> span, ref int offset, ReadOnlySpan<byte> data)
    {
        data.CopyTo(span[offset..]);
        offset += data.Length;
    }

    private static byte[] Read(ReadOnlySpan<byte> span, ref int offset, int length)
    {
        var result = span.Slice(offset, length).ToArray();
        offset += length;
        return result;
    }
}

public static class SecurityExtensions
{
    public static IServiceCollection AddPishkhanSecrets(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecretProtectionOptions>().Bind(configuration.GetSection(SecretProtectionOptions.Section));
        services.AddSingleton<EnvelopeSecretProtector>();
        services.AddSingleton<ISecretProtector>(sp => sp.GetRequiredService<EnvelopeSecretProtector>());
        services.AddSingleton<IBlindIndexer>(sp => sp.GetRequiredService<EnvelopeSecretProtector>());
        return services;
    }
}
