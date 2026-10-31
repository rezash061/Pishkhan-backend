using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.Modules.Billing.Services;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Billing.Features;

/// <summary>A subscription. <c>state</c>: active, trialing, expired (past its end), ended. <c>endReason</c>: replaced, cancelled, expired.</summary>
public sealed record SubscriptionResponse(
    Guid Id, Guid PlanId, string PlanCode, int PlanVersion, string State, bool Trial, DateTimeOffset StartsAt, DateTimeOffset? EndsAt,
    DateTimeOffset? EndedAt, string? EndReason);

/// <summary>
/// The workspace's plan now. <c>source</c>: subscription (granted plan or trial), free (no subscription: the published free plan) or
/// none (no free plan is published; nothing is limited). <c>limits</c> maps a meter to its limit per period; null is unlimited.
/// </summary>
public sealed record CurrentPlanResponse(
    string Source, Guid? PlanId, string? PlanCode, string? PlanName, int? PlanVersion, bool Trial, DateTimeOffset? EndsAt,
    IReadOnlyDictionary<string, int?> Limits, SubscriptionResponse? Subscription);

/// <summary>One meter: <c>used</c> this period (current count for channel_accounts and seats) against <c>limit</c> (null: unlimited).</summary>
public sealed record UsageMeter(string Meter, long Used, int? Limit);

/// <summary>Usage in the current billing period, a Jalali month in Tehran time (<c>periodStart</c> inclusive, <c>periodEnd</c> exclusive).</summary>
public sealed record UsageResponse(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, IReadOnlyList<UsageMeter> Meters);

public sealed record AdminWorkspaceBilling(CurrentPlanResponse Current, UsageResponse Usage, IReadOnlyList<SubscriptionResponse> History);

/// <summary>
/// Grants a published plan version. The end is <c>endsAt</c>, or <c>days</c> (1 to 1095) from now, or none (until cancelled; not for a
/// trial). It replaces the current subscription. <c>reason</c>: 5 to 300 characters.
/// </summary>
public sealed record GrantPlanRequest(Guid PlanId, bool Trial, DateTimeOffset? EndsAt, int? Days, string Reason);

/// <summary>Moves the current subscription's end <c>days</c> (1 to 365) later; an expired one is extended from now.</summary>
public sealed record ExtendSubscriptionRequest(int Days, string Reason);

public sealed record SubscriptionActionRequest(string Reason);

