using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.Modules.Billing.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Billing.Features;

/// <summary>Filters of the cross-workspace subscription list. <c>state</c>: current (active and trialing), trialing, scheduled, ended or all.</summary>
public sealed record AdminSubscriptionSearch(string? State, string? PlanCode, Guid? WorkspaceId, int? Page, int? PageSize);

public sealed record AdminSubscriptionRow(SubscriptionResponse Subscription, Guid WorkspaceId, string? WorkspaceName);

/// <summary>Filters of the payment list. <c>status</c>: paid (default), pending, failed, expired or all. <c>from</c> / <c>to</c> bound the creation time.</summary>
public sealed record PaymentSearch(string? Status, string? Kind, Guid? WorkspaceId, DateTimeOffset? From, DateTimeOffset? To, int? Page, int? PageSize);

/// <summary>
/// A checkout: a payment to Pishkhan through Zarinpal, and the invoice once paid (<c>invoiceNumber</c>). Amounts in Rial; <c>refunded</c> is the
/// sum of its refunds. <c>status</c>: pending, paid, failed or expired.
/// </summary>
public sealed record PaymentResponse(
    Guid Id, Guid WorkspaceId, string? WorkspaceName, string Kind, string PlanCode, string PlanName, int PlanVersion, string? Cycle, long Price, long Credit,
    long Amount, long Refunded, string Status, string? ReferenceId, string? CardMask, string? Error, long? InvoiceNumber, Guid? SubscriptionId,
    DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, long Discount = 0, string? CouponCode = null, long WalletUsed = 0, bool WalletReleased = false);

/// <summary>A refund. <c>createdBy</c> is the staff member's username.
/// <c>toWallet</c>: paid into the workspace's wallet; <c>walletReturned</c>: the checkout's wallet part given back with it.</summary>
public sealed record RefundResponse(
    Guid Id, Guid CheckoutId, long Amount, string Reason, string? ReferenceId, bool EndedSubscription, string? CreatedBy, DateTimeOffset CreatedAt, bool ToWallet = false,
    long WalletReturned = 0);

public sealed record PaymentDetail(PaymentResponse Payment, IReadOnlyList<RefundResponse> Refunds, SubscriptionResponse? Subscription);

/// <summary>
/// Records money paid back in Zarinpal's panel. <c>amount</c> (Rial) at most what is left of the payment; <c>referenceId</c> is Zarinpal's
/// refund reference (optional); <c>endSubscription</c> ends the subscription the payment bought, if it is still current or scheduled,
/// and gives back the part paid from the wallet. <c>toWallet</c> pays the refund into the workspace's wallet instead of through Zarinpal.
/// <c>amount</c> may be 0 only to give back the wallet part with <c>endSubscription</c>.
/// </summary>
public sealed record RefundRequest(long Amount, string Reason, string? ReferenceId, bool EndSubscription, bool ToWallet = false);

/// <summary>Payments over the last <c>days</c>: paid total, refunds, net, and how many failed. Rial.</summary>
public sealed record BillingSummary(int Days, int PaidCount, long PaidAmount, int RefundCount, long RefundedAmount, long NetAmount, int FailedCount, int PendingCount);

