using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Billing.Domain;

namespace Pishkhan.UnitTests;

/// <summary>BE-2.8: whether the AI may take a new conversation, and what overage costs.</summary>
public class OverageTests
{
    private const long Price = 50_000;
    private const long Cap = 1_000_000;

    [Fact]
    public void Within_the_plan_or_unlimited_the_ai_answers()
    {
        OverageRules.Decide(100, 99, false, Price, true, Cap, false).ShouldBe(AiAllowance.Within);
        OverageRules.Decide(null, 10_000, false, null, false, 0, true).ShouldBe(AiAllowance.Within);
    }

    [Fact]
    public void A_conversation_already_counted_always_continues()
    {
        OverageRules.Decide(100, 500, true, null, false, 0, true).ShouldBe(AiAllowance.Within);
    }

    [Fact]
    public void Past_the_plan_with_overage_the_conversation_is_charged()
    {
        var allowance = OverageRules.Decide(100, 100, false, Price, true, Cap, false);
        allowance.Allowed.ShouldBeTrue();
        allowance.Overage.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0L, true)]
    [InlineData(Price, false)]
    public void Without_an_overage_price_or_with_overage_off_it_goes_to_a_person(long? price, bool enabled)
    {
        OverageRules.Decide(100, 100, false, price, enabled, Cap, false).ShouldBe(AiAllowance.Refused(AiAllowanceReasons.PlanLimit));
    }

    [Fact]
    public void The_cap_stops_overage_at_the_conversation_that_would_pass_it()
    {
        // 20 extra conversations at 50,000 make exactly the 1,000,000 cap; the 21st would pass it.
        OverageRules.Decide(100, 119, false, Price, true, Cap, false).Overage.ShouldBeTrue();
        OverageRules.Decide(100, 120, false, Price, true, Cap, false).ShouldBe(AiAllowance.Refused(AiAllowanceReasons.OverageCap));
        OverageRules.Decide(100, 100, false, Price, true, 0, false).ShouldBe(AiAllowance.Refused(AiAllowanceReasons.OverageCap));
    }

    [Fact]
    public void Unpaid_overage_stops_new_overage()
    {
        OverageRules.Decide(100, 100, false, Price, true, Cap, true).ShouldBe(AiAllowance.Refused(AiAllowanceReasons.OverageUnpaid));
        OverageRules.Decide(100, 50, false, Price, true, Cap, true).ShouldBe(AiAllowance.Within);
    }

    [Fact]
    public void A_charge_never_passes_the_cap()
    {
        OverageRules.Charge(10, Price, Cap).ShouldBe(500_000);
        OverageRules.Charge(25, Price, Cap).ShouldBe(Cap);
        OverageRules.DefaultCap(2_990_000).ShouldBe(2_990_000);
        OverageRules.DefaultCap(-1).ShouldBe(0);
    }
}
