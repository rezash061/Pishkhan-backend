using System.Globalization;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Domain;

public enum SubscriptionStatus
{
    /// <summary>The workspace's current subscription (at most one), unless <see cref="Subscription.EndsAt"/> has passed.</summary>
    Active,

    /// <summary>Replaced, cancelled or expired; kept as history.</summary>
    Ended,

    /// <summary>Paid for and starting later, when the subscription before it ends (a renewal or a move to a smaller plan).</summary>
    Scheduled,
}

public static class BillingCycles
{
    public const string Monthly = "monthly";
    public const string Annual = "annual";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Monthly, Annual };

    /// <summary>How long a paid period lasts: 30 days for a month, 365 for a year.</summary>
    public static TimeSpan Length(string cycle) => cycle == Annual ? TimeSpan.FromDays(365) : TimeSpan.FromDays(30);
}

/// <summary>
/// A plan version a workspace is on (backend plan 9.13). During the closed beta staff grant plans and trials by hand (Finance); paid
/// checkout comes in Phase 2. A workspace without a current subscription is on the published free plan. The subscription keeps the
/// plan version it was granted, so editing the plan later does not change it.
/// </summary>
public sealed class Subscription : AggregateRoot<Guid>, ITenantOwned
{
    public const string EndReplaced = "replaced";
    public const string EndCancelled = "cancelled";
    public const string EndExpired = "expired";

    private Subscription()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid PlanId { get; private set; }

    public string PlanCode { get; private set; } = string.Empty;

    public int PlanVersion { get; private set; }

    public bool Trial { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    /// <summary>Null for a grant without an end.</summary>
    public DateTimeOffset? EndsAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>replaced, cancelled or expired.</summary>
    public string? EndReason { get; private set; }

    /// <summary>The staff member who granted it, or the user who bought it.</summary>
    public Guid GrantedBy { get; private set; }

    /// <summary>Bought through checkout: <see cref="BillingCycles"/>. Null for a staff grant.</summary>
    public string? Cycle { get; private set; }

    /// <summary>What was paid for this period (Rial); null for a staff grant. An upgrade credits the unused part of it.</summary>
    public long? PaidAmount { get; private set; }

    public Guid? CheckoutId { get; private set; }

    /// <summary>A subscription bought through checkout, current from <paramref name="startsAt"/> (now, or when the one before it ends).</summary>
    public static Subscription Purchase(
        Guid workspaceId, Plan plan, string cycle, long paid, Guid checkoutId, Guid userId, DateTimeOffset startsAt, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        PlanId = plan.Id,
        PlanCode = plan.Code,
        PlanVersion = plan.VersionNumber,
        Status = startsAt > now ? SubscriptionStatus.Scheduled : SubscriptionStatus.Active,
        StartsAt = startsAt,
        EndsAt = startsAt + BillingCycles.Length(cycle),
        GrantedBy = userId,
        Cycle = cycle,
        PaidAmount = paid,
        CheckoutId = checkoutId,
        CreatedAt = now,
    };

    /// <summary>A scheduled subscription whose time has come.</summary>
    public void Activate()
    {
        if (Status == SubscriptionStatus.Scheduled)
        {
            Status = SubscriptionStatus.Active;
        }
    }

    /// <summary>The unused part of what was paid for this period, credited on an upgrade (Rial, rounded down).</summary>
    public long UnusedCredit(DateTimeOffset now)
    {
        if (PaidAmount is not { } paid || EndsAt is not { } end || end <= now || end <= StartsAt)
        {
            return 0;
        }

        var from = now > StartsAt ? now : StartsAt;
        return (long)(paid * ((end - from).TotalSeconds / (end - StartsAt).TotalSeconds));
    }

    public static Subscription Grant(Guid workspaceId, Plan plan, bool trial, DateTimeOffset? endsAt, Guid staffId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        PlanId = plan.Id,
        PlanCode = plan.Code,
        PlanVersion = plan.VersionNumber,
        Trial = trial,
        Status = SubscriptionStatus.Active,
        StartsAt = now,
        EndsAt = endsAt,
        GrantedBy = staffId,
        CreatedAt = now,
    };

    /// <summary>Current: active and not past its end.</summary>
    public bool IsCurrent(DateTimeOffset now) =>
        (Status == SubscriptionStatus.Active || (Status == SubscriptionStatus.Scheduled && StartsAt <= now)) && (EndsAt is null || EndsAt > now);

    /// <summary>
    /// The workspace's current subscription: the active one, or a scheduled one whose start has come (the one before it ended). Status is
    /// stored as text, so "Active" sorts before "Scheduled".
    /// </summary>
    public static IQueryable<Subscription> Current(IQueryable<Subscription> query, Guid workspaceId, DateTimeOffset now) =>
        query.Where(s => s.WorkspaceId == workspaceId
                && (s.Status == SubscriptionStatus.Active || (s.Status == SubscriptionStatus.Scheduled && s.StartsAt <= now))
                && (s.EndsAt == null || s.EndsAt > now))
            .OrderBy(s => s.Status).ThenBy(s => s.StartsAt);

    /// <summary>Moves the end later. An expired subscription is extended from now.</summary>
    public void Extend(TimeSpan by, DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Active || EndsAt is not { } end)
        {
            throw new DomainException("billing.subscription.not_extendable");
        }

        EndsAt = (end > now ? end : now) + by;
    }

