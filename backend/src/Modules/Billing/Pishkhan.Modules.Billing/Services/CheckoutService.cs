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

internal sealed record StartedCheckout(Checkout? Checkout, Uri? PaymentUrl, string? Error);

/// <summary>
/// Subscription checkout (backend plan 9.13): quote, open the payment at Zarinpal, and on the callback verify it for the checkout's own
/// amount and start the subscription it bought. Repeated or forged callbacks never pay twice or for a different amount.
/// </summary>
internal sealed class CheckoutService(
    BillingDbContext db, IZarinpalClient zarinpal, ITenantContext tenant, ICacheInvalidator cache, IOptions<CheckoutOptions> options, IClock clock)
{
    private const string NextInvoiceSql = "SELECT nextval('billing.invoice_numbers') AS \"Value\"";

    public async Task<(CheckoutQuote? Quote, Plan? Plan, string? Error)> QuoteAsync(Guid workspaceId, Guid planId, string cycle, CancellationToken ct)
    {
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null)
        {
            return (null, null, "billing.plan.not_found");
        }

        var now = clock.UtcNow;
        var current = await Subscription.Current(db.Subscriptions.AsNoTracking(), workspaceId, now).FirstOrDefaultAsync(ct);
        var currentPlan = current is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == current.PlanId, ct);
        var scheduled = await db.Subscriptions.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Scheduled && s.StartsAt > now && (current == null || s.Id != current.Id))
            .ToListAsync(ct);
        var (quote, error) = CheckoutPlanner.Quote(plan, cycle, current, currentPlan, scheduled, now);
        return (quote, plan, error);
    }

    public async Task<StartedCheckout> StartAsync(Guid workspaceId, Guid planId, string cycle, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.MerchantId))
        {
            return new StartedCheckout(null, null, "billing.checkout.unavailable");
        }

        var (quote, plan, error) = await QuoteAsync(workspaceId, planId, cycle, ct);
        if (error is not null)
        {
            return new StartedCheckout(null, null, error);
        }

        var now = clock.UtcNow;
        var checkout = Checkout.Create(workspaceId, quote!, plan!, userId, now);
        db.Checkouts.Add(checkout);
        var callback = new Uri($"{options.Value.ApiBaseUrl.TrimEnd('/')}/billing/checkout/callback");
        var started = await zarinpal.StartAsync(options.Value.MerchantId!, options.Value.Sandbox, checkout.Amount, callback, $"اشتراک پیشخوان - {plan!.Name}", ct);
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
                await SaveAsync(ct);
            }

            return checkout;
        }

        // Money was received: the subscription starts even if the checkout passed its hour while the customer was at the gateway.
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(token => PayAsync(checkout, verified, now, token), ct);
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

    private async Task PayAsync(Checkout checkout, ZarinpalVerify verified, DateTimeOffset now, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var subscription = await ApplyAsync(checkout, now, token);
        var invoice = await db.Database.SqlQueryRaw<long>(NextInvoiceSql).SingleAsync(token);
        checkout.MarkPaid(verified.ReferenceId!, verified.CardMask, invoice, subscription.Id, now);
        var entry = AuditEntries.Create(
            AuditActor.User(checkout.CreatedBy, null, null), "billing.checkout_paid", "checkout", checkout.Id.ToString(), checkout.WorkspaceId, now,
            after: new { checkout.Kind, checkout.PlanCode, checkout.Cycle, checkout.Amount, checkout.Credit, invoice, referenceId = verified.ReferenceId });
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

        var subscription = Subscription.Purchase(checkout.WorkspaceId, plan, checkout.Cycle, checkout.Amount, checkout.Id, checkout.CreatedBy, start, now);
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
