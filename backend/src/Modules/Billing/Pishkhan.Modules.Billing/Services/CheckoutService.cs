using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.Modules.Payments.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Services;

/// <summary>Pishkhan's own Zarinpal account, which subscriptions are paid into (section <c>Billing:Checkout</c>).</summary>
public sealed class CheckoutOptions
{
    public const string Section = "Billing:Checkout";

    /// <summary>Pishkhan's Zarinpal merchant ID; checkout answers 503 <c>billing.checkout.unavailable</c> without it.</summary>
    public string? MerchantId { get; set; }

    public bool Sandbox { get; set; }

    /// <summary>The Api host as Zarinpal reaches it, for the callback <c>{ApiBaseUrl}/billing/checkout/callback</c>.</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Where the payer is sent back after Zarinpal: the panel's billing page, with <c>?checkout={id}</c>.</summary>
    public string ReturnUrl { get; set; } = "http://localhost:3000/billing";
}

internal sealed record CheckoutStanding(Subscription? Current, Plan? CurrentPlan, IReadOnlyList<Subscription> Scheduled, DateTimeOffset Now)
{
    public (CheckoutQuote? Quote, string? Error) Quote(Plan plan, string cycle) => CheckoutPlanner.Quote(plan, cycle, Current, CurrentPlan, Scheduled, Now);
}

internal sealed record StartedCheckout(Checkout? Checkout, Uri? PaymentUrl, string? Error);

