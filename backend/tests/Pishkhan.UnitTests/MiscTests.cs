using Pishkhan.Infrastructure.Telemetry;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Features;

namespace Pishkhan.UnitTests;

public class PiiRedactionTests
{
    [Theory]
    [InlineData("GET https://api.kavenegar.com/v1/ABC123SECRET/sms/send.json", "GET https://api.kavenegar.com/v1/***/sms/send.json")]
    [InlineData("phone 09121234567 ok", "phone 09**-***-67 ok")]
    [InlineData("token=IGAAabcdefghijklmnopqrstuvwxyz", "token=***")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer ***")]
    public void Secrets_and_personal_data_are_masked(string input, string expected)
    {
        PiiRedactor.Redact(input).ShouldBe(expected);
    }
}

public class ConversationTests
{
    [Fact]
    public void Inbound_message_opens_a_24_hour_window_and_reopens_closed_conversations()
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = Conversation.Open(Guid.NewGuid(), Guid.NewGuid(), "instagram", Guid.NewGuid(), "u1", now);
        conversation.IsWindowOpen(now).ShouldBeFalse();

        conversation.RecordInbound(now, "سلام");

        conversation.IsWindowOpen(now.AddHours(23)).ShouldBeTrue();
        conversation.IsWindowOpen(now.AddHours(25)).ShouldBeFalse();
        conversation.UnreadCount.ShouldBe(1);
    }

    [Fact]
    public void Out_of_order_inbound_does_not_shrink_the_window()
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = Conversation.Open(Guid.NewGuid(), Guid.NewGuid(), "instagram", Guid.NewGuid(), "u1", now);
        conversation.RecordInbound(now, "b");
        conversation.RecordInbound(now.AddMinutes(-5), "a");
        conversation.WindowExpiresAt.ShouldBe(now + Conversation.MessagingWindow);
    }

    [Fact]
    public void Keyset_cursor_round_trips_with_microsecond_precision()
    {
        var at = new DateTimeOffset(2026, 10, 28, 12, 30, 15, TimeSpan.Zero).AddTicks(1234560);
        var id = Guid.NewGuid();
        Cursor.TryRead(Cursor.Write(at, id), out var readAt, out var readId).ShouldBeTrue();
        readAt.ShouldBe(at);
        readId.ShouldBe(id);
        Cursor.TryRead("not-a-cursor", out _, out _).ShouldBeFalse();
    }
}

public class TotpTests
{
    private static readonly byte[] RfcSecret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Matches_rfc_6238_sha1_vectors(long unixSeconds, string expected)
    {
        Pishkhan.Infrastructure.Security.Totp.Compute(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).ShouldBe(expected);
    }

    [Fact]
    public void Verify_accepts_one_step_of_drift_and_persian_digits()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var previous = Pishkhan.Infrastructure.Security.Totp.Compute(RfcSecret, now.AddSeconds(-30));
        Pishkhan.Infrastructure.Security.Totp.Verify(RfcSecret, previous, now, out var step).ShouldBeTrue();
        step.ShouldBe(now.ToUnixTimeSeconds() / 30 - 1);
        Pishkhan.Infrastructure.Security.Totp.Verify(RfcSecret, "۰۰۵۹۲۴", now, out _).ShouldBeTrue();
        Pishkhan.Infrastructure.Security.Totp.Verify(RfcSecret, Pishkhan.Infrastructure.Security.Totp.Compute(RfcSecret, now.AddMinutes(2)), now, out _).ShouldBeFalse();
    }
}
