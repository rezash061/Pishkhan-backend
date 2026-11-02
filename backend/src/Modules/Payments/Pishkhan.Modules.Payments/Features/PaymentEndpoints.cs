using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Payments.Contracts;
using Pishkhan.Modules.Payments.Domain;
using Pishkhan.Modules.Payments.Persistence;
using Pishkhan.Modules.Payments.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Payments.Features;

/// <summary>
/// <c>merchantId</c> is the 36-character merchant ID from the Zarinpal panel. Leave it out to keep the stored one (to switch
/// <c>sandbox</c> or <c>enabled</c>). <c>sandbox</c> uses Zarinpal's test gateway, where no money moves.
/// </summary>
public sealed record MerchantAccountRequest(string? Provider, string? MerchantId, bool Sandbox = false, bool Enabled = true);

/// <summary><c>merchantIdHint</c> is the last four characters; the full ID is never returned.</summary>
public sealed record MerchantAccountResponse(bool Configured, string? Provider, string? MerchantIdHint, bool Sandbox, bool Enabled, DateTimeOffset? UpdatedAt);

/// <summary><c>status</c>: <c>active</c>, <c>paid</c>, <c>cancelled</c> or <c>expired</c>. <c>lastError</c> is the last failed attempt's code.</summary>
public sealed record PaymentLinkResponse(
    Guid Id, Guid OrderId, long OrderNumber, string Url, Money Amount, string Provider, string Status, int Attempts, string? LastError, DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, string? ReferenceId, string? CardMask);

