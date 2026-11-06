using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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

/// <summary>
/// A coupon. <c>kind</c> <c>percent</c> (<c>value</c> 1 to 100) or <c>fixed</c> (<c>value</c> Rial). <c>planCodes</c> empty means every plan.
/// <c>state</c>: active, scheduled (before <c>validFrom</c>), expired (past <c>validUntil</c>), used_up or disabled.
/// </summary>
public sealed record CouponResponse(
    Guid Id, string Code, string? Description, string Kind, long Value, IReadOnlyList<string> PlanCodes, bool FirstPurchaseOnly, int? MaxRedemptions,
    int MaxPerWorkspace, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, bool Active, string State, int Redemptions, long DiscountTotal,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Creates a coupon. <c>code</c>: letters, digits and dashes, 3 to 32, stored upper case. <c>maxPerWorkspace</c> defaults to 1.</summary>
public sealed record CreateCouponRequest(
    string Code, string Kind, long Value, string? Description, IReadOnlyList<string>? PlanCodes, bool FirstPurchaseOnly, int? MaxRedemptions,
    int? MaxPerWorkspace, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, bool? Active);

/// <summary>Replaces everything but the code, kind and value, which paid invoices already carry.</summary>
public sealed record UpdateCouponRequest(
    string? Description, IReadOnlyList<string>? PlanCodes, bool FirstPurchaseOnly, int? MaxRedemptions, int? MaxPerWorkspace, DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil, bool Active);

public sealed record CouponRedemptionResponse(Guid CheckoutId, Guid WorkspaceId, string? WorkspaceName, long Discount, long? InvoiceNumber, DateTimeOffset CreatedAt);

public sealed record CouponDetail(CouponResponse Coupon, IReadOnlyList<CouponRedemptionResponse> Redemptions);

/// <summary><c>amount</c> signed Rial: positive adds credit, negative takes it off (never below zero). Reason required.</summary>
public sealed record WalletAdjustmentRequest(long Amount, string Reason);

/// <summary>Coupons (admin plan 4.4) and workspace wallets in the AdminApi. Readers: SuperAdmin, Finance, Support, ReadOnly; changes: Finance.</summary>
internal static class CouponAdminEndpoints
{
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Finance, StaffRoles.Support, StaffRoles.ReadOnly];

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapAdminApi();
        var read = api.MapGroup(string.Empty).WithTags("Billing")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var finance = api.MapGroup(string.Empty).WithTags("Billing").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Finance));

        read.MapGet("/billing/coupons", ListAsync)
            .WithSummary("Coupons, newest first")
            .WithDescription("`q` matches the code or description; `state` active, scheduled, expired, used_up or disabled.")
            .Produces<PagedResult<CouponResponse>>();

        read.MapGet("/billing/coupons/{id:guid}", GetAsync)
            .WithSummary("A coupon and its redemptions (newest 100)")
            .Produces<CouponDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        finance.MapPost("/billing/coupons", CreateAsync)
            .WithSummary("Create a coupon")
            .WithDescription("Finance or SuperAdmin. 409 `billing.coupon.code_taken`. Audited as billing.coupon.create.")
            .Produces<CouponResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems()
            .WithExamples(new CreateCouponRequest("NOWRUZ1406", CouponKinds.Percent, 20, "تخفیف نوروز", [], true, 500, 1, null, null, true));

        finance.MapPut("/billing/coupons/{id:guid}", UpdateAsync)
            .WithSummary("Change a coupon's limits, dates or description, or turn it off")
            .WithDescription("Finance or SuperAdmin. Audited as billing.coupon.update.")
            .Produces<CouponResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        read.MapGet("/workspaces/{workspaceId:guid}/wallet", WalletAsync)
            .WithSummary("A workspace's wallet balance and its latest changes")
            .Produces<WalletResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        finance.MapPost("/workspaces/{workspaceId:guid}/wallet/adjustments", AdjustAsync)
            .RequireStepUp()
            .WithSummary("Add credit to a workspace's wallet or take it off")
            .WithDescription("Finance or SuperAdmin, step-up, reason. 409 `billing.wallet.insufficient` when it would go below zero. Audited in the " +
                "workspace as billing.wallet.adjust.")
            .Produces<WalletResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
    }

    internal static string StateOf(Coupon c, DateTimeOffset now) =>
        !c.Active ? "disabled"
        : c.ValidFrom is { } from && now < from ? "scheduled"
        : c.ValidUntil is { } until && now >= until ? "expired"
        : c.MaxRedemptions is { } max && c.Redemptions >= max ? "used_up"
        : "active";

    private static CouponResponse ToResponse(Coupon c, long discountTotal, DateTimeOffset now) => new(
        c.Id, c.Code, c.Description, c.Kind, c.Value, c.PlanCodes, c.FirstPurchaseOnly, c.MaxRedemptions, c.MaxPerWorkspace, c.ValidFrom, c.ValidUntil,
        c.Active, StateOf(c, now), c.Redemptions, discountTotal, c.CreatedAt, c.UpdatedAt);

    private static IQueryable<CouponRedemption> AllRedemptions(BillingDbContext db) =>
        db.CouponRedemptions.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static async Task<IResult> ListAsync(string? q, string? state, int? page, int? pageSize, BillingDbContext db, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var (p, size) = (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, 100));
        var query = db.Coupons.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var code = term.ToUpperInvariant();
            query = query.Where(c => c.Code.Contains(code) || (c.Description != null && EF.Functions.ILike(c.Description, "%" + term + "%")));
        }

        query = state switch
        {
            null or "" => query,
            "disabled" => query.Where(c => !c.Active),
            "scheduled" => query.Where(c => c.Active && c.ValidFrom > now),
            "expired" => query.Where(c => c.Active && c.ValidUntil <= now),
            "used_up" => query.Where(c => c.Active && c.MaxRedemptions != null && c.Redemptions >= c.MaxRedemptions),
            "active" => query.Where(c => c.Active && (c.ValidFrom == null || c.ValidFrom <= now) && (c.ValidUntil == null || c.ValidUntil > now)
                && (c.MaxRedemptions == null || c.Redemptions < c.MaxRedemptions)),
            _ => null!,
        };
        if (query is null)
        {
            return PlanEndpoints.Invalid("state", "validation.invalid");
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(c => c.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var sums = await AllRedemptions(db).Where(r => ids.Contains(r.CouponId)).GroupBy(r => r.CouponId).Select(g => new { g.Key, Sum = g.Sum(r => r.Discount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return Results.Ok(new PagedResult<CouponResponse>([.. rows.Select(c => ToResponse(c, sums.GetValueOrDefault(c.Id), now))], total, p, size));
    }

    private static async Task<IResult> GetAsync(Guid id, BillingDbContext db, IWorkspaceQueries workspaces, IClock clock, CancellationToken ct)
    {
        var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return coupon is null ? Error.NotFound().ToProblem() : Results.Ok(await DetailAsync(db, coupon, workspaces, clock.UtcNow, ct));
    }

    private static async Task<CouponDetail> DetailAsync(BillingDbContext db, Coupon coupon, IWorkspaceQueries workspaces, DateTimeOffset now, CancellationToken ct)
    {
        var redemptions = await AllRedemptions(db).Where(r => r.CouponId == coupon.Id).OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        var checkoutIds = redemptions.Select(r => r.CheckoutId).ToList();
        var invoices = await db.Checkouts.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).Where(c => checkoutIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.InvoiceNumber, ct);
        var names = new Dictionary<Guid, string?>();
        foreach (var ws in redemptions.Select(r => r.WorkspaceId).Distinct())
        {
            names[ws] = await workspaces.GetNameAsync(ws, ct);
        }

        var total = await AllRedemptions(db).Where(r => r.CouponId == coupon.Id).SumAsync(r => (long?)r.Discount, ct) ?? 0;
        return new CouponDetail(
            ToResponse(coupon, total, now),
            [.. redemptions.Select(r => new CouponRedemptionResponse(r.CheckoutId, r.WorkspaceId, names.GetValueOrDefault(r.WorkspaceId), r.Discount, invoices.GetValueOrDefault(r.CheckoutId), r.CreatedAt))]);
    }

    private static async Task<IResult> CreateAsync(CreateCouponRequest request, BillingDbContext db, PlanAudit audit, IClock clock, CancellationToken ct)
    {
        var terms = new CouponTerms(
            request.Kind?.Trim() ?? string.Empty, request.Value, request.Description, request.PlanCodes ?? [], request.FirstPurchaseOnly, request.MaxRedemptions,
            request.MaxPerWorkspace ?? 1, request.ValidFrom, request.ValidUntil, request.Active ?? true);
        var errors = Coupon.Validate(request.Code, terms, creating: true);
        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var code = Coupon.Normalize(request.Code);
        if (await db.Coupons.AnyAsync(c => c.Code == code, ct))
        {
            return Error.Conflict("billing.coupon.code_taken").ToProblem();
        }

        var now = clock.UtcNow;
        var coupon = Coupon.Create(code, terms, audit.StaffId, now);
        db.Coupons.Add(coupon);
        await audit.RecordCouponAsync(db, coupon, "billing.coupon.create", ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Error.Conflict("billing.coupon.code_taken").ToProblem();
        }

        return Results.Json(ToResponse(coupon, 0, now), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateCouponRequest request, BillingDbContext db, PlanAudit audit, IWorkspaceQueries workspaces, IClock clock, CancellationToken ct)
    {
        var coupon = await db.Coupons.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (coupon is null)
        {
            return Error.NotFound().ToProblem();
        }

        var terms = new CouponTerms(
            coupon.Kind, coupon.Value, request.Description, request.PlanCodes ?? [], request.FirstPurchaseOnly, request.MaxRedemptions, request.MaxPerWorkspace ?? 1,
            request.ValidFrom, request.ValidUntil, request.Active);
        var errors = Coupon.Validate(null, terms, creating: false);
        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var now = clock.UtcNow;
        coupon.Apply(terms, now);
        await audit.RecordCouponAsync(db, coupon, "billing.coupon.update", ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("billing.coupon.stale").ToProblem();
        }

        return Results.Ok((await DetailAsync(db, coupon, workspaces, now, ct)).Coupon);
    }

    private static async Task<IResult> WalletAsync(Guid workspaceId, BillingDbContext db, IWorkspaceQueries workspaces, CancellationToken ct)
    {
        if (await workspaces.GetNameAsync(workspaceId, ct) is null)
        {
            return Error.NotFound().ToProblem();
        }

        db.Tenant.SetWorkspace(workspaceId);
        var balance = await db.Wallets.AsNoTracking().Where(w => w.WorkspaceId == workspaceId).Select(w => (long?)w.Balance).FirstOrDefaultAsync(ct) ?? 0;
        return Results.Ok(await WalletEndpoints.ReadAsync(db, workspaceId, balance, ct));
    }

    private static async Task<IResult> AdjustAsync(
        Guid workspaceId, WalletAdjustmentRequest request, BillingDbContext db, WalletLedger wallets, IWorkspaceQueries workspaces, IStaffDirectory staff,
        ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (PlanEndpoints.Reason(request.Reason, required: true) is { } invalid)
        {
            return invalid;
        }

        if (request.Amount == 0 || Math.Abs(request.Amount) > CheckoutService.MaxTopUp)
        {
            return PlanEndpoints.Invalid("amount", "validation.out_of_range");
        }

        if (await workspaces.GetNameAsync(workspaceId, ct) is null)
        {
            return Error.NotFound().ToProblem();
        }

        db.Tenant.SetWorkspace(workspaceId);
        var wallet = await wallets.SettledAsync(workspaceId, ct);
        var before = wallet.Balance;
        if (before + request.Amount < 0)
        {
            return Error.Conflict("billing.wallet.insufficient").ToProblem();
        }

        var reason = request.Reason.Trim();
        wallets.Change(wallet, request.Amount, WalletEntryKinds.Adjustment, reason, null, WalletActors.Staff, caller.UserId);
        var now = clock.UtcNow;
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), "billing.wallet.adjust", "wallet", workspaceId.ToString(),
            workspaceId, now, before: new { balance = before }, after: new { balance = wallet.Balance, amount = request.Amount }, reason: reason);
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The wallet changed at the same time (a checkout spent it): nothing was saved.
            return Error.Conflict("billing.wallet.busy").ToProblem();
        }

        return Results.Ok(await WalletEndpoints.ReadAsync(db, workspaceId, wallet.Balance, ct));
    }
}