/// <summary>
/// Subscription checkout (backend plan 9.13): quote, open the payment at Zarinpal, and on the callback verify it for the checkout's own
/// amount and start the subscription it bought. Repeated or forged callbacks never pay twice or for a different amount.
/// </summary>
internal sealed class CheckoutService(
    BillingDbContext db, IZarinpalClient zarinpal, ITenantContext tenant, ICacheInvalidator cache, WalletLedger wallets, IOptions<CheckoutOptions> options, IClock clock)
{
    /// <summary>Wallet top-ups: 10,000 to 100,000,000 Toman.</summary>
    public const long MinTopUp = 100_000;

    public const long MaxTopUp = 1_000_000_000;

    private const string NextInvoiceSql = "SELECT nextval('billing.invoice_numbers') AS \"Value\"";

    /// <summary>
    /// The quote with the coupon (<paramref name="couponCode"/>, case-insensitive) and, when <paramref name="useWallet"/>, as much of the
    /// wallet as it can take. Coupon errors: <c>billing.coupon.invalid</c>, <c>expired</c>, <c>used_up</c>, <c>not_for_plan</c>,
    /// <c>first_purchase_only</c>, <c>already_used</c>.
    /// </summary>
    public async Task<(CheckoutQuote? Quote, Plan? Plan, string? Error)> QuoteAsync(
        Guid workspaceId, Guid planId, string cycle, string? couponCode, bool useWallet, CancellationToken ct)
    {
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null)
        {
            return (null, null, "billing.plan.not_found");
        }

        var standing = await StandingAsync(workspaceId, ct);
        var (quote, error) = standing.Quote(plan, cycle);
        if (error is not null)
        {
            return (null, plan, error);
        }

        Coupon? coupon = null;
        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            (coupon, error) = await CouponAsync(workspaceId, couponCode, plan.Code, quote!.Kind, ct);
            if (error is not null)
            {
                return (null, plan, error);
            }
        }

        var balance = useWallet ? (await wallets.SettledAsync(workspaceId, ct)).Balance : 0;
        return (quote!.Priced(coupon, balance), plan, null);
    }

    private async Task<(Coupon? Coupon, string? Error)> CouponAsync(Guid workspaceId, string code, string planCode, string kind, CancellationToken ct)
    {
        var normalized = Coupon.Normalize(code);
        var coupon = normalized.Length > 32 ? null : await db.Coupons.FirstOrDefaultAsync(c => c.Code == normalized, ct);
        if (coupon is null)
        {
            return (null, "billing.coupon.invalid");
        }

        var paidBefore = await db.Checkouts.AnyAsync(
            c => c.WorkspaceId == workspaceId && c.Status == CheckoutStatuses.Paid && c.Kind != CheckoutKinds.Overage && c.Kind != CheckoutKinds.WalletTopUp, ct);
        var used = await db.CouponRedemptions.CountAsync(r => r.CouponId == coupon.Id && r.WorkspaceId == workspaceId, ct);
        var refusal = coupon.Refusal(planCode, kind, paidBefore, used, clock.UtcNow);
        return refusal is null ? (coupon, null) : (null, refusal);
    }

    /// <summary>The workspace's subscriptions as a checkout sees them now.</summary>
    public async Task<CheckoutStanding> StandingAsync(Guid workspaceId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var current = await Subscription.Current(db.Subscriptions.AsNoTracking(), workspaceId, now).FirstOrDefaultAsync(ct);
        var currentPlan = current is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == current.PlanId, ct);
        var scheduled = await db.Subscriptions.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Scheduled && s.StartsAt > now && (current == null || s.Id != current.Id))
            .ToListAsync(ct);
        return new CheckoutStanding(current, currentPlan, scheduled, now);
    }

    /// <summary>
    /// Opens the checkout. The wallet part is taken at once (and given back if the payment fails). A checkout the coupon and wallet cover
    /// entirely is paid at once without Zarinpal: <see cref="StartedCheckout.PaymentUrl"/> is null and the checkout is paid.
    /// </summary>
    public async Task<StartedCheckout> StartAsync(Guid workspaceId, Guid planId, string cycle, string? couponCode, bool useWallet, Guid userId, CancellationToken ct)
    {
        var (quote, plan, error) = await QuoteAsync(workspaceId, planId, cycle, couponCode, useWallet, ct);
        if (error is not null)
        {
            return new StartedCheckout(null, null, error);
        }

        if (quote!.Amount > 0 && string.IsNullOrWhiteSpace(options.Value.MerchantId))
        {
            return new StartedCheckout(null, null, "billing.checkout.unavailable");
        }

        var now = clock.UtcNow;
        var checkout = Checkout.Create(workspaceId, quote, plan!, userId, now);
        db.Checkouts.Add(checkout);
        if (checkout.WalletUsed > 0)
        {
            wallets.Take(await wallets.GetAsync(workspaceId, ct), checkout);
        }

        if (checkout.Amount == 0)
        {
            await db.SaveChangesAsync(ct);
            await db.Database.CreateExecutionStrategy().ExecuteAsync(token => PayAsync(checkout, null, null, now, token), ct);
            await cache.InvalidateLocalAsync([BillingCacheTags.Workspace(workspaceId)], ct);
            return new StartedCheckout(checkout, null, null);
        }

        return await OpenAtZarinpalAsync(checkout, $"اشتراک پیشخوان - {plan!.Name}", ct);
    }

    /// <summary>Money into the wallet through Zarinpal, <see cref="MinTopUp"/> to <see cref="MaxTopUp"/> Rial (checked by the caller).</summary>
    public async Task<StartedCheckout> StartTopUpAsync(Guid workspaceId, long amount, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.MerchantId))
        {
            return new StartedCheckout(null, null, "billing.checkout.unavailable");
        }

        var checkout = Checkout.ForWalletTopUp(workspaceId, amount, userId, clock.UtcNow);
        db.Checkouts.Add(checkout);
        return await OpenAtZarinpalAsync(checkout, "شارژ کیف پول پیشخوان", ct);
    }

    private async Task<StartedCheckout> OpenAtZarinpalAsync(Checkout checkout, string description, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var callback = new Uri($"{options.Value.ApiBaseUrl.TrimEnd('/')}/billing/checkout/callback");
        var started = await zarinpal.StartAsync(options.Value.MerchantId!, options.Value.Sandbox, checkout.Amount, callback, description, ct);
        if (!started.Ok)
        {
            checkout.Fail(started.Error!, now);
            await GiveBackAsync(checkout, "checkout_failed", ct);
            await db.SaveChangesAsync(ct);
            return new StartedCheckout(checkout, null, "billing.checkout.gateway_unavailable");
        }

        checkout.Started(started.Authority!, now);
        await db.SaveChangesAsync(ct);
        return new StartedCheckout(checkout, started.RedirectUrl, null);
    }

    private async Task GiveBackAsync(Checkout checkout, string reason, CancellationToken ct)
    {
        if (checkout.WalletUsed > 0 && !checkout.WalletReleased)
        {
            wallets.GiveBack(await wallets.GetAsync(checkout.WorkspaceId, ct), checkout, reason);
        }
    }

    /// <summary>
    /// Pays every due overage charge at once (BE-2.8). Refused with <c>billing.overage.nothing_due</c> when nothing is due and
    /// <c>billing.overage.checkout_pending</c> while another overage payment is open, so the same charges are never paid twice.
    /// </summary>
    public async Task<StartedCheckout> StartOverageAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.MerchantId))
        {
            return new StartedCheckout(null, null, "billing.checkout.unavailable");
        }

        var now = clock.UtcNow;
        var due = await db.OverageCharges.Where(c => c.Status == OverageChargeStatuses.Due).SumAsync(c => (long?)c.Amount, ct) ?? 0;
        if (due <= 0)
        {
            return new StartedCheckout(null, null, "billing.overage.nothing_due");
        }

        var open = now - Checkout.Lifetime;
        if (await db.Checkouts.AnyAsync(c => c.Kind == CheckoutKinds.Overage && c.Status == CheckoutStatuses.Pending && c.CreatedAt > open, ct))
        {
            return new StartedCheckout(null, null, "billing.overage.checkout_pending");
        }

        var standing = await StandingAsync(workspaceId, ct);
        var plan = standing.CurrentPlan;
        var checkout = Checkout.ForOverage(
            workspaceId, Math.Max(due, CheckoutPlanner.MinimumAmount), plan?.Id ?? Guid.Empty, plan?.Code ?? CheckoutKinds.Overage, plan?.Name ?? "هزینه‌ی اضافه",
            plan?.VersionNumber ?? 0, userId, now);
        db.Checkouts.Add(checkout);
        var callback = new Uri($"{options.Value.ApiBaseUrl.TrimEnd('/')}/billing/checkout/callback");
        var started = await zarinpal.StartAsync(options.Value.MerchantId!, options.Value.Sandbox, checkout.Amount, callback, "هزینه‌ی اضافه‌ی پیشخوان", ct);
        if (!started.Ok)
        {
            checkout.Fail(started.Error!, now);
            await db.SaveChangesAsync(ct);
            return new StartedCheckout(checkout, null, "billing.checkout.gateway_unavailable");
        }

        checkout.Started(started.Authority!, now);
        await db.SaveChangesAsync(ct);
        return new StartedCheckout(checkout, started.RedirectUrl, null);
    }

    /// <summary>Zarinpal's callback. Returns the checkout (to send the payer back to it), or null for an unknown authority.</summary>
    public async Task<Checkout?> CompleteAsync(string? authority, bool customerFinished, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authority) || authority.Length > 64)
        {
            return null;
        }

        Checkout? checkout;
        using (tenant.BeginSystemScope())
        {
            checkout = await db.Checkouts.FirstOrDefaultAsync(c => c.Authority == authority, ct);
        }

        if (checkout is null)
        {
            return null;
        }

        tenant.SetWorkspace(checkout.WorkspaceId);
        var now = clock.UtcNow;
        if (checkout.Status != CheckoutStatuses.Pending)
        {
            return checkout;
        }

        if (!customerFinished)
        {
            checkout.Fail("cancelled", now);
            await GiveBackAsync(checkout, "checkout_cancelled", ct);
            await SaveAsync(ct);
            return checkout;
        }

        var verified = await zarinpal.VerifyAsync(options.Value.MerchantId ?? string.Empty, options.Value.Sandbox, checkout.Amount, authority, ct);
        if (!verified.Ok)
        {
            // Zarinpal unreachable: leave it pending, so the same callback can be retried.
            if (verified.Code != "unavailable")
            {
                checkout.Fail(verified.Code, now);
                await GiveBackAsync(checkout, "checkout_failed", ct);
                await SaveAsync(ct);
            }

            return checkout;
        }

        // Money was received: the subscription starts even if the checkout passed its hour while the customer was at the gateway.
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(token => PayAsync(checkout, verified.ReferenceId, verified.CardMask, now, token), ct);
        }
        catch (DbUpdateException)
        {
            // A second callback for the same payment got there first (row version, or the one-active-subscription index): its result stands.
            db.ChangeTracker.Clear();
            using (tenant.BeginSystemScope())
            {
                return await db.Checkouts.AsNoTracking().FirstAsync(c => c.Id == checkout.Id, ct);
            }
        }

        await cache.InvalidateLocalAsync([BillingCacheTags.Workspace(checkout.WorkspaceId)], ct);
        return checkout;
    }

    /// <summary>Records the payment: what it bought, the coupon's redemption, the invoice and the audit entry. No reference for a checkout paid in full by a coupon or the wallet.</summary>
    private async Task PayAsync(Checkout checkout, string? referenceId, string? cardMask, DateTimeOffset now, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        Guid? subscriptionId = null;
        var settled = 0;
        if (checkout.WalletUsed > 0 && checkout.WalletReleased)
        {
            // Paid at Zarinpal after its wallet hold was given back: take the wallet part again, as much as is there now.
            var wallet = await wallets.GetAsync(checkout.WorkspaceId, token);
            var again = Math.Min(wallet.Balance, checkout.WalletUsed);
            checkout.WalletRetaken(again, now);
            if (again > 0)
            {
                wallets.Change(wallet, -again, WalletEntryKinds.Checkout, "late_payment", checkout.Id, WalletActors.System, null);
            }
        }

        if (checkout.CouponId is { } couponId && await db.Coupons.FirstOrDefaultAsync(c => c.Id == couponId, token) is { } coupon)
        {
            coupon.Redeemed(now);
            db.CouponRedemptions.Add(CouponRedemption.Create(couponId, checkout, now));
        }

        if (checkout.Kind == CheckoutKinds.WalletTopUp)
        {
            wallets.Change(await wallets.GetAsync(checkout.WorkspaceId, token), checkout.Amount, WalletEntryKinds.TopUp, null, checkout.Id, WalletActors.User, checkout.CreatedBy);
        }
        else if (checkout.Kind == CheckoutKinds.Overage)
        {
            // The charges it was opened for: those due when it was made (a charge made later is paid next time).
            var charges = await db.OverageCharges.Where(c => c.Status == OverageChargeStatuses.Due && c.CreatedAt <= checkout.CreatedAt).ToListAsync(token);
            foreach (var charge in charges)
            {
                charge.Settle(checkout.Id, now);
            }

            settled = charges.Count;
        }
        else
        {
            subscriptionId = (await ApplyAsync(checkout, now, token)).Id;
        }

        var invoice = await db.Database.SqlQueryRaw<long>(NextInvoiceSql).SingleAsync(token);
        checkout.MarkPaid(referenceId, cardMask, invoice, subscriptionId, now);
        var entry = AuditEntries.Create(
            AuditActor.User(checkout.CreatedBy, null, null), "billing.checkout_paid", "checkout", checkout.Id.ToString(), checkout.WorkspaceId, now,
            after: new
            {
                checkout.Kind, checkout.PlanCode, checkout.Cycle, checkout.Amount, checkout.Credit, checkout.Discount, checkout.CouponCode, checkout.WalletUsed, invoice,
                referenceId, overageCharges = settled,
            });
        db.Publish(entry, entry.PartitionKey(), checkout.WorkspaceId);
        cache.Enqueue(db, BillingCacheTags.Workspace(checkout.WorkspaceId));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    /// <summary>
    /// Starts what the checkout bought. A new purchase or an upgrade replaces the current subscription now; a renewal or downgrade starts
    /// after the last period already paid for, as things stand at payment time.
    /// </summary>
    private async Task<Subscription> ApplyAsync(Checkout checkout, DateTimeOffset now, CancellationToken ct)
    {
        var plan = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == checkout.PlanId, ct);
        var all = await db.Subscriptions.Where(s => s.Status != SubscriptionStatus.Ended).ToListAsync(ct);

        // Settle the timeline first: an expired one ends, and a paid period whose start has come becomes the active one.
        foreach (var s in all.Where(s => s.EndsAt is { } end && end <= now))
        {
            s.End(Subscription.EndExpired, now);
        }

        var active = all.FirstOrDefault(s => s.Status == SubscriptionStatus.Active);
        if (active is null && all.Where(s => s.Status == SubscriptionStatus.Scheduled && s.StartsAt <= now).OrderBy(s => s.StartsAt).FirstOrDefault() is { } due)
        {
            due.Activate();
            active = due;
        }

        await db.SaveChangesAsync(ct);

        var immediate = checkout.Kind is CheckoutKinds.New or CheckoutKinds.Upgrade || active is null;
        DateTimeOffset start;
        if (immediate)
        {
            if (active is not null)
            {
                active.End(Subscription.EndReplaced, now);
                await db.SaveChangesAsync(ct);
            }

            start = now;
        }
        else
        {
            start = all.Where(s => s.Status != SubscriptionStatus.Ended).Select(s => s.EndsAt ?? now).Append(now).Max();
        }

        var subscription = Subscription.Purchase(checkout.WorkspaceId, plan, checkout.Cycle, checkout.Amount + checkout.WalletUsed, checkout.Id, checkout.CreatedBy, start, now);
        db.Subscriptions.Add(subscription);
        return subscription;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
        }
    }
}
