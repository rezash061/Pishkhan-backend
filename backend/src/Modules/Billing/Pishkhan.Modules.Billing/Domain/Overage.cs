using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Domain;

/// <summary>
/// A workspace's overage choices (BE-2.8, gap G31). Overage lets the AI keep answering new conversations after the plan's included ones,
/// each charged at the plan's overage price, up to <see cref="Cap"/> per billing period.
/// </summary>
public sealed class BillingSettings : ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public bool OverageEnabled { get; set; } = true;

    /// <summary>The most overage per period, in Rial. Null: the plan's monthly price (overage can at most double the bill).</summary>
    public long? Cap { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static BillingSettings Default(Guid workspaceId) => new() { WorkspaceId = workspaceId };
}

public static class OverageChargeStatuses
{
    public const string Due = "due";
    public const string Settled = "settled";
}

/// <summary>A closed period's overage: what is owed for the AI conversations beyond the plan's, paid with an <c>overage</c> checkout.</summary>
public sealed class OverageCharge : Entity<Guid>, ITenantOwned
{
    private OverageCharge()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public DateTimeOffset PeriodStart { get; private set; }

    public DateTimeOffset PeriodEnd { get; private set; }

    /// <summary>AI conversations beyond the plan's included ones.</summary>
    public int Conversations { get; private set; }

    public long UnitPrice { get; private set; }

    public long Amount { get; private set; }

    public string Status { get; private set; } = OverageChargeStatuses.Due;

    public Guid? CheckoutId { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public static OverageCharge Create(Guid workspaceId, BillingPeriod period, int conversations, long unitPrice, long amount, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        PeriodStart = period.Start,
        PeriodEnd = period.End,
        Conversations = conversations,
        UnitPrice = unitPrice,
        Amount = amount,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Settle(Guid checkoutId, DateTimeOffset now)
    {
        Status = OverageChargeStatuses.Settled;
        CheckoutId = checkoutId;
        SettledAt = now;
        UpdatedAt = now;
    }
}

/// <summary>A billing notice sent once (<see cref="Key"/>, for example <c>quota_used:2026-10-22</c>), so a repeated event never notifies twice.</summary>
public sealed class BillingAlert : ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public required string Key { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>The rule for whether the AI may take a conversation (BE-2.8). Pure, so it is tested on its own.</summary>
public static class OverageRules
{
    /// <summary>Overage charges unpaid this long after their period stop overage until paid (dunning).</summary>
    public static readonly TimeSpan UnpaidGrace = TimeSpan.FromDays(14);

    /// <summary>The cap when the workspace has not set one: the plan's monthly price.</summary>
    public static long DefaultCap(long monthlyPrice) => Math.Max(0, monthlyPrice);

    /// <param name="limit">The plan's included AI conversations; null is unlimited.</param>
    /// <param name="used">AI conversations counted this period.</param>
    /// <param name="alreadyCounted">This conversation was counted this period: it continues whatever the count.</param>
    /// <param name="price">The plan's price per extra conversation (Rial); null when the plan has no overage.</param>
    /// <param name="cap">The workspace's cap (Rial), already defaulted.</param>
    /// <param name="unpaid">An earlier period's overage is unpaid past the grace.</param>
    public static AiAllowance Decide(int? limit, long used, bool alreadyCounted, long? price, bool enabled, long cap, bool unpaid)
    {
        if (alreadyCounted || limit is not { } included || used < included)
        {
            return AiAllowance.Within;
        }

        if (price is not > 0 || !enabled)
        {
            return AiAllowance.Refused(AiAllowanceReasons.PlanLimit);
        }

        if (unpaid)
        {
            return AiAllowance.Refused(AiAllowanceReasons.OverageUnpaid);
        }

        var extra = used - included + 1;
        return extra * price.Value <= cap ? AiAllowance.InOverage : AiAllowance.Refused(AiAllowanceReasons.OverageCap);
    }

    /// <summary>What a period's extra conversations cost, never more than the cap (a race can let one through).</summary>
    public static long Charge(long extra, long price, long cap) => Math.Min(extra * price, cap);
}
