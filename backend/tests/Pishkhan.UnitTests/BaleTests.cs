using System.Text.Json;
using Pishkhan.Modules.Channels.Bale;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Conversations.Services;

namespace Pishkhan.UnitTests;

/// <summary>BE-2.7 part 2: Bale updates in, messages out, webhook signatures and link tokens.</summary>
public class BaleTests
{
    private static readonly InstagramWebhookParser Parser = new();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_private_text_message_becomes_an_inbound_message_from_the_chat()
    {
        var m = Parser.ParseBaleUpdate(Json("""{"update_id":7,"message":{"message_id":42,"date":1793750000,"chat":{"id":123456,"type":"private"},"text":"سلام قیمت؟"}}"""), "999")!;
        m.SenderExternalId.ShouldBe("123456");
        m.RecipientExternalId.ShouldBe("999");
        m.ExternalMessageId.ShouldBe("123456:42");
        m.Kind.ShouldBe(InboundKinds.Text);
        m.Text.ShouldBe("سلام قیمت؟");
        m.SentAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1793750000));
    }

    [Fact]
    public void A_button_press_comes_back_as_a_postback()
    {
        var m = Parser.ParseBaleUpdate(Json("""{"callback_query":{"id":"cb1","from":{"id":123456},"data":"choice:1"}}"""), "999")!;
        m.Kind.ShouldBe(InboundKinds.Postback);
        m.Postback.ShouldBe("choice:1");
        m.SenderExternalId.ShouldBe("123456");
    }

    [Theory]
    [InlineData("""{"message":{"message_id":1,"chat":{"id":-100,"type":"group"},"text":"hi"}}""")]
    [InlineData("""{"edited_message":{"message_id":1,"chat":{"id":1,"type":"private"},"text":"hi"}}""")]
    [InlineData("""{"message":{"message_id":1,"chat":{"id":1,"type":"private"}}}""")]
    [InlineData("[]")]
    public void Groups_edits_and_empty_updates_are_ignored(string json) => Parser.ParseBaleUpdate(Json(json), "999").ShouldBeNull();

    [Fact]
    public void Quick_replies_and_buttons_become_an_inline_keyboard()
    {
        var content = new OutboundContent(
            "چه سایزی؟", QuickReplies: [new QuickReply("M", "choice:0")], Buttons: [MessageButton.Link("سایت", new Uri("https://shop.example"))]);
        var keyboard = BaleBotClient.Keyboard(content)!["inline_keyboard"]!.AsArray();
        keyboard.Count.ShouldBe(2);
        keyboard[0]![0]!["callback_data"]!.GetValue<string>().ShouldBe("choice:0");
        keyboard[1]![0]!["url"]!.GetValue<string>().ShouldBe("https://shop.example/");
        BaleBotClient.Keyboard(new OutboundContent("سلام")).ShouldBeNull();
    }

    [Fact]
    public void Cards_are_written_as_text_lines()
    {
        var content = new OutboundContent(null, Cards: [new MessageCard("مانتو کتان", "۴۵۰٬۰۰۰ تومان", null, null, null), new MessageCard("شال", null, null, null, null)]);
        BaleBotClient.Text(content).ShouldBe("• مانتو کتان\n۴۵۰٬۰۰۰ تومان\n\n• شال");
    }

    [Fact]
    public void Webhook_signatures_are_per_account_and_secret()
    {
        var account = Guid.NewGuid();
        var signature = BaleBotClient.Signature(account, "secret-a");
        signature.Length.ShouldBe(40);
        BaleBotClient.Signature(account, "secret-b").ShouldNotBe(signature);
        BaleBotClient.Signature(Guid.NewGuid(), "secret-a").ShouldNotBe(signature);
    }

    [Fact]
    public void Link_tokens_are_url_safe_and_read_from_the_start_command()
    {
        var token = ContactLinkToken.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        token.Token.Length.ShouldBe(22);
        token.Token.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
        ContactLinkToken.FromStart("/start " + token.Token).ShouldBe(token.Token);
        ContactLinkToken.FromStart("/start").ShouldBeNull();
        ContactLinkToken.FromStart("/start short").ShouldBeNull();
        ContactLinkToken.FromStart("سلام").ShouldBeNull();
    }
}
