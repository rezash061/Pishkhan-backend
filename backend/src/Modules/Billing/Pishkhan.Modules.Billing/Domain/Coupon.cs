using System.Text.RegularExpressions;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Domain;

public static class CouponKinds
{
    /// <summary><see cref="Coupon.Value"/> percent off (1 to 100).</summary>
    public const string Percent = "percent";

    /// <summary><see cref="Coupon.Value"/> Rial off.</summary>
    public const string Fixed = "fixed";

    public static readonly string[] All = [Percent, Fixed];
}

/// <summary>
/// A discount code for subscription checkouts (admin plan 4.4), made by Finance. It applies to the price after an upgrade's credit, may be
/// limited to plans and to a first purchase, and counts redemptions only when a checkout using it is paid.
/// </summary>
public sealed partial class Coupon : AggregateRoot<Guid>
{
    public const int MaxDescription = 200;

    private Coupon()
    {
    }

    /// <summary>Upper case letters, digits and dashes, 3 to 32; matched case-insensitively.</summary>
    public string Code { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string Kind { get; private set; } = CouponKinds.Percent;

    public long Value { get; private set; }

    /// <summary>Plan codes it applies to; empty for every plan.</summary>
    public List<string> PlanCodes { get; private set; } = [];

    /// <summary>Only for a workspace's first paid subscription (kind <c>new</c>, never paid before).</summary>
    public bool FirstPurchaseOnly { get; private set; }

    /// <summary>Across all workspaces; null for no limit.</summary>
    public int? MaxRedemptions { get; private set; }

    public int MaxPerWorkspace { get; private set; } = 1;

    public DateTimeOffset? ValidFrom { get; private set; }

    public DateTimeOffset? ValidUntil { get; private set; }

    public bool Active { get; private set; } = true;

    public int Redemptions { get; private set; }

    public Guid CreatedBy { get; private set; }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{2,31}$")]
    private static partial Regex CodePattern();

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    public static bool IsValidCode(string code) => CodePattern().IsMatch(code);

    public static Coupon Create(string code, CouponTerms terms, Guid staffId, DateTimeOffset now)
    {
        var coupon = new Coupon { Id = Uuid.NewV7(now), Code = Normalize(code), CreatedBy = staffId, CreatedAt = now, UpdatedAt = now };
        coupon.Kind = terms.Kind;
        coupon.Value = terms.Value;
        coupon.Apply(terms, now);
        return coupon;
    }

    /// <summary>Changes everything but the code, kind and value (those are on paid invoices already).</summary>
    public void Apply(CouponTerms terms, DateTimeOffset now)
    {
        Description = string.IsNullOrWhiteSpace(terms.Description) ? null : terms.Description.Trim();
        PlanCodes = [.. terms.PlanCodes.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct()];
        FirstPurchaseOnly = terms.FirstPurchaseOnly;
        MaxRedemptions = terms.MaxRedemptions;
        MaxPerWorkspace = terms.MaxPerWorkspace;
        ValidFrom = terms.ValidFrom;
        ValidUntil = terms.ValidUntil;
        Active = terms.Active;
        UpdatedAt = now;
    }

    public void Redeemed(DateTimeOffset now)
    {
        Redemptions++;
        UpdatedAt = now;
    }

    /// <summary>The discount on <paramref name="amount"/> (Rial), never more than it.</summary>
    public long DiscountOn(long amount) =>
        amount <= 0 ? 0 : Kind == CouponKinds.Percent ? Math.Min(amount, (long)Math.Round(amount * Value / 100m, MidpointRounding.AwayFromZero)) : Math.Min(amount, Value);

    /// <summary>Why it cannot be used for this checkout now, or null. <paramref name="usedByWorkspace"/> counts paid uses by the workspace.</summary>
    public string? Refusal(string planCode, string checkoutKind, bool paidBefore, int usedByWorkspace, DateTimeOffset now)
    {
        if (!Active || (ValidFrom is { } from && now < from) || (ValidUntil is { } until && now >= until))
        {
            return "billing.coupon.expired";
        }

        if (MaxRedemptions is { } max && Redemptions >= max)
        {
            return "billing.coupon.used_up";
        }

        if (PlanCodes.Count > 0 && !PlanCodes.Contains(planCode))
        {
            return "billing.coupon.not_for_plan";
        }

        if (FirstPurchaseOnly && (checkoutKind != CheckoutKinds.New || paidBefore))
        {
            return "billing.coupon.first_purchase_only";
        }

        return usedByWorkspace >= MaxPerWorkspace ? "billing.coupon.already_used" : null;
    }

    /// <summary>Field errors for <see cref="Create"/> and <see cref="Apply"/>, keyed by request field.</summary>
    public static Dictionary<string, string[]> Validate(string? code, CouponTerms terms, bool creating)
    {
        var errors = new Dictionary<string, string[]>();
        if (creating && (code is null || !IsValidCode(Normalize(code))))
        {
            errors["code"] = ["billing.coupon.code_invalid"];
        }

        if (creating && !CouponKinds.All.Contains(terms.Kind))
        {
            errors["kind"] = ["validation.invalid"];
        }
        else if (creating && (terms.Kind == CouponKinds.Percent ? terms.Value is < 1 or > 100 : terms.Value is < 10_000 or > 10_000_000_000))
        {
            errors["value"] = ["validation.out_of_range"];
        }

        if (terms.Description is { Length: > MaxDescription })
        {
            errors["description"] = ["validation.too_long"];
        }

        if (terms.PlanCodes.Count > 20)
        {
            errors["planCodes"] = ["validation.too_many"];
        }

        if (terms.MaxRedemptions is < 1 or > 1_000_000)
        {
            errors["maxRedemptions"] = ["validation.out_of_range"];
        }

        if (terms.MaxPerWorkspace is < 1 or > 100)
        {
            errors["maxPerWorkspace"] = ["validation.out_of_range"];
        }

        if (terms.ValidFrom is { } from && terms.ValidUntil is { } until && until <= from)
        {
            errors["validUntil"] = ["billing.coupon.ends_before_start"];
        }

        return errors;
    }
}

public sealed record CouponTerms(
    string Kind, long Value, string? Description, IReadOnlyList<string> PlanCodes, bool FirstPurchaseOnly, int? MaxRedemptions, int MaxPerWorkspace,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, bool Active);

/// <summary>A paid checkout that used a coupon: one row per checkout.</summary>
public sealed class CouponRedemption : ITenantOwned
{
    private CouponRedemption()
    {
    }

    public Guid Id { get; private init; }

    public Guid CouponId { get; private init; }

    public Guid WorkspaceId { get; private init; }

    public Guid CheckoutId { get; private init; }

    /// <summary>Rial.</summary>
    public long Discount { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static CouponRedemption Create(Guid couponId, Checkout checkout, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        CouponId = couponId,
        WorkspaceId = checkout.WorkspaceId,
        CheckoutId = checkout.Id,
        Discount = checkout.Discount,
        CreatedAt = now,
    };
}