internal static class PaymentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}").WithTags("Payments");

        ws.MapGet("/payments/merchant-account", GetMerchantAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("The workspace's payment gateway account")
            .Produces<MerchantAccountResponse>();

        ws.MapPut("/payments/merchant-account", PutMerchantAsync)
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithSummary("Connect or update the workspace's Zarinpal merchant account")
            .Produces<MerchantAccountResponse>()
            .WithStandardProblems();

        ws.MapDelete("/payments/merchant-account", DeleteMerchantAsync)
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithSummary("Disconnect the merchant account; open payment links stop working")
            .Produces(StatusCodes.Status204NoContent);

        ws.MapPost("/orders/{orderId:guid}/payment-links", CreateLinkAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithIdempotency()
            .WithSummary("Create a payment link for an order")
            .WithDescription("A complete draft moves to `awaiting_payment`. The link is for the order's total now; a newer link cancels older ones, and the order going back to draft or being cancelled cancels it. 422 `payments.merchant_missing` without an enabled merchant account; 409 `orders.not_payable`; 400 `orders.incomplete`.")
            .Produces<PaymentLinkResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        ws.MapGet("/orders/{orderId:guid}/payment-links", ListLinksAsync)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .WithSummary("The order's payment links, newest first")
            .Produces<IReadOnlyList<PaymentLinkResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost("/payment-links/{linkId:guid}/cancel", CancelLinkAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithSummary("Cancel an active payment link")
            .Produces<PaymentLinkResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    public static PaymentLinkResponse ToResponse(PaymentLink l, PaymentsOptions options, DateTimeOffset now) => new(
        l.Id, l.OrderId, l.OrderNumber, $"{options.PublicBaseUrl.TrimEnd('/')}/{l.Token}", Money.Rial(l.Amount), l.Provider, l.StatusAt(now), l.Attempts, l.LastError,
        l.ExpiresAt, l.CreatedAt, l.PaidAt, l.ReferenceId, l.CardMask);

    private static MerchantAccountResponse ToResponse(MerchantAccount? a) =>
        a is null ? new(false, null, null, false, false, null) : new(true, a.Provider, a.MerchantIdHint, a.Sandbox, a.Enabled, a.UpdatedAt);

    private static async Task<IResult> GetMerchantAsync(Guid workspaceId, PaymentsDbContext db, CancellationToken ct) =>
        Results.Ok(ToResponse(await db.MerchantAccounts.AsNoTracking().FirstOrDefaultAsync(ct)));

    private static async Task<IResult> PutMerchantAsync(
        Guid workspaceId, MerchantAccountRequest request, PaymentsDbContext db, MerchantSecrets secrets, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var provider = request.Provider ?? PaymentProviders.Zarinpal;
        if (!PaymentProviders.All.Contains(provider))
        {
            return Invalid("provider", "payments.provider_unknown");
        }

        var merchantId = request.MerchantId?.Trim();
        if (merchantId is not null && !Guid.TryParseExact(merchantId, "D", out _))
        {
            return Invalid("merchantId", "payments.merchant_id_invalid");
        }

        var account = await db.MerchantAccounts.FirstOrDefaultAsync(ct);
        if (account is null && merchantId is null)
        {
            return Invalid("merchantId", "payments.merchant_id_required");
        }

        var now = clock.UtcNow;
        var created = account is null;
        if (account is null)
        {
            account = MerchantAccount.Create(workspaceId, provider, now);
            db.MerchantAccounts.Add(account);
        }

        account.Update(provider, merchantId is null ? null : secrets.Protect(merchantId.ToLowerInvariant()), request.Sandbox, request.Enabled, now);
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), created ? "payments.merchant_connected" : "payments.merchant_updated", "merchant_account",
            account.Id.ToString(), workspaceId, now, after: new { provider, hint = account.MerchantIdHint, request.Sandbox, request.Enabled, merchantIdChanged = merchantId is not null });
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(account));
    }

    private static async Task<IResult> DeleteMerchantAsync(Guid workspaceId, PaymentsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var account = await db.MerchantAccounts.FirstOrDefaultAsync(ct);
        if (account is not null)
        {
            db.MerchantAccounts.Remove(account);
            var entry = AuditEntries.Create(
                AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), "payments.merchant_disconnected", "merchant_account", account.Id.ToString(),
                workspaceId, clock.UtcNow, before: new { account.Provider, hint = account.MerchantIdHint });
            db.Publish(entry, entry.PartitionKey(), workspaceId);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> CreateLinkAsync(
        Guid workspaceId, Guid orderId, PaymentsDbContext db, IOrderCommands orders, IWorkspaceQueries workspaces, IOptions<PaymentsOptions> options, ICurrentUser caller,
        IClock clock, CancellationToken ct)
    {
        var ready = await orders.RequestPaymentAsync(orderId, caller.UserId, ct);
        switch (ready.Error)
        {
            case "orders.not_found":
                return Error.NotFound().ToProblem();
            case "orders.not_payable":
                return Error.Conflict("orders.not_payable").ToProblem();
            case "orders.incomplete":
                return Invalid("items", "orders.incomplete");
        }

        var account = await db.MerchantAccounts.AsNoTracking().FirstOrDefaultAsync(ct);
        if (account is not { Enabled: true })
        {
            return Error.Unprocessable("payments.merchant_missing").ToProblem();
        }

        var order = ready.Order!;
        var now = clock.UtcNow;
        foreach (var older in await db.Links.Where(l => l.OrderId == orderId && l.Status == PaymentLinkStatuses.Active).ToListAsync(ct))
        {
            older.Cancel(now);
        }

        var link = PaymentLink.Create(
            workspaceId, order.Id, order.Number, await workspaces.GetNameAsync(workspaceId, ct),
            [.. order.Lines.Select(l => new PaymentLinkLine(l.Title, l.VariantLabel, l.Quantity, l.LineTotal.Amount))],
            order.Total.Amount, account.Provider, options.Value.LinkLifetime, caller.UserId, now);
        db.Links.Add(link);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(link, options.Value, now), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListLinksAsync(
        Guid workspaceId, Guid orderId, PaymentsDbContext db, IOrderQueries orders, IOptions<PaymentsOptions> options, IClock clock, CancellationToken ct)
    {
        if (await orders.GetForPaymentAsync(orderId, ct) is null)
        {
            return Error.NotFound().ToProblem();
        }

        var links = await db.Links.AsNoTracking().Where(l => l.OrderId == orderId).OrderByDescending(l => l.CreatedAt).Take(100).ToListAsync(ct);
        var now = clock.UtcNow;
        return Results.Ok(links.Select(l => ToResponse(l, options.Value, now)).ToList());
    }

    private static async Task<IResult> CancelLinkAsync(Guid workspaceId, Guid linkId, PaymentsDbContext db, IOptions<PaymentsOptions> options, IClock clock, CancellationToken ct)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        if (!link.Cancel(now))
        {
            return Error.Conflict("payments.link_not_active").ToProblem();
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("payments.link_not_active").ToProblem();
        }

        return Results.Ok(ToResponse(link, options.Value, now));
    }

    private static IResult Invalid(string field, string code) =>
        Error.Validation(code, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [code] }).ToProblem();
}
