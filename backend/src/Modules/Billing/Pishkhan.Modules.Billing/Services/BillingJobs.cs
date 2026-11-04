using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.SharedKernel;
using Quartz;

namespace Pishkhan.Modules.Billing.Services;

/// <summary>
/// Daily (platform group, BE-2.8): closes the previous period's overage into charges, and sends the billing reminders: a paid
/// subscription ending within 3 days, an overage charge still due a week after its period, and overage stopped for an unpaid charge.
/// Each reminder goes once (<see cref="BillingAlert"/>), so a rerun never repeats one.
/// </summary>
[DisallowConcurrentExecution]
public sealed class BillingDailyJob(BillingDbContext db, ITenantContext tenant, IClock clock, ILogger<BillingDailyJob> logger) : IJob
{
    public static readonly TimeSpan ExpiryNotice = TimeSpan.FromDays(3);

    public static readonly TimeSpan DueNotice = TimeSpan.FromDays(7);

    public async Task Execute(IJobExecutionContext context) => await RunAsync(context.CancellationToken);

    /// <summary>Returns the number of overage charges made.</summary>
    internal async Task<int> RunAsync(CancellationToken ct)
    {
        using var system = tenant.BeginSystemScope();
        var now = clock.UtcNow;
        var charges = await ChargeAsync(now, ct);
        await RemindExpiryAsync(now, ct);
        await RemindChargesAsync(now, ct);
        return charges;
    }

    private async Task<int> ChargeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var previous = BillingPeriod.For(BillingPeriod.For(now).Start.AddSeconds(-1));
        var counters = await db.UsageCounters.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(c => c.Meter == BillingMeters.AiConversations && c.PeriodStart == previous.Start && c.Quantity > 0)
            .Select(c => new { c.WorkspaceId, c.Quantity })
            .ToListAsync(ct);
        var charged = (await db.OverageCharges.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(c => c.PeriodStart == previous.Start).Select(c => c.WorkspaceId).ToListAsync(ct)).ToHashSet();

        var made = 0;
        foreach (var counter in counters.Where(c => !charged.Contains(c.WorkspaceId)))
        {
            // The plan as it was at the period's end, and the cap as set now (the owner's latest word).
            var plan = await EntitlementService.LoadAsync(db, counter.WorkspaceId, previous.End.AddSeconds(-1), ct);
            if (plan.Limit(BillingMeters.AiConversations) is not { } included || counter.Quantity <= included || plan.OveragePrice is not > 0)
            {
                continue;
            }

            var settings = await db.Settings.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).FirstOrDefaultAsync(s => s.WorkspaceId == counter.WorkspaceId, ct)
                ?? BillingSettings.Default(counter.WorkspaceId);
            var extra = counter.Quantity - included;
            var price = plan.OveragePrice.Value;
            var amount = OverageRules.Charge(extra, price, settings.Cap ?? OverageRules.DefaultCap(plan.MonthlyPrice));
            if (amount <= 0)
            {
                continue;
            }

            db.ChangeTracker.Clear();
            db.OverageCharges.Add(OverageCharge.Create(counter.WorkspaceId, previous, (int)Math.Min(extra, int.MaxValue), price, amount, now));
            Notify(counter.WorkspaceId, "صورتحساب هزینه‌ی اضافه آماده است", "هزینه‌ی گفتگوهای بیش از طرح در ماه گذشته محاسبه شد؛ لطفاً تا دو هفته پرداخت کنید.");
            await db.SaveChangesAsync(ct);
            made++;
            logger.LogInformation("Overage charge for workspace {WorkspaceId}: {Extra} conversations, {Amount} Rial", counter.WorkspaceId, extra, amount);
        }

        return made;
    }

    private async Task RemindExpiryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var soon = now + ExpiryNotice;
        var ending = await db.Subscriptions.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(s => s.Status == SubscriptionStatus.Active && !s.Trial && s.CheckoutId != null && s.EndsAt != null && s.EndsAt > now && s.EndsAt <= soon)
            .Select(s => new { s.Id, s.WorkspaceId })
            .ToListAsync(ct);
        foreach (var s in ending)
        {
            // Renewed already: a later paid period is scheduled.
            var renewed = await db.Subscriptions.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
                .AnyAsync(x => x.WorkspaceId == s.WorkspaceId && x.Status == SubscriptionStatus.Scheduled, ct);
            if (!renewed)
            {
                await RemindOnceAsync(s.WorkspaceId, $"expiry:{s.Id:N}", "اشتراک شما به‌زودی تمام می‌شود", "برای اینکه پاسخ خودکار قطع نشود، اشتراک را تمدید کنید.", ct);
            }
        }
    }

    private async Task RemindChargesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var dueBefore = now - DueNotice;
        var due = await db.OverageCharges.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(c => c.Status == OverageChargeStatuses.Due && c.PeriodEnd < dueBefore)
            .Select(c => new { c.Id, c.WorkspaceId, c.PeriodEnd })
            .ToListAsync(ct);
        foreach (var c in due)
        {
            if (c.PeriodEnd < now - OverageRules.UnpaidGrace)
            {
                await RemindOnceAsync(
                    c.WorkspaceId, $"overage_blocked:{c.Id:N}", "هزینه‌ی اضافه پرداخت نشده است",
                    "تا پرداخت آن، گفتگوهای بیش از طرح به همکاران شما سپرده می‌شود.", ct);
            }
            else
            {
                await RemindOnceAsync(
                    c.WorkspaceId, $"overage_due:{c.Id:N}", "یادآوری پرداخت هزینه‌ی اضافه",
                    "اگر تا یک هفته‌ی دیگر پرداخت نشود، پاسخ خودکار به گفتگوهای بیش از طرح متوقف می‌شود.", ct);
            }
        }
    }

    private async Task RemindOnceAsync(Guid workspaceId, string key, string title, string body, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (await db.Alerts.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).AnyAsync(a => a.WorkspaceId == workspaceId && a.Key == key, ct))
        {
            return;
        }

        db.Alerts.Add(new BillingAlert { WorkspaceId = workspaceId, Key = key, CreatedAt = clock.UtcNow });
        Notify(workspaceId, title, body);
        await db.SaveChangesAsync(ct);
    }

    private void Notify(Guid workspaceId, string title, string body) =>
        db.Publish(new NotificationRequested(workspaceId, null, BillingNotices.Kind, title, body, BillingNotices.Link), workspaceId.ToString(), workspaceId: workspaceId);
}
