using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Channels.Instagram;

namespace Pishkhan.UnitTests;

public class InstagramSignatureTests
{
    private const string Secret = "app-secret";

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var body = Encoding.UTF8.GetBytes("""{"object":"instagram","entry":[]}""");
        InstagramSignatures.VerifyWebhook(body, InstagramSignatures.Sign(body, Secret), Secret).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abc")]
    [InlineData("sha256=zz")]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    public void Invalid_signatures_are_rejected(string? header)
    {
        InstagramSignatures.VerifyWebhook("{}"u8, header, Secret).ShouldBeFalse();
    }

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var signature = InstagramSignatures.Sign("{\"a\":1}"u8, Secret);
        InstagramSignatures.VerifyWebhook("{\"a\":2}"u8, signature, Secret).ShouldBeFalse();
    }

    [Fact]
    public void Empty_app_secret_never_verifies()
    {
        InstagramSignatures.VerifyWebhook("{}"u8, InstagramSignatures.Sign("{}"u8, string.Empty), string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void Signed_request_round_trips_and_rejects_tampering()
    {
        var payload = Base64Url.Encode(Encoding.UTF8.GetBytes("""{"algorithm":"HMAC-SHA256","user_id":"123"}"""));
        var signature = Base64Url.Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.ASCII.GetBytes(payload)));

        var parsed = InstagramSignatures.ParseSignedRequest($"{signature}.{payload}", Secret);
        parsed.ShouldNotBeNull();
        parsed.Value.GetProperty("user_id").GetString().ShouldBe("123");

        InstagramSignatures.ParseSignedRequest($"{signature}.{payload}x", Secret).ShouldBeNull();
        InstagramSignatures.ParseSignedRequest("garbage", Secret).ShouldBeNull();
        InstagramSignatures.ParseSignedRequest($"{signature}.{payload}", "other-secret").ShouldBeNull();
    }
}

public class InstagramWebhookParserTests
{
    private readonly InstagramWebhookParser _parser = new();

    private static JsonElement Entry(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parses_text_attachment_story_reply_and_postback()
    {
        var entry = Entry("""
            {"id":"1","time":1,"messaging":[
              {"sender":{"id":"u1"},"recipient":{"id":"1"},"timestamp":1730000000000,"message":{"mid":"m1","text":"سلام"}},
              {"sender":{"id":"u1"},"recipient":{"id":"1"},"timestamp":1730000000001,"message":{"mid":"m2","attachments":[{"type":"image","payload":{"url":"https://scontent.cdninstagram.com/x.jpg"}}]}},
              {"sender":{"id":"u1"},"recipient":{"id":"1"},"timestamp":1730000000002,"message":{"mid":"m3","text":"چنده؟","reply_to":{"story":{"id":"s1","url":"https://x"}}}},
              {"sender":{"id":"u1"},"recipient":{"id":"1"},"timestamp":1730000000003,"postback":{"mid":"m4","title":"خرید","payload":"BUY_1"}},
              {"sender":{"id":"1"},"recipient":{"id":"u1"},"timestamp":1730000000004,"message":{"mid":"m5","text":"echo","is_echo":true}},
              {"sender":{"id":"u1"},"recipient":{"id":"1"},"timestamp":1730000000005,"message":{"mid":"m6","is_deleted":true}},
              {"sender":{"id":"u1"},"recipient":{"id":"1"},"timestamp":1730000000006,"read":{"mid":"m1"}}
            ]}
            """);

        var messages = _parser.ParseInstagramEntry(entry);

        messages.Select(m => (m.ExternalMessageId, m.Kind)).ShouldBe([("m1", "text"), ("m2", "attachment"), ("m3", "story_reply"), ("m4", "postback"), ("m5", "text"), ("m1", "read")]);
        messages[1].Attachments.Single().Url.ShouldBe("https://scontent.cdninstagram.com/x.jpg");
        messages[2].ReplyToStoryId.ShouldBe("s1");
        messages[3].Postback.ShouldBe("BUY_1");
        messages[4].IsEcho.ShouldBeTrue();
        messages[0].SentAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1730000000000));
    }

    [Theory]
    [InlineData("""{"id":"1"}""")]
    [InlineData("""{"id":"1","messaging":"nope"}""")]
    [InlineData("""{"id":"1","messaging":[{"message":{"mid":"x"}}]}""")]
    [InlineData("""{"id":"1","changes":[{"field":"comments","value":{}}]}""")]
    public void Unknown_shapes_are_skipped(string json)
    {
        _parser.ParseInstagramEntry(Entry(json)).ShouldBeEmpty();
    }
}

public class InstagramErrorTests
{
    [Theory]
    [InlineData(400, 190, null, InstagramErrorKind.InvalidToken, false)]
    [InlineData(400, 4, null, InstagramErrorKind.RateLimited, true)]
    [InlineData(400, 613, null, InstagramErrorKind.RateLimited, true)]
    [InlineData(400, 10, 2534022, InstagramErrorKind.OutsideWindow, false)]
    [InlineData(400, 551, null, InstagramErrorKind.RecipientUnavailable, false)]
    [InlineData(500, 2, null, InstagramErrorKind.Transient, true)]
    [InlineData(503, null, null, InstagramErrorKind.Transient, true)]
    [InlineData(429, null, null, InstagramErrorKind.RateLimited, true)]
    [InlineData(400, 100, null, InstagramErrorKind.Rejected, false)]
    public void Errors_are_classified(int status, int? code, int? subcode, InstagramErrorKind kind, bool retryable)
    {
        var ex = new InstagramApiException((HttpStatusCode)status, code, subcode, "x");
        ex.Kind.ShouldBe(kind);
        ex.Retryable.ShouldBe(retryable);
    }

    [Fact]
    public void Error_body_is_read()
    {
        var body = JsonDocument.Parse("""{"error":{"message":"Invalid OAuth access token","type":"OAuthException","code":190,"error_subcode":463}}""").RootElement;
        var ex = InstagramApiException.From(HttpStatusCode.BadRequest, body);
        ex.Code.ShouldBe(190);
        ex.Subcode.ShouldBe(463);
        ex.ErrorCode.ShouldBe("instagram.token_invalid");
    }
}
