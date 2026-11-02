using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.Modules.Billing.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Billing.Features;

/// <summary><c>cycle</c>: <c>monthly</c> (30 days, the default) or <c>annual</c> (365 days, when the plan has a yearly price).</summary>
public sealed record CheckoutRequest(Guid PlanId, string? Cycle);

/// <summary>
/// What buying the plan costs now. <c>kind</c>: <c>new</c> (starts now), <c>renewal</c> (same plan, starts when the paid time ends),
/// <c>upgrade</c> (starts now; <c>credit</c> is the unused part of the current period) or <c>downgrade</c> (starts when the paid time ends).
/// <c>amount</c> is what is paid: <c>price</c> minus <c>credit</c>, at least 1,000 Toman.
/// </summary>
public sealed record CheckoutQuoteResponse(
    string Kind, Guid PlanId, string PlanCode, string PlanName, string Cycle, Money Price, Money Credit, Money Amount, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// A checkout, and once paid the invoice. <c>status</c>: pending, paid, failed or expired (not paid within an hour). <c>error</c> is
/// Zarinpal's code for a failed one, or <c>cancelled</c> when the payer left the gateway.
/// </summary>
public sealed record CheckoutResponse(
    Guid Id, string Kind, Guid PlanId, string PlanCode, string PlanName, int PlanVersion, string Cycle, Money Price, Money Credit, Money Amount,
    string Status, string? Error, long? InvoiceNumber, string? ReferenceId, string? CardMask, Guid? SubscriptionId, Guid CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt);

/// <summary><c>paymentUrl</c> is Zarinpal's page; send the browser there. Zarinpal returns the payer to the panel's billing page with <c>?checkout={id}</c>.</summary>
public sealed record StartCheckoutResponse(CheckoutResponse Checkout, string PaymentUrl);

/// <summary>
/// A plan on sale as the panel's billing page shows it. <c>current</c>: the workspace is on this plan now (the free plan when nothing is
/// bought or granted, or during a trial). <c>kind</c>: what buying it (monthly, or annual when that is the only price) would be (new, renewal, upgrade or downgrade), or null when it cannot be bought now; then <c>unavailable</c> is the error code a
/// quote would give.
/// </summary>
public sealed record BillingPlanOption(PublicPlan Plan, bool Current, string? Kind, string? Unavailable);

/// <summary>Self-serve subscription checkout through Zarinpal into Pishkhan's own account (BE-2.8 checkout).</summary>
internal static class CheckoutEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/billing").WithTags("Billing");

        ws.MapGet("/plans", PlansAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithSummary("Plans on sale, in display order, with what buying each would be for this workspace")
            .Produces<IReadOnlyList<BillingPlanOption>>();

        ws.MapPost("/checkout/quote", QuoteAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithSummary("What buying a plan costs now, and when it would start")
            .WithDescription("404 `billing.plan.not_found`; 422 `billing.checkout.plan_not_for_sale` or `billing.checkout.cycle_not_offered`; 409 `billing.checkout.managed_subscription` (a plan granted by the Pishkhan team without an end) or `billing.checkout.scheduled_pending` (an upgrade while a later period is already paid for).")
            .Produces<CheckoutQuoteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        ws.MapPost("/checkout", StartAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithIdempotency()
            .WithSummary("Buy, renew or upgrade: opens the payment at Zarinpal")
            .WithDescription("The same errors as the quote, plus 422 `billing.checkout.unavailable` (online payment is not set up) and `billing.checkout.gateway_unavailable` (Zarinpal did not answer; try again). The subscription starts when Zarinpal confirms the payment.")
            .Produces<StartCheckoutResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        ws.MapGet("/checkouts", ListAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithSummary("Checkouts and invoices, newest first (100)")
            .Produces<IReadOnlyList<CheckoutResponse>>();

        ws.MapGet("/checkouts/{checkoutId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithSummary("One checkout; a paid one is the invoice")
            .Produces<CheckoutResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGroup("/billing/checkout").AllowAnonymous().ExcludeFromDescription().RequireRateLimiting(RateLimitPolicies.Public)
            .MapGet("/callback", CallbackAsync);
    }

    private static string Cycle(CheckoutRequest request) => request.Cycle ?? BillingCycles.Monthly;

    private static async Task<IResult> QuoteAsync(Guid workspaceId, CheckoutRequest request, CheckoutService checkouts, CancellationToken ct)
    {
        if (!BillingCycles.All.Contains(Cycle(request)))
        {
            return PlanEndpoints.Invalid("cycle", "billing.checkout.cycle_invalid");
        }

        var (quote, plan, error) = await checkouts.QuoteAsync(workspaceId, request.PlanId, Cycle(request), ct);
        return error is not null ? Problem(error) : Results.Ok(ToResponse(quote!, plan!));
    }

    private static async Task<IResult> PlansAsync(Guid workspaceId, BillingDbContext db, CheckoutService checkouts, CancellationToken ct)
    {
        var plans = await db.Plans.AsNoTracking().Where(p => p.Status == PlanStatus.Published && p.IsPublic)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.MonthlyPrice).ToListAsync(ct);
        var standing = await checkouts.StandingAsync(workspaceId, ct);
        return Results.Ok(plans.Select(plan =>
        {
            var (quote, error) = standing.Quote(plan, BillingCycles.Monthly);
            if (error == "billing.checkout.cycle_not_offered")
            {
                (quote, error) = standing.Quote(plan, BillingCycles.Annual);
            }

            var current = standing.Current is { Trial: false } s ? s.PlanCode == plan.Code : plan.IsFree;
            return new BillingPlanOption(PlanEndpoints.ToPublic(plan), current, quote?.Kind, error);
        }).ToList());
    }

    private static async Task<IResult> StartAsync(Guid workspaceId, CheckoutRequest request, CheckoutService checkouts, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (!BillingCycles.All.Contains(Cycle(request)))
        {
            return PlanEndpoints.Invalid("cycle", "billing.checkout.cycle_invalid");
        }

        var started = await checkouts.StartAsync(workspaceId, request.PlanId, Cycle(request), caller.UserId, ct);
        return started.Error is not null
            ? Problem(started.Error)
            : Results.Json(new StartCheckoutResponse(ToResponse(started.Checkout!, clock.UtcNow), started.PaymentUrl!.ToString()), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAsync(Guid workspaceId, BillingDbContext db, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await db.Checkouts.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(100).ToListAsync(ct);
        return Results.Ok(rows.Select(c => ToResponse(c, now)).ToList());
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid checkoutId, BillingDbContext db, IClock clock, CancellationToken ct) =>
        await db.Checkouts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == checkoutId, ct) is { } checkout
            ? Results.Ok(ToResponse(checkout, clock.UtcNow))
            : Error.NotFound().ToProblem();

    /// <summary>Zarinpal returns <c>?Authority=…&amp;Status=OK|NOK</c>; the payment is verified with Zarinpal either way it says OK.</summary>
    private static async Task<IResult> CallbackAsync(HttpContext http, CheckoutService checkouts, IOptions<CheckoutOptions> options)
    {
        var q = http.Request.Query;
        var checkout = await checkouts.CompleteAsync(q["Authority"].ToString(), string.Equals(q["Status"], "OK", StringComparison.Ordinal), http.RequestAborted);
        if (checkout is null)
        {
            return Results.NotFound();
        }

        var separator = options.Value.ReturnUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Location = $"{options.Value.ReturnUrl}{separator}checkout={checkout.Id}&status={checkout.Status}";
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static IResult Problem(string code) => code switch
    {
        "billing.plan.not_found" => Error.NotFound(code).ToProblem(),
        "billing.checkout.managed_subscription" or "billing.checkout.scheduled_pending" => Error.Conflict(code).ToProblem(),
        _ => Error.Unprocessable(code).ToProblem(),
    };

    private static CheckoutQuoteResponse ToResponse(CheckoutQuote q, Plan plan) => new(
        q.Kind, plan.Id, plan.Code, plan.Name, q.Cycle, Money.Rial(q.Price), Money.Rial(q.Credit), Money.Rial(q.Amount), q.StartsAt, q.EndsAt);

    private static CheckoutResponse ToResponse(Checkout c, DateTimeOffset now) => new(
        c.Id, c.Kind, c.PlanId, c.PlanCode, c.PlanName, c.PlanVersion, c.Cycle, Money.Rial(c.Price), Money.Rial(c.Credit), Money.Rial(c.Amount), c.StatusAt(now),
        c.Error, c.InvoiceNumber, c.ReferenceId, c.CardMask, c.SubscriptionId, c.CreatedBy, c.CreatedAt, c.PaidAt);
}
