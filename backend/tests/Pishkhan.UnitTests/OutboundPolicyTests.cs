using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Services;

namespace Pishkhan.UnitTests;

public class OutboundPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static Conversation WithLastInbound(TimeSpan ago)
    {
        var conversation = Conversation.Open(Guid.NewGuid(), Guid.NewGuid(), ChannelKinds.Instagram, Guid.NewGuid(), "u1", Now.AddDays(-30));
        conversation.RecordInbound(Now - ago, "سلام");
        return conversation;
    }

    private static Message Outbound(Conversation c, string handledBy, Guid? sentBy, string? comment = null) =>
        Message.QueueOutbound(c.WorkspaceId, c.Id, new OutboundContent("پاسخ"), handledBy, sentBy, Now, comment);

    [Theory]
    [InlineData(1, HandledBy.Rule, false, true, null)]
    [InlineData(23, HandledBy.Ai, false, true, null)]
    [InlineData(25, HandledBy.Human, true, true, MessageTags.HumanAgent)]
    [InlineData(24 * 7 - 1, HandledBy.Human, true, true, MessageTags.HumanAgent)]
    [InlineData(24 * 7 + 1, HandledBy.Human, true, false, null)]
    [InlineData(25, HandledBy.Ai, false, false, null)]
    [InlineData(25, HandledBy.Rule, false, false, null)]
    [InlineData(25, HandledBy.Human, false, false, null)]
    public void Window_and_human_agent_rules(int hoursAgo, string handledBy, bool hasSender, bool allowed, string? tag)
    {
        var conversation = WithLastInbound(TimeSpan.FromHours(hoursAgo));
        var decision = OutboundPolicy.Check(conversation, Outbound(conversation, handledBy, hasSender ? Guid.NewGuid() : null), Now);

        decision.Allowed.ShouldBe(allowed);
        decision.Tag.ShouldBe(tag);
        if (!allowed)
        {
            decision.ErrorCode.ShouldBe("conversation.window_closed");
        }
    }

    [Fact]
    public void A_private_reply_ignores_the_dm_window()
    {
        var conversation = WithLastInbound(TimeSpan.FromDays(10));
        OutboundPolicy.Check(conversation, Outbound(conversation, HandledBy.Rule, null, "c1"), Now).ShouldBe(OutboundDecision.Allow(null));
    }

    [Fact]
    public void Rich_content_round_trips_and_plain_text_stores_no_json()
    {
        var c = WithLastInbound(TimeSpan.FromHours(1));
        var rich = new OutboundContent("سایز؟", QuickReplies: [new QuickReply("S", "SIZE_S")], Buttons: null);
        var message = Message.QueueOutbound(c.WorkspaceId, c.Id, rich, HandledBy.Human, Guid.NewGuid(), Now);

        message.ContentJson.ShouldNotBeNull();
        var content = message.ToContent(MessageTags.HumanAgent);
        content.QuickReplies!.Single().Payload.ShouldBe("SIZE_S");
        content.Tag.ShouldBe(MessageTags.HumanAgent);

        var plain = Message.QueueOutbound(c.WorkspaceId, c.Id, "سلام", HandledBy.Rule, null, Now);
        plain.ContentJson.ShouldBeNull();
        plain.ToContent(null).ShouldBe(new OutboundContent("سلام"));

        var cards = Message.QueueOutbound(c.WorkspaceId, c.Id, new OutboundContent(null, Cards: [new MessageCard("مانتو", null, null, null, null)]), HandledBy.Rule, null, Now);
        cards.Kind.ShouldBe("cards");
        cards.Text.ShouldBe("مانتو");
    }

    [Fact]
    public void Status_assignment_and_ai_pause_report_changes()
    {
        var c = WithLastInbound(TimeSpan.FromHours(1));
        var user = Guid.NewGuid();

        c.ChangeStatus("archived").ShouldBeFalse();
        c.ChangeStatus(ConversationStatuses.HandedOff).ShouldBeTrue();
        c.ChangeStatus(ConversationStatuses.HandedOff).ShouldBeFalse();
        c.Assign(user).ShouldBeTrue();
        c.Assign(user).ShouldBeFalse();
        c.SetAiPaused(true).ShouldBeTrue();
        c.SetAiPaused(true).ShouldBeFalse();
        c.IsHumanAgentWindowOpen(Now).ShouldBeTrue();
    }
}
