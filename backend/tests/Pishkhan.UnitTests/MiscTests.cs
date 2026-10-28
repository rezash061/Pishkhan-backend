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
