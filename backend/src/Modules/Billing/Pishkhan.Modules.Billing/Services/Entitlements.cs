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

    internal static async Task<Entitlements> LoadAsync(BillingDbContext db, Guid workspaceId, DateTimeOffset now, CancellationToken ct)
    {
        var subscription = await db.Subscriptions.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active && (s.EndsAt == null || s.EndsAt > now), ct);
        if (subscription is not null && await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == subscription.PlanId, ct) is { } plan)
        {
            return new Entitlements(
                workspaceId, "subscription", plan.Id, plan.Code, plan.Name, plan.VersionNumber, subscription.Trial, subscription.EndsAt, new(plan.Limits, StringComparer.Ordinal));
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
internal sealed class UsageRecordedHandler(BillingDbContext db) : IdempotentHandler<UsageRecorded, BillingDbContext>(db)
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
    }
}
