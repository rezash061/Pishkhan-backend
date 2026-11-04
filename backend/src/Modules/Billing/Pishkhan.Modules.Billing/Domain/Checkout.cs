using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Domain;

/// <summary>What a checkout buys, relative to the workspace's current subscription.</summary>
public static class CheckoutKinds
{
    /// <summary>No paid subscription now (free plan, a trial, or expired): starts at once.</summary>
    public const string New = "new";

    /// <summary>The same plan again: starts when the current period (and any already paid after it) ends.</summary>
    public const string Renewal = "renewal";

    /// <summary>A bigger plan: starts at once, with the unused part of the current period credited.</summary>
    public const string Upgrade = "upgrade";

    /// <summary>A smaller (or equally priced) plan: starts when the current period ends.</summary>
    public const string Downgrade = "downgrade";

    /// <summary>Pays the workspace's due overage charges (BE-2.8); buys no subscription. The plan fields name the current plan.</summary>
    public const string Overage = "overage";
}

public static class CheckoutStatuses
{
    public const string Pending = "pending";
    public const string Paid = "paid";
    public const string Failed = "failed";

    /// <summary>Not paid within <see cref="Checkout.Lifetime"/>.</summary>
    public const string Expired = "expired";
}

/// <summary>
/// One payment for a subscription, made to Pishkhan through Zarinpal (backend plan 9.13). The amount is fixed when the checkout is made
/// and is the only amount ever verified. A paid checkout is the invoice: it gets the next invoice number and the subscription it bought.
/// </summary>
public sealed class Checkout : AggregateRoot<Guid>, ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private Checkout()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Kind { get; private set; } = CheckoutKinds.New;

    public Guid PlanId { get; private set; }

    public string PlanCode { get; private set; } = string.Empty;

    public string PlanName { get; private set; } = string.Empty;

    public int PlanVersion { get; private set; }

    public string Cycle { get; private set; } = BillingCycles.Monthly;

    /// <summary>The plan's price for the cycle (Rial).</summary>
    public long Price { get; private set; }

    /// <summary>Unused part of the current period taken off an upgrade (Rial).</summary>
    public long Credit { get; private set; }

    /// <summary>What is paid: <see cref="Price"/> minus <see cref="Credit"/>, at least <see cref="CheckoutPlanner.MinimumAmount"/> (Rial).</summary>
    public long Amount { get; private set; }

    /// <summary>The subscription an upgrade replaces.</summary>
    public Guid? ReplacesSubscriptionId { get; private set; }

    public string Status { get; private set; } = CheckoutStatuses.Pending;

    public string? Authority { get; private set; }

    public string? ReferenceId { get; private set; }

    public string? CardMask { get; private set; }

    public string? Error { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>Sequential across Pishkhan, given when paid.</summary>
    public long? InvoiceNumber { get; private set; }

    public Guid? SubscriptionId { get; private set; }

    public static Checkout Create(Guid workspaceId, CheckoutQuote quote, Plan plan, Guid userId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Kind = quote.Kind,
        PlanId = plan.Id,
        PlanCode = plan.Code,
        PlanName = plan.Name,
        PlanVersion = plan.VersionNumber,
        Cycle = quote.Cycle,
        Price = quote.Price,
        Credit = quote.Credit,
        Amount = quote.Amount,
        ReplacesSubscriptionId = quote.ReplacesSubscriptionId,
        CreatedBy = userId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>A checkout for the due overage charges, <paramref name="amount"/> already at least the minimum.</summary>
    public static Checkout ForOverage(Guid workspaceId, long amount, Guid planId, string planCode, string planName, int planVersion, Guid userId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Kind = CheckoutKinds.Overage,
        PlanId = planId,
        PlanCode = planCode,
        PlanName = planName,
        PlanVersion = planVersion,
        Price = amount,
        Amount = amount,
        CreatedBy = userId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public string StatusAt(DateTimeOffset now) => Status == CheckoutStatuses.Pending && now - CreatedAt > Lifetime ? CheckoutStatuses.Expired : Status;

    public void Started(string authority, DateTimeOffset now)
    {
        Authority = authority;
        UpdatedAt = now;
    }

    public void Fail(string code, DateTimeOffset now)
    {
        if (Status != CheckoutStatuses.Pending)
        {
            return;
        }

        Status = CheckoutStatuses.Failed;
        Error = code;
        UpdatedAt = now;
    }

    public void MarkPaid(string referenceId, string? cardMask, long invoiceNumber, Guid? subscriptionId, DateTimeOffset now)
    {
        Status = CheckoutStatuses.Paid;
        ReferenceId = referenceId;
        CardMask = cardMask;
        InvoiceNumber = invoiceNumber;
        SubscriptionId = subscriptionId;
        PaidAt = now;
        Error = null;
        UpdatedAt = now;
    }
}

/// <summary>What buying a plan costs now, and when it would start.</summary>
public sealed record CheckoutQuote(
    string Kind, string Cycle, long Price, long Credit, long Amount, DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid? ReplacesSubscriptionId);

/// <summary>
/// Works out a checkout from the plan and the workspace's subscriptions (the current one and any already paid after it). Buying while a
/// staff grant without an end is current is refused; an upgrade is refused while a later period is already paid for.
/// </summary>
public static class CheckoutPlanner
{
    /// <summary>Zarinpal's smallest payment, 1,000 Toman.</summary>
    public const long MinimumAmount = 10_000;

    public static (CheckoutQuote? Quote, string? Error) Quote(
        Plan plan, string cycle, Subscription? current, Plan? currentPlan, IReadOnlyList<Subscription> scheduled, DateTimeOffset now)
    {
        if (plan.Status != PlanStatus.Published || !plan.IsPublic || plan.IsFree)
        {
            return (null, "billing.checkout.plan_not_for_sale");
        }

        var price = PriceOf(plan, cycle);
        if (price is not > 0)
        {
            return (null, "billing.checkout.cycle_not_offered");
        }

        var length = BillingCycles.Length(cycle);
        if (current is null || current.Trial || currentPlan is null)
        {
            return (new CheckoutQuote(CheckoutKinds.New, cycle, price.Value, 0, Math.Max(price.Value, MinimumAmount), now, now + length, current?.Id), null);
        }

        if (current.EndsAt is not { } currentEnd)
        {
            return (null, "billing.checkout.managed_subscription");
        }

        var lastEnd = scheduled.Select(s => s.EndsAt ?? currentEnd).Append(currentEnd).Max();
        if (plan.Code != current.PlanCode && Monthly(plan) > Monthly(currentPlan))
        {
            if (scheduled.Count > 0)
            {
                return (null, "billing.checkout.scheduled_pending");
            }

            var credit = Math.Min(current.UnusedCredit(now), price.Value);
            return (new CheckoutQuote(CheckoutKinds.Upgrade, cycle, price.Value, credit, Math.Max(price.Value - credit, MinimumAmount), now, now + length, current.Id), null);
        }

        var kind = plan.Code == current.PlanCode ? CheckoutKinds.Renewal : CheckoutKinds.Downgrade;
        return (new CheckoutQuote(kind, cycle, price.Value, 0, Math.Max(price.Value, MinimumAmount), lastEnd, lastEnd + length, null), null);
    }

    public static long? PriceOf(Plan plan, string cycle) => cycle == BillingCycles.Annual ? plan.AnnualPrice : plan.MonthlyPrice;

    /// <summary>A plan's price per month, to tell an upgrade from a downgrade.</summary>
    private static decimal Monthly(Plan plan) => plan.MonthlyPrice > 0 ? plan.MonthlyPrice : (plan.AnnualPrice ?? 0) / 12m;
}
