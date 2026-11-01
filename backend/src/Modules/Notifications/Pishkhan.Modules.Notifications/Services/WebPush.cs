using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Notifications.Domain;
using Pishkhan.Modules.Notifications.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Notifications.Services;

/// <summary>Web push (VAPID, RFC 8292). Without keys, push is off: subscriptions are refused and nothing is sent.</summary>
public sealed class WebPushOptions
{
    public const string Section = "WebPush";

    /// <summary>The VAPID public key: base64url of the uncompressed P-256 point (65 bytes), as browsers take it in <c>applicationServerKey</c>.</summary>
    public string? PublicKey { get; set; }

    /// <summary>The VAPID private key: base64url of the 32-byte scalar. A secret.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Who push services contact about our traffic (<c>mailto:</c> or https).</summary>
    public string Subject { get; set; } = "mailto:support@pishkhan.ir";

    /// <summary>Push service hosts we deliver to (suffix match). Subscriptions to other hosts are refused, so a subscription cannot point the server anywhere else.</summary>
    public List<string> AllowedHosts { get; set; } =
        [".googleapis.com", ".push.services.mozilla.com", ".notify.windows.com", ".push.apple.com"];

    /// <summary>How long a push service keeps an undelivered message.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(12);

    public bool Enabled => !string.IsNullOrEmpty(PublicKey) && !string.IsNullOrEmpty(PrivateKey);
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}

/// <summary>
/// Message encryption for web push (RFC 8291, <c>aes128gcm</c> from RFC 8188), one record. The ephemeral key and salt can be passed
/// in for the RFC's test vector; otherwise they are new for every message.
/// </summary>
public static class WebPushEncryption
{
    public const int RecordSize = 4096;

    /// <summary>A push message body fits one 4096-byte record: 4096 minus the 16-byte tag and the delimiter.</summary>
    public const int MaxPlaintext = RecordSize - 16 - 1 - 86;

    public static byte[] Encrypt(byte[] uaPublic, byte[] authSecret, byte[] plaintext, ECDiffieHellman? serverKey = null, byte[]? salt = null)
    {
        if (plaintext.Length > MaxPlaintext)
        {
            throw new ArgumentException("The push message is too long.", nameof(plaintext));
        }

        using var ephemeral = serverKey is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var asKey = serverKey ?? ephemeral!;
        salt ??= RandomNumberGenerator.GetBytes(16);
        var asPublic = PublicPoint(asKey.ExportParameters(false));

        using var ua = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Point(uaPublic) });
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);

        // IKM = HKDF(auth_secret, ecdh_secret, "WebPush: info" || 0x00 || ua_public || as_public, 32)
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var padded = Concat(plaintext, [0x02]);
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Encrypt(nonce, padded, cipher, tag);
        }

        // Header: salt (16) || record size (4, big endian) || key id length (1) || key id (the server's public key, 65).
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    public static ECPoint Point(byte[] uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 0x04)
        {
            throw new ArgumentException("Expected an uncompressed P-256 point.", nameof(uncompressed));
        }

        return new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..] };
    }

    public static byte[] PublicPoint(ECParameters parameters) => Concat([0x04], parameters.Q.X!, parameters.Q.Y!);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}

/// <summary>The VAPID <c>Authorization</c> header (RFC 8292): an ES256 JWT for the push service's origin, and our public key.</summary>
public static class Vapid
{
    public static string Authorization(Uri endpoint, string subject, string publicKey, string privateKey, DateTimeOffset now)
    {
        var header = Base64Url.Encode("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = now.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode(privateKey),
            Q = WebPushEncryption.Point(Base64Url.Decode(publicKey)),
        });
        var signature = key.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{Base64Url.Encode(signature)}, k={publicKey}";
    }
}

/// <summary>What the panel's service worker receives (JSON): it shows <c>title</c> and <c>body</c> and opens <c>link</c>.</summary>
public sealed record PushPayload(string Kind, string Title, string? Body, string? Link, Guid WorkspaceId);

/// <summary>
/// Sends web push to a user's browsers. Best effort: push is unreliable in Iran, so it is never the only channel. A subscription the push
/// service says is gone (404, 410) is deleted; one that keeps failing is dropped after <see cref="PushSubscription.MaxFailures"/>.
/// </summary>
internal sealed class WebPushSender(
    IHttpClientFactory http, IOptions<WebPushOptions> options, NotificationsDbContext db, IClock clock, ILogger<WebPushSender> logger)
{
    public const string ClientName = "webpush";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task SendAsync(Guid userId, PushPayload payload, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        var subscriptions = await db.PushSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
        if (subscriptions.Count == 0)
        {
            return;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(payload with { Body = Cut(payload.Body, 1_000) }, Json);
        var now = clock.UtcNow;
        foreach (var subscription in subscriptions)
        {
            try
            {
                var endpoint = new Uri(subscription.Endpoint);
                SafeHttpClient.EnsureAllowed(endpoint, o.AllowedHosts);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new ByteArrayContent(WebPushEncryption.Encrypt(Base64Url.Decode(subscription.P256dh), Base64Url.Decode(subscription.Auth), body)),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentEncoding.Add("aes128gcm");
                request.Headers.TryAddWithoutValidation("Authorization", Vapid.Authorization(endpoint, o.Subject, o.PublicKey!, o.PrivateKey!, now));
                request.Headers.Add("TTL", ((int)o.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
                request.Headers.Add("Urgency", "high");

                using var response = await http.CreateClient(ClientName).SendAsync(request, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    db.PushSubscriptions.Remove(subscription);
                }
                else if (response.IsSuccessStatusCode)
                {
                    subscription.Delivered(now);
                }
                else if (subscription.Failed())
                {
                    logger.LogInformation("Dropping push subscription {SubscriptionId} after repeated failures ({Status})", subscription.Id, (int)response.StatusCode);
                    db.PushSubscriptions.Remove(subscription);
                }
            }
            catch (Exception e) when (e is HttpRequestException or SafeHttpException or CryptographicException or FormatException or ArgumentException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "Web push to subscription {SubscriptionId} failed", subscription.Id);
                if (subscription.Failed())
                {
                    db.PushSubscriptions.Remove(subscription);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
