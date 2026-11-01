using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pishkhan.Modules.Notifications.Services;

namespace Pishkhan.UnitTests;

public sealed class WebPushTests
{
    [Fact]
    public void Encryption_matches_the_RFC_8291_test_vector()
    {
        // RFC 8291, Appendix A.
        var asPublic = Base64Url.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var asKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = WebPushEncryption.Point(asPublic),
        });
        var body = WebPushEncryption.Encrypt(
            Base64Url.Decode("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            Base64Url.Decode("BTBZMqHH6r4Tts7J_aSIgg"),
            Encoding.UTF8.GetBytes("When I grow up, I want to be a watermelon"),
            asKey,
            Base64Url.Decode("DGv6ra1nlYgDCS1FRnbzlw"));

        Base64Url.Encode(body.AsSpan(0, 86)).ShouldBe("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        Base64Url.Encode(body.AsSpan(86)).ShouldBe("8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ");
    }

    [Fact]
    public void Vapid_header_is_an_ES256_token_for_the_push_service_origin()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        var publicKey = Base64Url.Encode(WebPushEncryption.PublicPoint(p));
        var header = Vapid.Authorization(new Uri("https://fcm.googleapis.com/fcm/send/abc"), "mailto:ops@pishkhan.ir", publicKey, Base64Url.Encode(p.D), DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        header.ShouldStartWith("vapid t=");
        header.ShouldEndWith(", k=" + publicKey);
        var jwt = header["vapid t=".Length..header.IndexOf(',', StringComparison.Ordinal)].Split('.');
        var claims = JsonDocument.Parse(Base64Url.Decode(jwt[1])).RootElement;
        claims.GetProperty("aud").GetString().ShouldBe("https://fcm.googleapis.com");
        claims.GetProperty("exp").GetInt64().ShouldBe(1_700_000_000 + 12 * 3600);
        claims.GetProperty("sub").GetString().ShouldBe("mailto:ops@pishkhan.ir");
        key.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), Base64Url.Decode(jwt[2]), HashAlgorithmName.SHA256).ShouldBeTrue();
    }
}