/// <summary>AdminApi billing across workspaces (admin plan 4.4): subscriptions, payments and invoices, refunds.</summary>
internal static class BillingAdminEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Finance, StaffRoles.Support, StaffRoles.ReadOnly];
    private static readonly string[] Statuses = [CheckoutStatuses.Pending, CheckoutStatuses.Paid, CheckoutStatuses.Failed, CheckoutStatuses.Expired];

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().MapGroup("/billing").WithTags("Billing");
        var read = admin.MapGroup(string.Empty).RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var finance = admin.MapGroup(string.Empty).RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Finance));

        read.MapGet("/subscriptions", SubscriptionsAsync)
            .WithSummary("Subscriptions across workspaces, newest first")
            .WithDescription("Roles: SuperAdmin, Finance, Support, ReadOnly.")
            .Produces<PagedResult<AdminSubscriptionRow>>()
            .WithStandardProblems();

        read.MapGet("/payments", PaymentsAsync)
            .WithSummary("Payments (checkouts) across workspaces, newest first; paid ones are the invoices")
            .WithDescription("Roles: SuperAdmin, Finance, Support, ReadOnly.")
            .Produces<PagedResult<PaymentResponse>>()
            .WithStandardProblems();

        read.MapGet("/payments/{id:guid}", PaymentAsync)
            .WithSummary("A payment with its refunds and the subscription it bought")
            .Produces<PaymentDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        read.MapGet("/summary", SummaryAsync)
            .WithSummary("Paid, refunded and failed payments over the last `days` (default 30, at most 366)")
            .Produces<BillingSummary>();

        finance.MapPost("/payments/{id:guid}/refunds", RefundAsync)
            .RequireStepUp()
            .WithSummary("Record a refund made in Zarinpal's panel")
            .WithDescription("Finance or SuperAdmin, step-up, reason. 409 `billing.refund.not_paid` for an unpaid checkout; 400 `billing.refund.too_much` on `amount` " +
                "past what is left. Audited in the workspace as billing.refund.")
            .Produces<PaymentDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
    }

    private static IQueryable<Subscription> AllSubscriptions(BillingDbContext db) => db.Subscriptions.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static IQueryable<Checkout> AllCheckouts(BillingDbContext db) => db.Checkouts.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static IQueryable<Refund> AllRefunds(BillingDbContext db) => db.Refunds.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static (int Page, int Size) Paging(int? page, int? size) => (Math.Max(1, page ?? 1), Math.Clamp(size ?? DefaultPageSize, 1, MaxPageSize));

    private static async Task<IResult> SubscriptionsAsync([AsParameters] AdminSubscriptionSearch search, BillingDbContext db, IWorkspaceQueries workspaces, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var (page, size) = Paging(search.Page, search.PageSize);
        var query = AllSubscriptions(db);
        var state = string.IsNullOrWhiteSpace(search.State) ? "current" : search.State.Trim().ToLowerInvariant();
        query = state switch
        {
            "current" => query.Where(s => s.Status == SubscriptionStatus.Active && (s.EndsAt == null || s.EndsAt > now)),
            "trialing" => query.Where(s => s.Status == SubscriptionStatus.Active && s.Trial && (s.EndsAt == null || s.EndsAt > now)),
            "scheduled" => query.Where(s => s.Status == SubscriptionStatus.Scheduled && s.StartsAt > now),
            "ended" => query.Where(s => s.Status == SubscriptionStatus.Ended || (s.EndsAt != null && s.EndsAt <= now)),
            "all" => query,
            _ => null!,
        };
        if (query is null)
        {
            return PlanEndpoints.Invalid("state", "validation.invalid");
        }

        if (!string.IsNullOrWhiteSpace(search.PlanCode))
        {
            query = query.Where(s => s.PlanCode == search.PlanCode.Trim());
        }

        if (search.WorkspaceId is { } ws)
        {
            query = query.Where(s => s.WorkspaceId == ws);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.WorkspaceId), workspaces, ct);
        return Results.Ok(new PagedResult<AdminSubscriptionRow>(
            [.. rows.Select(s => new AdminSubscriptionRow(SubscriptionEndpoints.ToResponse(s, now), s.WorkspaceId, names.GetValueOrDefault(s.WorkspaceId)))], total, page, size));
    }

    private static async Task<IResult> PaymentsAsync([AsParameters] PaymentSearch search, BillingDbContext db, IWorkspaceQueries workspaces, IClock clock, CancellationToken ct)
    {
        var (page, size) = Paging(search.Page, search.PageSize);
        var now = clock.UtcNow;
        var query = AllCheckouts(db);
        var status = string.IsNullOrWhiteSpace(search.Status) ? CheckoutStatuses.Paid : search.Status.Trim().ToLowerInvariant();
        if (status != "all" && !Statuses.Contains(status))
        {
            return PlanEndpoints.Invalid("status", "validation.invalid");
        }

        // Expired is a pending checkout past its lifetime (see Checkout.StatusAt).
        var expiredBefore = now - Checkout.Lifetime;
        query = status switch
        {
            CheckoutStatuses.Pending => query.Where(c => c.Status == CheckoutStatuses.Pending && c.CreatedAt >= expiredBefore),
            CheckoutStatuses.Expired => query.Where(c => c.Status == CheckoutStatuses.Pending && c.CreatedAt < expiredBefore),
            "all" => query,
            _ => query.Where(c => c.Status == status),
        };
        if (!string.IsNullOrWhiteSpace(search.Kind))
        {
            query = query.Where(c => c.Kind == search.Kind.Trim());
        }

        if (search.WorkspaceId is { } ws)
        {
            query = query.Where(c => c.WorkspaceId == ws);
        }

        if (search.From is { } from)
        {
            query = query.Where(c => c.CreatedAt >= from);
        }

        if (search.To is { } to)
        {
            query = query.Where(c => c.CreatedAt < to);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var refunded = await AllRefunds(db).Where(r => ids.Contains(r.CheckoutId)).GroupBy(r => r.CheckoutId).Select(g => new { g.Key, Sum = g.Sum(r => r.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var names = await NamesAsync(rows.Select(r => r.WorkspaceId), workspaces, ct);
        return Results.Ok(new PagedResult<PaymentResponse>(
            [.. rows.Select(c => ToPayment(c, refunded.GetValueOrDefault(c.Id), names.GetValueOrDefault(c.WorkspaceId), now))], total, page, size));
    }

    private static async Task<IResult> PaymentAsync(Guid id, BillingDbContext db, IWorkspaceQueries workspaces, IStaffDirectory staff, IClock clock, CancellationToken ct)
    {
        var checkout = await AllCheckouts(db).FirstOrDefaultAsync(c => c.Id == id, ct);
        return checkout is null ? Error.NotFound().ToProblem() : Results.Ok(await DetailAsync(db, checkout, workspaces, staff, clock.UtcNow, ct));
    }

    private static async Task<IResult> SummaryAsync(int? days, BillingDbContext db, IClock clock, CancellationToken ct)
    {
        var span = Math.Clamp(days ?? 30, 1, 366);
        var now = clock.UtcNow;
        var from = now.AddDays(-span);
        var paid = await AllCheckouts(db).Where(c => c.Status == CheckoutStatuses.Paid && c.PaidAt >= from).GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(c => c.Amount) }).FirstOrDefaultAsync(ct);
        var refunds = await AllRefunds(db).Where(r => r.CreatedAt >= from).GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(r => r.Amount) }).FirstOrDefaultAsync(ct);
        var failed = await AllCheckouts(db).CountAsync(c => c.Status == CheckoutStatuses.Failed && c.CreatedAt >= from, ct);
        var pending = await AllCheckouts(db).CountAsync(c => c.Status == CheckoutStatuses.Pending && c.CreatedAt >= now - Checkout.Lifetime, ct);
        var paidSum = paid?.Sum ?? 0;
        var refundSum = refunds?.Sum ?? 0;
        return Results.Ok(new BillingSummary(span, paid?.Count ?? 0, paidSum, refunds?.Count ?? 0, refundSum, paidSum - refundSum, failed, pending));
    }

    private static async Task<IResult> RefundAsync(
        Guid id, RefundRequest request, BillingDbContext db, WalletLedger wallets, IWorkspaceQueries workspaces, IStaffDirectory staff, ICurrentUser caller,
        ICacheInvalidator cache, IClock clock, CancellationToken ct)
    {
        if (PlanEndpoints.Reason(request.Reason, required: true) is { } invalid)
        {
            return invalid;
        }

        if (request.ReferenceId is { Length: > 64 })
        {
            return PlanEndpoints.Invalid("referenceId", "validation.too_long");
        }

        var checkout = await AllCheckouts(db).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (checkout is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (checkout.Status != CheckoutStatuses.Paid)
        {
            return Error.Conflict("billing.refund.not_paid").ToProblem();
        }

        db.Tenant.SetWorkspace(checkout.WorkspaceId);
        var now = clock.UtcNow;
        var already = await db.Refunds.Where(r => r.CheckoutId == id).SumAsync(r => (long?)r.Amount, ct) ?? 0;
        var returnsWallet = request.EndSubscription && checkout.WalletUsed > 0 && !checkout.WalletReleased;
        if (request.Amount < 0 || (request.Amount == 0 && !returnsWallet) || request.Amount > checkout.Amount - already)
        {
            return PlanEndpoints.Invalid("amount", "billing.refund.too_much");
        }

        if (request.ToWallet && checkout.Kind == CheckoutKinds.WalletTopUp)
        {
            return PlanEndpoints.Invalid("toWallet", "billing.refund.top_up_to_wallet");
        }

        // Refunding a top-up takes the money back out of the wallet, so it must still be there.
        Wallet? wallet = null;
        if (request.ToWallet || returnsWallet || (checkout.Kind == CheckoutKinds.WalletTopUp && request.Amount > 0))
        {
            wallet = await wallets.SettledAsync(checkout.WorkspaceId, ct);
        }

        if (checkout.Kind == CheckoutKinds.WalletTopUp && request.Amount > 0)
        {
            if (wallet!.Balance < request.Amount)
            {
                return Error.Conflict("billing.refund.wallet_spent").ToProblem();
            }

            wallets.Change(wallet, -request.Amount, WalletEntryKinds.Refund, request.Reason.Trim(), checkout.Id, WalletActors.Staff, caller.UserId);
        }

        Subscription? ended = null;
        if (request.EndSubscription && checkout.SubscriptionId is { } subscriptionId)
        {
            ended = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId && s.Status != SubscriptionStatus.Ended, ct);
            ended?.End(Subscription.EndRefunded, now);
        }

        var walletReturned = returnsWallet ? wallets.GiveBack(wallet!, checkout, "refund", WalletActors.Staff, caller.UserId) : 0;
        if (request.ToWallet && request.Amount > 0)
        {
            wallets.Change(wallet!, request.Amount, WalletEntryKinds.Refund, request.Reason.Trim(), checkout.Id, WalletActors.Staff, caller.UserId);
        }

        var refund = Refund.Create(
            checkout, already, request.Amount, request.Reason, request.ReferenceId, ended is not null, caller.UserId, now, request.ToWallet, walletReturned);
        db.Refunds.Add(refund);
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), "billing.refund", "checkout", checkout.Id.ToString(),
            checkout.WorkspaceId, now,
            after: new { refund.Amount, refund.ReferenceId, refund.ToWallet, refund.WalletReturned, invoice = checkout.InvoiceNumber, paid = checkout.Amount, refundedTotal = already + refund.Amount, endedSubscription = ended?.Id },
            reason: refund.Reason);
        db.Publish(entry, entry.PartitionKey(), checkout.WorkspaceId);
        if (ended is not null)
        {
            cache.Enqueue(db, BillingCacheTags.Workspace(checkout.WorkspaceId));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("billing.wallet.busy").ToProblem();
        }

        if (ended is not null)
        {
            await cache.InvalidateLocalAsync([BillingCacheTags.Workspace(checkout.WorkspaceId)], ct);
        }

        return Results.Ok(await DetailAsync(db, checkout, workspaces, staff, now, ct));
    }

    private static async Task<PaymentDetail> DetailAsync(
        BillingDbContext db, Checkout checkout, IWorkspaceQueries workspaces, IStaffDirectory staff, DateTimeOffset now, CancellationToken ct)
    {
        var refunds = await AllRefunds(db).Where(r => r.CheckoutId == checkout.Id).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var who = new Dictionary<Guid, string?>();
        foreach (var by in refunds.Select(r => r.CreatedBy).Distinct())
        {
            who[by] = await staff.GetUsernameAsync(by, ct);
        }

        var subscription = checkout.SubscriptionId is { } sid ? await AllSubscriptions(db).FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
        return new PaymentDetail(
            ToPayment(checkout, refunds.Sum(r => r.Amount), await workspaces.GetNameAsync(checkout.WorkspaceId, ct), now),
            [.. refunds.Select(r => new RefundResponse(r.Id, r.CheckoutId, r.Amount, r.Reason, r.ReferenceId, r.EndedSubscription, who.GetValueOrDefault(r.CreatedBy), r.CreatedAt, r.ToWallet, r.WalletReturned))],
            subscription is null ? null : SubscriptionEndpoints.ToResponse(subscription, now));
    }

    private static PaymentResponse ToPayment(Checkout c, long refunded, string? workspaceName, DateTimeOffset now) => new(
        c.Id, c.WorkspaceId, workspaceName, c.Kind, c.PlanCode, c.PlanName, c.PlanVersion, c.Kind == CheckoutKinds.Overage ? null : c.Cycle, c.Price, c.Credit, c.Amount,
        refunded, c.StatusAt(now), c.ReferenceId, c.CardMask, c.Error, c.InvoiceNumber, c.SubscriptionId, c.CreatedAt, c.PaidAt, c.Discount, c.CouponCode, c.WalletUsed,
        c.WalletReleased);

    private static async Task<Dictionary<Guid, string?>> NamesAsync(IEnumerable<Guid> ids, IWorkspaceQueries workspaces, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string?>();
        foreach (var id in ids.Distinct())
        {
            names[id] = await workspaces.GetNameAsync(id, ct);
        }

        return names;
    }
}
