using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.Modules.Billing.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Billing.Features;

/// <summary>
/// The workspace's overage this period. <c>available</c>: the plan has an overage price (a trial or the free plan has none).
/// <c>cap</c> is the most overage per period (the plan's monthly price unless set; <c>capIsDefault</c>). <c>included</c> and
/// <c>used</c> are AI conversations; <c>extra</c> those beyond the plan, costing <c>amount</c> so far. <c>due</c> is unpaid overage from
/// earlier periods; <c>blocked</c>: some of it is more than 14 days old, so overage is stopped until it is paid. <c>state</c>:
/// <c>within</c>, <c>overage</c>, or why new conversations go to a person (<c>plan_limit</c>, <c>overage_cap</c>, <c>overage_unpaid</c>).
/// </summary>
public sealed record OverageResponse(
    bool Available, bool Enabled, Money? PricePerConversation, Money Cap, bool CapIsDefault, int? Included, long Used, long Extra, Money Amount, Money Due,
    bool Blocked, string State, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);

/// <summary><c>cap</c> in Rial: null for the default (the plan's monthly price), else 0 to 10 times the monthly price.</summary>
public sealed record UpdateOverageRequest(bool Enabled, long? Cap);

/// <summary>A closed period's overage. <c>status</c>: due or settled (paid with the checkout <c>checkoutId</c>).</summary>
public sealed record OverageChargeResponse(
    Guid Id, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, int Conversations, Money UnitPrice, Money Amount, string Status, Guid? CheckoutId,
    DateTimeOffset? SettledAt, DateTimeOffset CreatedAt);

/// <summary>Overage billing (BE-2.8, gap G31): settings and cap, charges per closed period, and paying them.</summary>
internal static class OverageEndpoints
{
    public const int MaxCapMultiple = 10;

    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/billing/overage").WithTags("Billing");

        ws.MapGet("/", GetAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Overage this period: price, cap, conversations beyond the plan and what is owed")
            .Produces<OverageResponse>()
            .WithStandardProblems();

        ws.MapPut("/", UpdateAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithSummary("Turn overage on or off and set its cap")
            .WithDescription("Off, or at the cap, new AI conversations go to a person (the AI keeps answering conversations it already took this period). 400 `billing.overage.cap_invalid`.")
            .Produces<OverageResponse>()
            .ProducesValidationProblem()
            .WithStandardProblems();

        ws.MapGet("/charges", ChargesAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithSummary("Overage charges of closed periods, newest first (24)")
            .Produces<IReadOnlyList<OverageChargeResponse>>();

        ws.MapPost("/checkout", CheckoutAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithIdempotency()
            .WithSummary("Pay every due overage charge: opens the payment at Zarinpal")
            .WithDescription("422 `billing.overage.nothing_due`, `billing.checkout.unavailable` or `billing.checkout.gateway_unavailable`; 409 `billing.overage.checkout_pending` (another overage payment is open, up to an hour). The charges are settled when Zarinpal confirms the payment; the checkout's kind is `overage`.")
            .Produces<StartCheckoutResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();
    }

    internal static async Task<OverageResponse> ReadAsync(Guid workspaceId, BillingDbContext db, IEntitlements entitlements, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var period = BillingPeriod.For(now);
        var plan = await entitlements.GetAsync(workspaceId, ct);
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct) ?? BillingSettings.Default(workspaceId);
        var included = plan.Limit(BillingMeters.AiConversations);
        var used = await entitlements.GetUsageAsync(workspaceId, BillingMeters.AiConversations, ct);
        var cap = settings.Cap ?? OverageRules.DefaultCap(plan.MonthlyPrice);
        var extra = included is { } i ? Math.Max(0, used - i) : 0;
        var price = plan.OveragePrice is > 0 ? plan.OveragePrice : null;
        var due = await db.OverageCharges.AsNoTracking().Where(c => c.Status == OverageChargeStatuses.Due).SumAsync(c => (long?)c.Amount, ct) ?? 0;
        var overdue = now - OverageRules.UnpaidGrace;
        var blocked = await db.OverageCharges.AsNoTracking().AnyAsync(c => c.Status == OverageChargeStatuses.Due && c.PeriodEnd < overdue, ct);

        // What a new conversation would get now (the reply check also counts ai_messages).
        var next = OverageRules.Decide(included, used, false, price, settings.OverageEnabled, cap, blocked);
        var state = next.Reason ?? (next.Overage ? "overage" : "within");
        return new OverageResponse(
            price is not null, settings.OverageEnabled, price is { } p ? Money.Rial(p) : null, Money.Rial(cap), settings.Cap is null, included, used, extra,
            Money.Rial(price is { } q ? OverageRules.Charge(extra, q, cap) : 0), Money.Rial(due), blocked, state, period.Start, period.End);
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, BillingDbContext db, IEntitlements entitlements, IClock clock, CancellationToken ct) =>
        Results.Ok(await ReadAsync(workspaceId, db, entitlements, clock, ct));

    private static async Task<IResult> UpdateAsync(
        Guid workspaceId, UpdateOverageRequest request, BillingDbContext db, IEntitlements entitlements, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var plan = await entitlements.GetAsync(workspaceId, ct);
        if (request.Cap is { } cap && (cap < 0 || cap > Math.Max(plan.MonthlyPrice, CheckoutPlanner.MinimumAmount) * MaxCapMultiple))
        {
            return PlanEndpoints.Invalid("cap", "billing.overage.cap_invalid");
        }

        var now = clock.UtcNow;
        var settings = await db.Settings.FirstOrDefaultAsync(ct);
        var before = settings is null ? null : new { enabled = settings.OverageEnabled, cap = settings.Cap };
        if (settings is null)
        {
            settings = BillingSettings.Default(workspaceId);
            db.Settings.Add(settings);
        }

        settings.OverageEnabled = request.Enabled;
        settings.Cap = request.Cap;
        settings.UpdatedAt = now;
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), "billing.overage_updated", "billing_settings", workspaceId.ToString(), workspaceId, now,
            before: before, after: new { enabled = request.Enabled, cap = request.Cap });
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ReadAsync(workspaceId, db, entitlements, clock, ct));
    }

    private static async Task<IResult> ChargesAsync(Guid workspaceId, BillingDbContext db, CancellationToken ct)
    {
        var rows = await db.OverageCharges.AsNoTracking().OrderByDescending(c => c.PeriodStart).Take(24).ToListAsync(ct);
        return Results.Ok(rows.Select(c => new OverageChargeResponse(
            c.Id, c.PeriodStart, c.PeriodEnd, c.Conversations, Money.Rial(c.UnitPrice), Money.Rial(c.Amount), c.Status, c.CheckoutId, c.SettledAt, c.CreatedAt)).ToList());
    }

    private static async Task<IResult> CheckoutAsync(Guid workspaceId, CheckoutService checkouts, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var started = await checkouts.StartOverageAsync(workspaceId, caller.UserId, ct);
        return started.Error switch
        {
            null => Results.Json(
                new StartCheckoutResponse(CheckoutEndpoints.ToResponse(started.Checkout!, clock.UtcNow), started.PaymentUrl!.ToString()), statusCode: StatusCodes.Status201Created),
            "billing.overage.checkout_pending" => Error.Conflict(started.Error).ToProblem(),
            _ => Error.Unprocessable(started.Error).ToProblem(),
        };
    }
}
