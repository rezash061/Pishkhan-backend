using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;

namespace Pishkhan.UnitTests;

/// <summary>BE-1.8: the follow-gate node and the schedule_followup action.</summary>
public class FollowGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static FlowContext Context(bool? follows, bool windowOpen = true) =>
        new(Now, windowOpen, [], new Dictionary<string, string>(), new Dictionary<string, string?> { ["contact.first_name"] = "مریم" }, BusinessHours.AlwaysOpen, follows);

    private static FlowRun NewRun() => FlowRun.Start(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), ChannelKinds.Instagram, "u1", null, Now);

    /// <summary>Gate → (followed) the discount code; (not followed after one retry) a polite goodbye.</summary>
    private static FlowGraph GateFlow(int maxRetries = 1) => new(
        "gate",
        [
            new FlowNode { Id = "gate", Type = FlowNodeTypes.FollowGate, Message = new OutboundContent("{{contact.first_name}} جان، اول پیج را دنبال کن"), RetryText = "هنوز دنبال نکرده‌ای 🙂", MaxRetries = maxRetries },
            new FlowNode { Id = "code", Type = FlowNodeTypes.Message, Message = new OutboundContent("کد تخفیف: OFF20") },
            new FlowNode { Id = "bye", Type = FlowNodeTypes.Message, Message = new OutboundContent("هر وقت دنبال کردی دوباره پیام بده") },
        ],
        [new FlowEdge("gate", "code", FlowHandles.True), new FlowEdge("gate", "bye", FlowHandles.False)]);

    private static string? TextOf(FlowEffect effect) => (effect as FlowEffect.Send)?.Content.Text;

    [Fact]
    public void The_gate_is_a_valid_node_with_true_and_false_exits()
    {
        FlowValidator.Validate(GateFlow()).IsValid.ShouldBeTrue();

        var noMessage = new FlowGraph("g", [new FlowNode { Id = "g", Type = FlowNodeTypes.FollowGate, MaxRetries = 9 }], []);
        var codes = FlowValidator.Validate(noMessage).Errors.Select(e => e.Code).ToList();
        codes.ShouldContain("flow.message_required");
        codes.ShouldContain("flow.max_retries_invalid");
    }

    [Fact]
    public void A_follower_passes_straight_through()
    {
        var run = NewRun();
        var effects = FlowRunner.Start(GateFlow(), run, Context(follows: true));
        effects.Select(TextOf).ShouldBe(["کد تخفیف: OFF20"]);
        run.Status.ShouldBe(FlowRunStatuses.Completed);
    }

    [Fact]
    public void An_unknown_follow_status_does_not_block_the_customer()
    {
        var run = NewRun();
        FlowRunner.Start(GateFlow(), run, Context(follows: null)).Select(TextOf).ShouldBe(["کد تخفیف: OFF20"]);
    }

    [Fact]
    public void A_non_follower_is_asked_and_checked_again_on_reply()
    {
        var run = NewRun();
        var ask = FlowRunner.Start(GateFlow(), run, Context(follows: false)).Single().ShouldBeOfType<FlowEffect.Send>();
        ask.Content.Text.ShouldBe("مریم جان، اول پیج را دنبال کن");
        ask.Content.QuickReplies!.Single().Payload.ShouldBe(FlowRunner.FollowCheckPayload);
        run.Status.ShouldBe(FlowRunStatuses.WaitingInput);

        // Still not following: the retry text, still waiting.
        FlowRunner.Answer(GateFlow(), run, null, FlowRunner.FollowCheckPayload, Context(follows: false)).Select(TextOf).ShouldBe(["هنوز دنبال نکرده‌ای 🙂"]);
        run.Status.ShouldBe(FlowRunStatuses.WaitingInput);

        // Followed now: the gate opens.
        FlowRunner.Answer(GateFlow(), run, "دنبال کردم", null, Context(follows: true)).Select(TextOf).ShouldBe(["کد تخفیف: OFF20"]);
        run.Status.ShouldBe(FlowRunStatuses.Completed);
    }

    [Fact]
    public void After_the_retries_the_run_leaves_on_false()
    {
        var run = NewRun();
        FlowRunner.Start(GateFlow(maxRetries: 1), run, Context(follows: false));
        FlowRunner.Answer(GateFlow(maxRetries: 1), run, "ok", null, Context(follows: false));
        FlowRunner.Answer(GateFlow(maxRetries: 1), run, "ok", null, Context(follows: false)).Select(TextOf).ShouldBe(["هر وقت دنبال کردی دوباره پیام بده"]);
        run.Status.ShouldBe(FlowRunStatuses.Completed);
    }

    private static RuleAction FollowUp(int? delay = 3600, string? text = "هنوز به این مدل فکر می‌کنی؟") =>
        new() { Kind = ActionKinds.ScheduleFollowUp, DelaySeconds = delay, Message = text is null ? null : new OutboundContent(text) };

    [Fact]
    public void A_follow_up_needs_a_message_and_a_delay_inside_the_window()
    {
        FollowUps.Validate(FollowUp()).ShouldBeNull();
        FollowUps.Validate(FollowUp(delay: 30)).ShouldBe(("delaySeconds", "automation.followup_delay_invalid"));
        FollowUps.Validate(FollowUp(delay: 24 * 3600)).ShouldBe(("delaySeconds", "automation.followup_delay_invalid"));
        FollowUps.Validate(FollowUp(delay: null)).ShouldBe(("delaySeconds", "automation.followup_delay_invalid"));
        FollowUps.Validate(FollowUp(text: null)).ShouldBe(("message", "validation.message.empty"));
    }

    [Fact]
    public void Rules_accept_one_follow_up_but_comment_rules_cannot_schedule_one()
    {
        var dm = new RuleDefinition("پیگیری", null, TriggerKinds.DmKeyword, new TriggerConfig { Keywords = ["قیمت"] }, [FollowUp()], 0, true);
        RuleValidator.Validate(dm).ShouldBeEmpty();
        RuleValidator.Validate(dm with { Actions = [FollowUp(), FollowUp()] }).ShouldContain(("actions", "automation.one_followup"));
        RuleValidator.Validate(dm with { Actions = [FollowUp(delay: 10)] }).ShouldContain(("actions[0].delaySeconds", "automation.followup_delay_invalid"));

        var comment = dm with { TriggerKind = TriggerKinds.CommentKeyword };
        RuleValidator.Validate(comment).ShouldContain(("actions[0]", "automation.action_needs_conversation"));
    }

    [Fact]
    public void A_follow_up_completes_once_and_defaults_to_cancel_on_reply_and_purchase()
    {
        var followUp = Pishkhan.Modules.Automation.Domain.FollowUp.Schedule(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ChannelKinds.Instagram, "u1", "rule:x", Guid.NewGuid(), null, new OutboundContent("سلام"), FollowUp(), Now);
        followUp.DueAt.ShouldBe(Now.AddHours(1));
        followUp.CancelOnReply.ShouldBeTrue();
        followUp.CancelOnPurchase.ShouldBeTrue();
        followUp.Complete(FollowUpStatuses.Sent, null, Now).ShouldBeTrue();
        followUp.Complete(FollowUpStatuses.Cancelled, FollowUpReasons.Replied, Now).ShouldBeFalse();
        followUp.Status.ShouldBe(FollowUpStatuses.Sent);
    }
}