    public void End(string reason, DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Ended)
        {
            return;
        }

        Status = SubscriptionStatus.Ended;
        EndedAt = EndsAt is { } end && end < now ? end : now;
        EndReason = EndsAt is { } e && e <= now ? EndExpired : reason;
    }

    /// <summary>The API spelling: active, trialing, expired, ended, scheduled.</summary>
    public string StateName(DateTimeOffset now) => Status switch
    {
        SubscriptionStatus.Ended => "ended",
        SubscriptionStatus.Scheduled when StartsAt > now => "scheduled",
        _ when !IsCurrent(now) => "expired",
        _ when Trial => "trialing",
        _ => "active",
    };
}

/// <summary>Usage of a counted meter in one billing period.</summary>
public sealed class UsageCounter : ITenantOwned
{
    private UsageCounter()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Meter { get; private set; } = string.Empty;

    /// <summary>Start of the period (UTC), see <see cref="BillingPeriod"/>.</summary>
    public DateTimeOffset PeriodStart { get; private set; }

    public long Quantity { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>
/// A billing period is a Jalali calendar month in Asia/Tehran (1 Aban 00:00 to 1 Azar 00:00), the month sellers think in. Plan
/// limits are per period; a subscription granted mid-month gets its limits for the rest of that month.
/// </summary>
public readonly record struct BillingPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    private static readonly PersianCalendar Calendar = new();

    public static BillingPeriod For(DateTimeOffset utc)
    {
        var local = TehranTime.ToTehran(utc).DateTime;
        var year = Calendar.GetYear(local);
        var month = Calendar.GetMonth(local);
        var start = Calendar.ToDateTime(year, month, 1, 0, 0, 0, 0);
        var end = month == 12 ? Calendar.ToDateTime(year + 1, 1, 1, 0, 0, 0, 0) : Calendar.ToDateTime(year, month + 1, 1, 0, 0, 0, 0);
        return new BillingPeriod(FromTehran(start), FromTehran(end));
    }

    private static DateTimeOffset FromTehran(DateTime local)
    {
        var offset = TehranTime.Zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUniversalTime();
    }
}

/// <summary>A subject (for example a conversation) already counted for a distinct meter in a period.</summary>
public sealed class UsageSubject : ITenantOwned
{
    private UsageSubject()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Meter { get; private set; } = string.Empty;

    public DateTimeOffset PeriodStart { get; private set; }

    public Guid SubjectId { get; private set; }
}
