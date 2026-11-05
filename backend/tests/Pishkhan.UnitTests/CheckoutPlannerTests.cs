using Pishkhan.Modules.Billing.Domain;

namespace Pishkhan.UnitTests;

/// <summary>BE-2.8 checkout: what buying a plan costs and when it starts, against the workspace's current subscription.</summary>
public class CheckoutPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();

    private static Plan PlanOf(string code, long monthly, long? annual = null, bool isPublic = true, bool isFree = false, bool publish = true)
    {
        var plan = Plan.Create(
            code,
            new PlanDraft(code, null, null, monthly, annual, null, new Dictionary<string, int?>(), [], isPublic, isFree, false, 0),
            Guid.NewGuid(),
            Now.AddDays(-100));
        if (publish)
        {
            plan.Publish(Guid.NewGuid(), Now.AddDays(-100));
        }

        return plan;
    }

    private static Subscription Bought(Plan plan, long paid, DateTimeOffset startsAt, string cycle = BillingCycles.Monthly) =>
        Subscription.Purchase(Ws, plan, cycle, paid, Guid.NewGuid(), Guid.NewGuid(), startsAt, startsAt);

    private static readonly Plan Basic = PlanOf("basic", 5_000_000, 50_000_000);
    private static readonly Plan Pro = PlanOf("pro", 10_000_000);

    [Fact]
    public void Without_a_paid_plan_it_is_a_new_purchase_from_now()
    {
        var (quote, error) = CheckoutPlanner.Quote(Basic, BillingCycles.Annual, null, null, [], Now);
        error.ShouldBeNull();
        quote!.Kind.ShouldBe(CheckoutKinds.New);
        quote.Amount.ShouldBe(50_000_000);
        quote.StartsAt.ShouldBe(Now);
        quote.EndsAt.ShouldBe(Now.AddDays(365));
    }

    [Fact]
    public void A_trial_is_replaced_by_a_new_purchase()
    {
        var trial = Subscription.Grant(Ws, Pro, trial: true, Now.AddDays(10), Guid.NewGuid(), Now.AddDays(-4));
        var (quote, _) = CheckoutPlanner.Quote(Basic, BillingCycles.Monthly, trial, Pro, [], Now);
        quote!.Kind.ShouldBe(CheckoutKinds.New);
        quote.Credit.ShouldBe(0);
        quote.ReplacesSubscriptionId.ShouldBe(trial.Id);
    }

    [Fact]
    public void The_same_plan_is_a_renewal_after_the_last_paid_period()
    {
        var current = Bought(Basic, 5_000_000, Now.AddDays(-10));
        var next = Bought(Basic, 5_000_000, current.EndsAt!.Value);
        var (quote, _) = CheckoutPlanner.Quote(Basic, BillingCycles.Monthly, current, Basic, [next], Now);
        quote!.Kind.ShouldBe(CheckoutKinds.Renewal);
        quote.StartsAt.ShouldBe(next.EndsAt!.Value);
        quote.Amount.ShouldBe(5_000_000);
    }

    [Fact]
    public void A_bigger_plan_is_an_upgrade_now_with_the_unused_part_credited()
    {
        // 10 of 30 days used: two thirds of 5,000,000 Rial left.
        var current = Bought(Basic, 5_000_000, Now.AddDays(-10));
        var (quote, _) = CheckoutPlanner.Quote(Pro, BillingCycles.Monthly, current, Basic, [], Now);
        quote!.Kind.ShouldBe(CheckoutKinds.Upgrade);
        quote.Credit.ShouldBe(3_333_333);
        quote.Amount.ShouldBe(10_000_000 - 3_333_333);
        quote.StartsAt.ShouldBe(Now);
        quote.ReplacesSubscriptionId.ShouldBe(current.Id);
    }

    [Fact]
    public void An_upgrade_never_costs_less_than_the_gateway_minimum()
    {
        // Paid 10,999,000 a minute ago for a plan now listed at 10,000,000: the credit almost covers the 11,000,000 plan.
        var current = Bought(Pro, 10_999_000, Now.AddMinutes(-1));
        var bigger = PlanOf("max", 11_000_000);
        var (quote, _) = CheckoutPlanner.Quote(bigger, BillingCycles.Monthly, current, Pro, [], Now);
        quote!.Kind.ShouldBe(CheckoutKinds.Upgrade);
        quote.Amount.ShouldBe(CheckoutPlanner.MinimumAmount);
    }

    [Fact]
    public void A_smaller_plan_is_a_downgrade_when_the_current_period_ends()
    {
        var current = Bought(Pro, 10_000_000, Now.AddDays(-5));
        var (quote, _) = CheckoutPlanner.Quote(Basic, BillingCycles.Monthly, current, Pro, [], Now);
        quote!.Kind.ShouldBe(CheckoutKinds.Downgrade);
        quote.StartsAt.ShouldBe(current.EndsAt!.Value);
        quote.Credit.ShouldBe(0);
    }

    [Fact]
    public void Refuses_what_cannot_be_sold()
    {
        CheckoutPlanner.Quote(PlanOf("draft", 1_000_000, publish: false), BillingCycles.Monthly, null, null, [], Now).Error.ShouldBe("billing.checkout.plan_not_for_sale");
        CheckoutPlanner.Quote(PlanOf("private", 1_000_000, isPublic: false), BillingCycles.Monthly, null, null, [], Now).Error.ShouldBe("billing.checkout.plan_not_for_sale");
        CheckoutPlanner.Quote(PlanOf("free", 0, isFree: true), BillingCycles.Monthly, null, null, [], Now).Error.ShouldBe("billing.checkout.plan_not_for_sale");
        CheckoutPlanner.Quote(Pro, BillingCycles.Annual, null, null, [], Now).Error.ShouldBe("billing.checkout.cycle_not_offered");

        var managed = Subscription.Grant(Ws, Basic, trial: false, null, Guid.NewGuid(), Now.AddDays(-1));
        CheckoutPlanner.Quote(Pro, BillingCycles.Monthly, managed, Basic, [], Now).Error.ShouldBe("billing.checkout.managed_subscription");

        var current = Bought(Basic, 5_000_000, Now.AddDays(-1));
        var next = Bought(Basic, 5_000_000, current.EndsAt!.Value);
        CheckoutPlanner.Quote(Pro, BillingCycles.Monthly, current, Basic, [next], Now).Error.ShouldBe("billing.checkout.scheduled_pending");
    }

    [Fact]
    public void Credit_is_the_unused_part_of_what_was_paid()
    {
        var sub = Bought(Basic, 3_000_000, Now.AddDays(-15));
        sub.UnusedCredit(Now).ShouldBe(1_500_000);
        sub.UnusedCredit(sub.EndsAt!.Value).ShouldBe(0);
        Subscription.Grant(Ws, Basic, trial: false, Now.AddDays(5), Guid.NewGuid(), Now).UnusedCredit(Now).ShouldBe(0);

        var scheduled = Bought(Basic, 3_000_000, Now.AddDays(5));
        scheduled.UnusedCredit(Now).ShouldBe(3_000_000);
    }

    private static CheckoutQuote Quote(long price, long credit = 0) =>
        new(CheckoutKinds.New, BillingCycles.Monthly, price, credit, Math.Max(price - credit, CheckoutPlanner.MinimumAmount), Now, Now.AddDays(30), null);

    private static Coupon CouponOf(string kind, long value) =>
        Coupon.Create("TEST-1", new CouponTerms(kind, value, null, [], false, null, 1, null, null, true), Guid.NewGuid(), Now);

    [Fact]
    public void Coupons_come_off_after_the_credit_and_the_wallet_pays_the_rest()
    {
        var q = Quote(5_000_000, credit: 1_000_000).Priced(CouponOf(CouponKinds.Percent, 25), wallet: 1_000_000);
        q.Discount.ShouldBe(1_000_000);
        q.WalletUsed.ShouldBe(1_000_000);
        q.Amount.ShouldBe(2_000_000);
        q.CouponCode.ShouldBe("TEST-1");

        Quote(5_000_000).Priced(CouponOf(CouponKinds.Fixed, 9_000_000), 0).Amount.ShouldBe(0);
        Quote(5_000_000).Priced(null, 9_000_000).ShouldSatisfyAllConditions(x => x.WalletUsed.ShouldBe(5_000_000), x => x.Amount.ShouldBe(0));
    }

    [Fact]
    public void What_Zarinpal_is_asked_for_is_zero_or_at_least_its_minimum()
    {
        // The wallet leaves the minimum rather than a few Toman.
        var q = Quote(5_000_000).Priced(null, 4_995_000);
        q.Amount.ShouldBe(CheckoutPlanner.MinimumAmount);
        q.WalletUsed.ShouldBe(5_000_000 - CheckoutPlanner.MinimumAmount);

        // A tiny remainder after a coupon is raised to the minimum, as without one.
        Quote(5_000_000).Priced(CouponOf(CouponKinds.Fixed, 4_995_000), 0).Amount.ShouldBe(CheckoutPlanner.MinimumAmount);

        // A fully credited upgrade still pays the minimum, as before.
        Quote(5_000_000, credit: 5_000_000).Priced(null, 0).Amount.ShouldBe(CheckoutPlanner.MinimumAmount);
    }

    [Fact]
    public void A_coupon_refuses_outside_its_terms()
    {
        var coupon = Coupon.Create("FIRST", new CouponTerms(CouponKinds.Percent, 10, null, ["pro"], true, 2, 1, Now.AddDays(-1), Now.AddDays(1), true), Guid.NewGuid(), Now);
        coupon.Refusal("pro", CheckoutKinds.New, paidBefore: false, 0, Now).ShouldBeNull();
        coupon.Refusal("basic", CheckoutKinds.New, false, 0, Now).ShouldBe("billing.coupon.not_for_plan");
        coupon.Refusal("pro", CheckoutKinds.Renewal, true, 0, Now).ShouldBe("billing.coupon.first_purchase_only");
        coupon.Refusal("pro", CheckoutKinds.New, false, 1, Now).ShouldBe("billing.coupon.already_used");
        coupon.Refusal("pro", CheckoutKinds.New, false, 0, Now.AddDays(2)).ShouldBe("billing.coupon.expired");
        coupon.Redeemed(Now);
        coupon.Redeemed(Now);
        coupon.Refusal("pro", CheckoutKinds.New, false, 0, Now).ShouldBe("billing.coupon.used_up");
        Coupon.Validate("ab", new CouponTerms("percent", 0, null, [], false, null, 1, null, null, true), true).Keys
            .ShouldBe(["code", "value"], ignoreOrder: true);
    }

    [Fact]
    public void A_wallet_never_goes_below_zero()
    {
        var wallet = Wallet.Open(Guid.NewGuid(), Now);
        wallet.Change(1_000, WalletEntryKinds.Adjustment, "x", null, WalletActors.Staff, null, Now).BalanceAfter.ShouldBe(1_000);
        Should.Throw<Pishkhan.SharedKernel.DomainException>(() => wallet.Change(-1_001, WalletEntryKinds.Checkout, null, null, WalletActors.User, null, Now));
        wallet.Balance.ShouldBe(1_000);
    }
}
