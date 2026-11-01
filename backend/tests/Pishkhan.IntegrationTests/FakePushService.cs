using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pishkhan.Modules.Notifications.Services;

namespace Pishkhan.IntegrationTests;

/// <summary>A push service (FCM-like endpoints): records each delivery; an endpoint containing <c>gone</c> answers 410.</summary>
internal sealed class FakePushService : HttpMessageHandler
{
    private static readonly ECParameters Vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportParameters(true);

    public static readonly string VapidPublicKey = Base64Url.Encode(WebPushEncryption.PublicPoint(Vapid));
    public static readonly string VapidPrivateKey = Base64Url.Encode(Vapid.D);

    public static readonly ConcurrentQueue<(string Endpoint, string? Authorization, string? Encoding, byte[] Body)> Deliveries = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        Deliveries.Enqueue((request.RequestUri!.ToString(), request.Headers.TryGetValues("Authorization", out var a) ? a.Single() : null,
            request.Content.Headers.ContentEncoding.SingleOrDefault(), body));
        return new HttpResponseMessage(request.RequestUri.AbsolutePath.Contains("gone", StringComparison.Ordinal) ? HttpStatusCode.Gone : HttpStatusCode.Created);
    }

    /// <summary>A browser's side of a subscription: its key pair and auth secret, and decryption (RFC 8291) of what it receives.</summary>
    public sealed class Browser : IDisposable
    {
        private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        public byte[] AuthSecret { get; } = RandomNumberGenerator.GetBytes(16);

        public byte[] PublicKey => WebPushEncryption.PublicPoint(_key.ExportParameters(false));

        public string Decrypt(byte[] body)
        {
            var salt = body[..16];
            var keyLength = body[20];
            var serverPublic = body[21..(21 + keyLength)];
            var cipher = body[(21 + keyLength)..];
            using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = WebPushEncryption.Point(serverPublic) });
            var secret = _key.DeriveRawSecretAgreement(server.PublicKey);
            var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, AuthSecret, [.. "WebPush: info\0"u8, .. PublicKey, .. serverPublic]);
            var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());
            var plain = new byte[cipher.Length - 16];
            using (var aes = new AesGcm(cek, 16))
            {
                aes.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
            }

            plain[^1].ShouldBe((byte)0x02);
            return Encoding.UTF8.GetString(plain, 0, plain.Length - 1);
        }

        public void Dispose() => _key.Dispose();
    }
}
