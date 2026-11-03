using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Conversations.Services;

namespace Pishkhan.UnitTests;

/// <summary>BE-2.7: the outage detector, who a campaign may text, and the capture_phone action.</summary>
public class OutageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);
    private static readonly OutageOptions Options = new();

    [Fact]
    public void A_simulated_instagram_outage_is_detected_and_ends_after_recovery()
    {
        var status = PlatformChannelStatus.New(ChannelKinds.Instagram, T0);

        // Normal traffic with a few errors: still up.
        status.Evaluate(new CallCounts(Ok: 300, Failed: 10), Options, T0).ShouldBeFalse();
        status.Status.ShouldBe(OutageStatuses.Up);

        // Most calls fail: down.
        status.Evaluate(new CallCounts(Ok: 10, Failed: 90), Options, T0.AddMinutes(1)).ShouldBeTrue();
        status.Status.ShouldBe(OutageStatuses.Down);
        status.Since.ShouldBe(T0.AddMinutes(1));

        // Calls succeed again, but not for long enough: stays down.
        status.Evaluate(new CallCounts(Ok: 50, Failed: 1), Options, T0.AddMinutes(2)).ShouldBeFalse();
        status.Status.ShouldBe(OutageStatuses.Down);

        // After the minimum outage time: up.
        status.Evaluate(new CallCounts(Ok: 50, Failed: 1), Options, T0.AddMinutes(5)).ShouldBeTrue();
        status.Status.ShouldBe(OutageStatuses.Up);
    }

    [Fact]
    public void A_few_errors_on_a_quiet_night_are_not_an_outage()
    {
        var status = PlatformChannelStatus.New(ChannelKinds.Instagram, T0);
        status.Evaluate(new CallCounts(Ok: 0, Failed: 5), Options, T0).ShouldBeFalse();
        status.Evaluate(default, Options, T0).ShouldBeFalse();
        status.Status.ShouldBe(OutageStatuses.Up);
    }

    [Fact]
    public void Staff_can_force_the_status_either_way()
    {
        var status = PlatformChannelStatus.New(ChannelKinds.Instagram, T0);
        status.SetMode(OutageModes.ForcedDown, "فیلترینگ گسترده", Guid.NewGuid(), T0);
        status.Evaluate(new CallCounts(Ok: 1_000, Failed: 0), Options, T0).ShouldBeTrue();
        status.Status.ShouldBe(OutageStatuses.Down);

        status.SetMode(OutageModes.ForcedUp, "هشدار اشتباه", Guid.NewGuid(), T0);
        status.Evaluate(new CallCounts(Ok: 0, Failed: 1_000), Options, T0).ShouldBeTrue();
        status.Status.ShouldBe(OutageStatuses.Up);
    }

    [Theory]
    [InlineData("09121234567", true, true)]
    [InlineData("+989121234567", true, true)]
    [InlineData("09121234567", false, false)]
    [InlineData("09121234567", null, false)]
    [InlineData("12345", true, false)]
    public void Only_contacts_whose_latest_sms_consent_is_a_grant_and_who_have_a_mobile_are_texted(string phone, bool? consent, bool reachable)
    {
        var fields = new Dictionary<string, string> { ["phone"] = phone };
        (CampaignAudience.Reachable(fields, consent) is not null).ShouldBe(reachable);
    }

    [Fact]
    public void A_contact_without_a_phone_is_never_texted() =>
        CampaignAudience.Reachable(new Dictionary<string, string>(), true).ShouldBeNull();

    private static FlowGraph CaptureFlow(RuleAction action) => new(
        "a", [new FlowNode { Id = "a", Type = FlowNodeTypes.Action, Actions = [action] }], []);

    [Fact]
    public void Capture_phone_needs_the_number_and_the_consent_text_and_only_runs_in_flows()
    {
        var ok = new RuleAction { Kind = ActionKinds.CapturePhone, Value = "{{flow.phone}}", ConsentText = "اگر اینستاگرام قطع شد با پیامک خبرتان کنیم؟" };
        FlowValidator.Validate(CaptureFlow(ok)).IsValid.ShouldBeTrue();
        FlowValidator.Validate(CaptureFlow(ok with { ConsentText = null })).Errors.Select(e => e.Code).ShouldContain("flow.capture_phone_invalid");
        FlowValidator.Validate(CaptureFlow(ok with { Value = " " })).Errors.Select(e => e.Code).ShouldContain("flow.capture_phone_invalid");

        var rule = new RuleDefinition("شماره", null, TriggerKinds.DmKeyword, new TriggerConfig { Keywords = ["شماره"] }, [ok], 0, true);
        RuleValidator.Validate(rule).ShouldContain(("actions[0]", "automation.capture_phone_flow_only"));
    }
}