/// <summary>Subscriptions, plan grants and usage (BE-1.11; admin plan 4.2 and 4.4 basic). Paid checkout is Phase 2 (BE-2.8).</summary>
internal static class SubscriptionEndpoints
{
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Finance, StaffRoles.Support, StaffRoles.ReadOnly];

    public static void MapPanel(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/billing").WithTags("Billing");

        ws.MapGet("/subscription", GetCurrentAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("The workspace's plan and its limits")
            .WithDescription("Without a subscription the workspace is on the free plan. During the beta plans are granted by the Pishkhan team.")
            .Produces<CurrentPlanResponse>()
            .WithStandardProblems();

        ws.MapGet("/usage", GetUsageAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Usage this billing period against the plan's limits")
            .WithDescription("Every meter: ai_conversations, ai_messages, receipt_verifications and sms count this period; channel_accounts and seats (members plus pending invitations) are current counts.")
            .Produces<UsageResponse>()
            .WithStandardProblems();
    }

    public static void MapAdmin(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Billing");
        admin.MapGroup("/workspaces/{workspaceId:guid}")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers))
            .MapGet("/billing", GetAdminAsync)
            .WithSummary("A workspace's plan, usage this period and subscription history")
            .WithDescription("Roles: SuperAdmin, Finance, Support, ReadOnly. History is newest first.")
            .Produces<AdminWorkspaceBilling>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        var write = admin.MapGroup("/workspaces/{workspaceId:guid}/subscription").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Finance));
        write.MapPost("/", GrantAsync)
            .RequireStepUp()
            .WithSummary("Grant a plan or a trial")
            .WithDescription("Finance or SuperAdmin, step-up. `planId` is a published version. Replaces the current subscription; audited in the workspace as subscription.grant.")
            .Produces<SubscriptionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        write.MapPost("/extend", ExtendAsync)
            .RequireStepUp()
            .WithSummary("Extend the current subscription or trial")
            .WithDescription("Finance or SuperAdmin, step-up. 409 `billing.subscription.none` without a subscription, `billing.subscription.not_extendable` when it has no end. Audited as subscription.extend.")
            .Produces<SubscriptionResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        write.MapPost("/cancel", CancelAsync)
            .RequireStepUp()
            .WithSummary("End the current subscription now; the workspace returns to the free plan")
            .WithDescription("Finance or SuperAdmin, step-up. Audited as subscription.cancel.")
            .Produces<SubscriptionResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
    }

    private static async Task<IResult> GetCurrentAsync(Guid workspaceId, BillingDbContext db, IClock clock, CancellationToken ct) =>
        Results.Ok(await CurrentAsync(db, workspaceId, clock.UtcNow, ct));

    private static async Task<IResult> GetUsageAsync(
        Guid workspaceId, BillingDbContext db, IEntitlements entitlements, IChannelDirectory channels, IMembershipQueries members, IClock clock, CancellationToken ct)
    {
        var limits = await entitlements.GetAsync(workspaceId, ct);
        var accounts = (await channels.ListAsync(ct)).Count;
        return Results.Ok(await UsageAsync(db, limits, accounts, await members.CountSeatsAsync(workspaceId, ct), clock.UtcNow, ct));
    }

    private static async Task<IResult> GetAdminAsync(
        Guid workspaceId, BillingDbContext db, IMembershipQueries members, IChannelDirectory channels, IClock clock, CancellationToken ct)
    {
        if (await members.GetOwnerIdAsync(workspaceId, ct) is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        var current = await CurrentAsync(db, workspaceId, now, ct);
        var limits = await EntitlementService.LoadAsync(db, workspaceId, now, ct);
        var accounts = (await channels.ListForWorkspacesAsync([workspaceId], ct)).Count(a => a.Status != ChannelStatuses.Disconnected);
        var usage = await UsageAsync(db, limits, accounts, await members.CountSeatsAsync(workspaceId, ct), now, ct);
        var history = await Subscriptions(db).Where(s => s.WorkspaceId == workspaceId).OrderByDescending(s => s.CreatedAt).Take(50).ToListAsync(ct);
        return Results.Ok(new AdminWorkspaceBilling(current, usage, [.. history.Select(s => ToResponse(s, now))]));
    }

    private static async Task<IResult> GrantAsync(
        Guid workspaceId, GrantPlanRequest request, BillingDbContext db, IMembershipQueries members, SubscriptionAudit audit, ICacheInvalidator cache,
        IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (PlanEndpoints.Reason(request.Reason, required: true) is { } invalid)
        {
            return invalid;
        }

        if (request.EndsAt is not null && request.Days is not null)
        {
            return PlanEndpoints.Invalid("days", "billing.subscription.end_twice");
        }

        if (request.Days is { } days && days is < 1 or > 1095)
        {
            return PlanEndpoints.Invalid("days", "validation.range");
        }

        if (request.EndsAt is { } endsAt && (endsAt <= now || endsAt > now.AddYears(3)))
        {
            return PlanEndpoints.Invalid("endsAt", "validation.range");
        }

        var end = request.EndsAt ?? (request.Days is { } d ? now.AddDays(d) : null);
        if (request.Trial && end is null)
        {
            return PlanEndpoints.Invalid("days", "billing.subscription.trial_needs_end");
        }

        if (await members.GetOwnerIdAsync(workspaceId, ct) is null)
        {
            return Error.NotFound().ToProblem();
        }

        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PlanId, ct);
        if (plan is null)
        {
            return Error.NotFound("billing.plan.not_found").ToProblem();
        }

        if (plan.Status != PlanStatus.Published)
        {
            return Error.Conflict("billing.plan.not_published").ToProblem();
        }

        db.Tenant.SetWorkspace(workspaceId);
        var strategy = db.Database.CreateExecutionStrategy();
        var granted = await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            // End the current one first: the unique index allows one active subscription per workspace.
            var previous = await db.Subscriptions.FirstOrDefaultAsync(s => s.Status == SubscriptionStatus.Active, token);
            previous?.End(Subscription.EndReplaced, now);
            await db.SaveChangesAsync(token);

            var subscription = Subscription.Grant(workspaceId, plan, request.Trial, end, audit.StaffId, now);
            db.Subscriptions.Add(subscription);
            await audit.RecordAsync(db, subscription, "subscription.grant", request.Reason, token, previous is null ? null : ToResponse(previous, now));
            cache.Enqueue(db, BillingCacheTags.Workspace(workspaceId));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return subscription;
        }, ct);

        await cache.InvalidateLocalAsync([BillingCacheTags.Workspace(workspaceId)], ct);
        return Results.Ok(ToResponse(granted, now));
    }

    private static async Task<IResult> ExtendAsync(
        Guid workspaceId, ExtendSubscriptionRequest request, BillingDbContext db, SubscriptionAudit audit, ICacheInvalidator cache, IClock clock, CancellationToken ct)
    {
        if (PlanEndpoints.Reason(request.Reason, required: true) is { } invalid)
        {
            return invalid;
        }

        if (request.Days is < 1 or > 365)
        {
            return PlanEndpoints.Invalid("days", "validation.range");
        }

        db.Tenant.SetWorkspace(workspaceId);
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.Status == SubscriptionStatus.Active, ct);
        if (subscription is null)
        {
            return Error.Conflict("billing.subscription.none").ToProblem();
        }

        if (subscription.EndsAt is null)
        {
            return Error.Conflict("billing.subscription.not_extendable").ToProblem();
        }

        var now = clock.UtcNow;
        var before = ToResponse(subscription, now);
        subscription.Extend(TimeSpan.FromDays(request.Days), now);
        await audit.RecordAsync(db, subscription, "subscription.extend", request.Reason, ct, before);
        cache.Enqueue(db, BillingCacheTags.Workspace(workspaceId));
        await db.SaveChangesAsync(ct);
        await cache.InvalidateLocalAsync([BillingCacheTags.Workspace(workspaceId)], ct);
        return Results.Ok(ToResponse(subscription, now));
    }

    private static async Task<IResult> CancelAsync(
        Guid workspaceId, SubscriptionActionRequest request, BillingDbContext db, SubscriptionAudit audit, ICacheInvalidator cache, IClock clock, CancellationToken ct)
    {
        if (PlanEndpoints.Reason(request.Reason, required: true) is { } invalid)
        {
            return invalid;
        }

        db.Tenant.SetWorkspace(workspaceId);
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.Status == SubscriptionStatus.Active, ct);
        if (subscription is null)
        {
            return Error.Conflict("billing.subscription.none").ToProblem();
        }

        var now = clock.UtcNow;
        var before = ToResponse(subscription, now);
        subscription.End(Subscription.EndCancelled, now);
        await audit.RecordAsync(db, subscription, "subscription.cancel", request.Reason, ct, before);
        cache.Enqueue(db, BillingCacheTags.Workspace(workspaceId));
        await db.SaveChangesAsync(ct);
        await cache.InvalidateLocalAsync([BillingCacheTags.Workspace(workspaceId)], ct);
        return Results.Ok(ToResponse(subscription, now));
    }

    private static async Task<CurrentPlanResponse> CurrentAsync(BillingDbContext db, Guid workspaceId, DateTimeOffset now, CancellationToken ct)
    {
        var limits = await EntitlementService.LoadAsync(db, workspaceId, now, ct);
        var subscription = await Subscriptions(db)
            .Where(s => s.WorkspaceId == workspaceId && s.Status == SubscriptionStatus.Active)
            .FirstOrDefaultAsync(ct);
        return new CurrentPlanResponse(
            limits.Source, limits.PlanId, limits.PlanCode, limits.PlanName, limits.PlanVersion, limits.Trial, limits.EndsAt, limits.Limits,
            subscription is null ? null : ToResponse(subscription, now));
    }

    private static async Task<UsageResponse> UsageAsync(
        BillingDbContext db, Entitlements limits, int channelAccounts, int seats, DateTimeOffset now, CancellationToken ct)
    {
        var period = BillingPeriod.For(now);
        var counted = await db.UsageCounters.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(u => u.WorkspaceId == limits.WorkspaceId && u.PeriodStart == period.Start)
            .ToDictionaryAsync(u => u.Meter, u => u.Quantity, ct);
        var meters = BillingMeters.All.Select(meter => new UsageMeter(
            meter,
            meter switch
            {
                BillingMeters.ChannelAccounts => channelAccounts,
                BillingMeters.Seats => seats,
                _ => counted.GetValueOrDefault(meter),
            },
            limits.Limit(meter))).ToList();
        return new UsageResponse(period.Start, period.End, meters);
    }

    private static IQueryable<Subscription> Subscriptions(BillingDbContext db) =>
        db.Subscriptions.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static SubscriptionResponse ToResponse(Subscription s, DateTimeOffset now) =>
        new(s.Id, s.PlanId, s.PlanCode, s.PlanVersion, s.StateName(now), s.Trial, s.StartsAt, s.EndsAt, s.EndedAt, s.EndReason);
}

/// <summary>Subscription changes are audited in the workspace, so the owner's audit view shows them, by the signed-in staff member.</summary>
internal sealed class SubscriptionAudit(ICurrentUser caller, IStaffDirectory staff, IClock clock)
{
    public Guid StaffId => caller.UserId;

    public async Task RecordAsync(ModuleDbContext db, Subscription subscription, string action, string reason, CancellationToken ct, object? before = null)
    {
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), action, "subscription", subscription.Id.ToString(),
            subscription.WorkspaceId, clock.UtcNow,
            before: before,
            after: new { plan = subscription.PlanCode, version = subscription.PlanVersion, subscription.Trial, subscription.EndsAt, state = subscription.StateName(clock.UtcNow) },
            reason: reason.Trim());
        db.Publish(entry, entry.PartitionKey(), workspaceId: subscription.WorkspaceId);
    }
}
