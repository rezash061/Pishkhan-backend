using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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
/// One wallet change. <c>kind</c>: <c>top_up</c> (paid through Zarinpal), <c>adjustment</c> (by the Pishkhan team, with <c>reason</c>),
/// <c>refund</c> (a refund paid into the wallet), <c>checkout</c> (spent) or <c>released</c> (given back from a checkout that was not paid
/// or was refunded). <c>amount</c> is signed.
/// </summary>
public sealed record WalletEntryResponse(Guid Id, string Kind, Money Amount, Money BalanceAfter, string? Reason, Guid? CheckoutId, string ActorKind, DateTimeOffset CreatedAt);

/// <summary>The wallet balance and its latest changes (newest first, at most 100).</summary>
public sealed record WalletResponse(Money Balance, IReadOnlyList<WalletEntryResponse> Entries);

/// <summary><c>amount</c> in Rial, 100,000 (10,000 Toman) to 1,000,000,000.</summary>
public sealed record WalletTopUpRequest(long Amount);

/// <summary>The seller's wallet (admin plan 4.4, user panel billing): read it and top it up through Zarinpal. Spent with <c>useWallet</c> at checkout.</summary>
internal static class WalletEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/billing/wallet").WithTags("Billing");

        ws.MapGet(string.Empty, GetAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithSummary("The wallet balance and its latest changes")
            .Produces<WalletResponse>();

        ws.MapPost("/top-up", TopUpAsync)
            .RequireWorkspacePermission(Permissions.BillingManage)
            .WithIdempotency()
            .WithSummary("Pay money into the wallet: opens the payment at Zarinpal")
            .WithDescription("400 on `amount` outside 100,000 to 1,000,000,000 Rial; 422 `billing.checkout.unavailable` or `billing.checkout.gateway_unavailable`. " +
                "The balance grows when Zarinpal confirms the payment; the checkout (kind `wallet_top_up`) is the invoice.")
            .Produces<StartCheckoutResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems()
            .WithExamples(new WalletTopUpRequest(5_000_000));
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, BillingDbContext db, WalletLedger wallets, CancellationToken ct)
    {
        var wallet = await wallets.SettledAsync(workspaceId, ct);
        if (db.ChangeTracker.HasChanges() && db.Entry(wallet).State != EntityState.Added)
        {
            // Holds of checkouts left unpaid were given back.
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(await ReadAsync(db, workspaceId, wallet.Balance, ct));
    }

    internal static async Task<WalletResponse> ReadAsync(BillingDbContext db, Guid workspaceId, long balance, CancellationToken ct)
    {
        var entries = await db.WalletEntries.AsNoTracking().Where(e => e.WorkspaceId == workspaceId)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(100).ToListAsync(ct);
        return new WalletResponse(Money.Rial(balance), [.. entries.Select(ToResponse)]);
    }

    internal static WalletEntryResponse ToResponse(WalletEntry e) =>
        new(e.Id, e.Kind, Money.Rial(e.Amount), Money.Rial(e.BalanceAfter), e.Reason, e.CheckoutId, e.ActorKind, e.CreatedAt);

    private static async Task<IResult> TopUpAsync(Guid workspaceId, WalletTopUpRequest request, CheckoutService checkouts, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (request.Amount is < CheckoutService.MinTopUp or > CheckoutService.MaxTopUp)
        {
            return PlanEndpoints.Invalid("amount", "validation.out_of_range");
        }

        var started = await checkouts.StartTopUpAsync(workspaceId, request.Amount, caller.UserId, ct);
        return started.Error is not null
            ? Error.Unprocessable(started.Error).ToProblem()
            : Results.Json(new StartCheckoutResponse(CheckoutEndpoints.ToResponse(started.Checkout!, clock.UtcNow), started.PaymentUrl!.ToString()), statusCode: StatusCodes.Status201Created);
    }
}
