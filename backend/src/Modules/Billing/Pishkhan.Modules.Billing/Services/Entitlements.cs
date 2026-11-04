using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Services;

public static class BillingCacheTags
{
    /// <summary>A workspace's subscription; plan changes use <see cref="CacheTags.Plans"/>.</summary>
    public static string Workspace(Guid workspaceId) => $"billing:ws:{workspaceId:N}";
}

/// <summary>
/// The current plan of a workspace: its current subscription's plan version, else the published free plan (the cheapest when there
/// are several), else nothing limited. Cached 5 minutes; a grant, extension or cancellation and any plan publish clear it.
/// </summary>
internal sealed class EntitlementService(HybridCache cache, IServiceScopeFactory scopes, IClock clock) : IEntitlements
{
    private static readonly HybridCacheEntryOptions Entry = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(1) };

    public async Task<Entitlements> GetAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var cached = await cache.GetOrCreateAsync(
            $"billing:ent:{workspaceId:N}",
            (workspaceId, scopes, clock),
            static async (state, token) =>
            {
                // A new scope: the caller may be inside its own transaction, and HybridCache may run this on another task.
                await using var scope = state.scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
                return await LoadAsync(db, state.workspaceId, state.clock.UtcNow, token);
            },
            Entry,
            [CacheTags.Plans, BillingCacheTags.Workspace(workspaceId)],
            cancellationToken);

        // An entry cached before a subscription's end is not used after it.
        return cached.EndsAt is { } end && end <= clock.UtcNow ? await ReloadAsync(workspaceId, cancellationToken) : cached;
    }

    public async Task<long> GetUsageAsync(Guid workspaceId, string meter, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var period = BillingPeriod.For(clock.UtcNow);
        return await db.UsageCounters.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(u => u.WorkspaceId == workspaceId && u.Meter == meter && u.PeriodStart == period.Start)
            .Select(u => u.Quantity)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AiAllowance> CheckAiAsync(Guid workspaceId, Guid conversationId, CancellationToken cancellationToken)
    {
        var plan = await GetAsync(workspaceId, cancellationToken);
        if (plan.Limit(BillingMeters.AiMessages) is not null
            && !plan.Allows(BillingMeters.AiMessages, await GetUsageAsync(workspaceId, BillingMeters.AiMessages, cancellationToken) + 1))
        {
            return AiAllowance.Refused(AiAllowanceReasons.PlanLimit);
        }

        if (plan.Limit(BillingMeters.AiConversations) is not { } limit)
        {
            return AiAllowance.Within;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var now = clock.UtcNow;
        var period = BillingPeriod.For(now);
        var counted = await db.UsageSubjects.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .AnyAsync(s => s.WorkspaceId == workspaceId && s.Meter == BillingMeters.AiConversations && s.PeriodStart == period.Start && s.SubjectId == conversationId, cancellationToken);
        if (counted)
        {
            return AiAllowance.Within;
        }

        var used = await GetUsageAsync(workspaceId, BillingMeters.AiConversations, cancellationToken);
        var settings = await db.Settings.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, cancellationToken)
            ?? BillingSettings.Default(workspaceId);
        var overdue = now - OverageRules.UnpaidGrace;
        var unpaid = await db.OverageCharges.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .AnyAsync(c => c.WorkspaceId == workspaceId && c.Status == OverageChargeStatuses.Due && c.PeriodEnd < overdue, cancellationToken);
        return OverageRules.Decide(limit, used, false, plan.OveragePrice, settings.OverageEnabled, settings.Cap ?? OverageRules.DefaultCap(plan.MonthlyPrice), unpaid);
    }

    internal static async Task<Entitlements> LoadAsync(BillingDbContext db, Guid workspaceId, DateTimeOffset now, CancellationToken ct)
    {
        var subscription = await Subscription.Current(db.Subscriptions.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]), workspaceId, now)
            .FirstOrDefaultAsync(ct);
        if (subscription is not null && await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == subscription.PlanId, ct) is { } plan)
        {
            return new Entitlements(
                workspaceId, "subscription", plan.Id, plan.Code, plan.Name, plan.VersionNumber, subscription.Trial, subscription.EndsAt, new(plan.Limits, StringComparer.Ordinal),
                plan.MonthlyPrice, subscription.Trial ? null : plan.OveragePerConversation);
        }

        var free = await FreePlanAsync(db, ct);
        return free is null
            ? new Entitlements(workspaceId, "none", null, null, null, null, false, null, [])
            : new Entitlements(workspaceId, "free", free.Id, free.Code, free.Name, free.VersionNumber, false, null, new(free.Limits, StringComparer.Ordinal));
    }

    internal static Task<Plan?> FreePlanAsync(BillingDbContext db, CancellationToken ct) =>
        db.Plans.AsNoTracking().Where(p => p.Status == PlanStatus.Published && p.IsFree).OrderBy(p => p.MonthlyPrice).ThenBy(p => p.SortOrder).FirstOrDefaultAsync(ct);

    private async Task<Entitlements> ReloadAsync(Guid workspaceId, CancellationToken ct)
    {
        await cache.RemoveByTagAsync(BillingCacheTags.Workspace(workspaceId), ct);
        await using var scope = scopes.CreateAsyncScope();
        return await LoadAsync(scope.ServiceProvider.GetRequiredService<BillingDbContext>(), workspaceId, clock.UtcNow, ct);
    }
}

/// <summary>
/// Adds <see cref="UsageRecorded"/> events to the period's counters (platform group). A distinct meter counts a subject once per period.
/// One message per transaction for now; the plan's batching comes with Phase 2 overage billing.
/// </summary>
internal sealed class UsageRecordedHandler(BillingDbContext db, IEntitlements entitlements) : IdempotentHandler<UsageRecorded, BillingDbContext>(db)
{
    protected override async Task HandleOnceAsync(UsageRecorded message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!BillingMeters.Counted.Contains(message.Meter) || message.Quantity <= 0)
        {
            return;
        }

        var period = BillingPeriod.For(message.OccurredAt);
        var quantity = (long)message.Quantity;
        if (BillingMeters.Distinct.Contains(message.Meter))
        {
            if (message.SubjectId is not { } subject)
            {
                return;
            }

            var added = await Db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO billing.usage_subjects (workspace_id, meter, period_start, subject_id) VALUES ({message.WorkspaceId}, {message.Meter}, {period.Start}, {subject}) ON CONFLICT DO NOTHING",
                cancellationToken);
            if (added == 0)
            {
                return;
            }

            quantity = 1;
        }

        await Db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO billing.usage_counters (workspace_id, meter, period_start, quantity, updated_at)
            VALUES ({message.WorkspaceId}, {message.Meter}, {period.Start}, {quantity}, now())
            ON CONFLICT (workspace_id, meter, period_start) DO UPDATE SET quantity = billing.usage_counters.quantity + EXCLUDED.quantity, updated_at = now()
            """,
            cancellationToken);

        if (message.Meter == BillingMeters.AiConversations)
        {
            await AlertAsync(message.WorkspaceId, period, cancellationToken);
        }
    }

    /// <summary>
    /// The owner hears once per period when the plan's AI conversations are used up (overage starts, or the AI stops when there is none)
    /// and once when the overage cap is reached (BE-2.8 acceptance: overage never surprises).
    /// </summary>
    private async Task AlertAsync(Guid workspaceId, BillingPeriod period, CancellationToken ct)
    {
        var plan = await entitlements.GetAsync(workspaceId, ct);
        if (plan.Limit(BillingMeters.AiConversations) is not { } limit)
        {
            return;
        }

        var used = await Db.UsageCounters.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(c => c.WorkspaceId == workspaceId && c.Meter == BillingMeters.AiConversations && c.PeriodStart == period.Start)
            .Select(c => c.Quantity).FirstOrDefaultAsync(ct);
        var settings = await Db.Settings.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct)
            ?? BillingSettings.Default(workspaceId);
        var overage = plan.OveragePrice is > 0 && settings.OverageEnabled;
        var day = period.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (used >= limit)
        {
            await NotifyOnceAsync(
                workspaceId, $"quota_used:{day}", "گفتگوهای هوش مصنوعی این ماه تمام شد",
                overage
                    ? "از این به بعد هر گفتگوی تازه با هزینه‌ی اضافه پاسخ داده می‌شود؛ سقف آن را در صورتحساب ببینید."
                    : "گفتگوهای تازه به همکاران شما سپرده می‌شود. برای ادامه‌ی پاسخ خودکار، طرح را ارتقا دهید یا هزینه‌ی اضافه را روشن کنید.",
                ct);
        }

        if (overage && used >= limit)
        {
            var cap = settings.Cap ?? OverageRules.DefaultCap(plan.MonthlyPrice);
            if ((used - limit + 1) * plan.OveragePrice!.Value > cap)
            {
                await NotifyOnceAsync(
                    workspaceId, $"overage_cap:{day}", "سقف هزینه‌ی اضافه پر شد",
                    "تا پایان این دوره گفتگوهای تازه به همکاران شما سپرده می‌شود. می‌توانید سقف را بالا ببرید یا طرح را ارتقا دهید.", ct);
            }
        }
    }

    private async Task NotifyOnceAsync(Guid workspaceId, string key, string title, string body, CancellationToken ct)
    {
        var added = await Db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO billing.billing_alerts (workspace_id, key, created_at) VALUES ({workspaceId}, {key}, now()) ON CONFLICT DO NOTHING", ct);
        if (added > 0)
        {
            Db.Publish(new NotificationRequested(workspaceId, null, BillingNotices.Kind, title, body, BillingNotices.Link), workspaceId.ToString(), workspaceId: workspaceId);
        }
    }
}

internal static class BillingNotices
{
    public const string Kind = "billing";

    public const string Link = "/billing";
}
