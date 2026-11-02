using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Domain;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.Modules.Catalog.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Catalog.Features;

/// <summary>
/// Which products: any of <c>productIds</c> (up to 1,000), <c>categoryId</c> (with its subcategories), <c>tag</c> and <c>status</c>, all
/// of which must match; or <c>all: true</c> for every product. Products priced by formula are never included.
/// </summary>
public sealed record PriceChangeScopeRequest(IReadOnlyList<Guid>? ProductIds, Guid? CategoryId, string? Tag, string? Status, bool All = false);

/// <summary>
/// <c>kind</c> <c>percent</c> uses <c>percent</c> (−90 to 500, for example 10 or −15); <c>amount</c> uses <c>amount</c> (Rial, negative to
/// lower prices). New prices are rounded to <c>roundTo</c> Toman (1, 100, 500, 1000, 5000, 10000, 50000 or 100000; default 1) by
/// <c>roundMode</c> (<c>nearest</c>, <c>up</c> or <c>down</c>). On apply, <c>fingerprint</c> from the preview makes sure the catalog has
/// not changed since; without it the change is worked out afresh.
/// </summary>
public sealed record PriceChangeRequest(PriceChangeScopeRequest? Scope, string? Kind, decimal? Percent, Money? Amount, long? RoundTo, string? RoundMode, string? Fingerprint);

/// <summary>One price: the product's own (<c>variantId</c> null) or a variant's own price.</summary>
public sealed record PriceChangeLine(Guid ProductId, string Title, Guid? VariantId, string? VariantLabel, Money OldPrice, Money? NewPrice);

/// <summary>
/// <c>items</c> are the prices that change; <c>invalid</c> are prices the change would take to zero or below (apply is refused while there
/// are any). Pass <c>fingerprint</c> to apply exactly this.
/// </summary>
public sealed record PriceChangePreviewResponse(string Fingerprint, int ProductCount, int PriceCount, IReadOnlyList<PriceChangeLine> Items, IReadOnlyList<PriceChangeLine> Invalid);

/// <summary><c>items</c> is only filled when one batch is read. <c>undoSkipped</c> counts prices undo left alone because they had changed again.</summary>
public sealed record PriceChangeBatchResponse(
    Guid Id, PriceChangeScopeRequest Scope, string Kind, decimal? Percent, Money? Amount, long RoundTo, string RoundMode, int ProductCount, int PriceCount,
    Guid? CreatedBy, DateTimeOffset CreatedAt, bool CanUndo, DateTimeOffset UndoDeadline, DateTimeOffset? UndoneAt, Guid? UndoneBy, int UndoSkipped,
    IReadOnlyList<PriceChangeLine>? Items);

internal static class PriceChangeEndpoints
{
    private const int MaxProductIds = 1_000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var root = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/catalog/price-changes").WithTags("Catalog");

        root.MapPost("/preview", PreviewAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Preview a bulk price change")
            .WithDescription("Nothing is saved. 422 `catalog.price_change_too_large` when more than 5,000 products match.")
            .Produces<PriceChangePreviewResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        root.MapPost(string.Empty, ApplyAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithIdempotency()
            .WithSummary("Apply a bulk price change as one batch")
            .WithDescription("The result is exactly the preview for the same request. 409 `catalog.price_change_stale` when the fingerprint no longer matches; 422 `catalog.price_change_empty` when no price changes, `catalog.price_change_below_zero` when a price would reach zero, `catalog.price_change_too_large`. Cards, the AI's knowledge and caches are refreshed at once.")
            .Produces<PriceChangeBatchResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        root.MapGet(string.Empty, ListAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .WithSummary("Recent bulk price changes, newest first (50)")
            .Produces<IReadOnlyList<PriceChangeBatchResponse>>();

        root.MapGet("/{batchId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .WithSummary("A bulk price change with every price it changed")
            .Produces<PriceChangeBatchResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPost("/{batchId:guid}/undo", UndoAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Undo a bulk price change within 24 hours")
            .WithDescription("Puts back every price that still has the batch's new value; prices changed since are left alone (`undoSkipped`). 409 `catalog.price_change_undone` or `catalog.price_change_undo_expired`.")
            .Produces<PriceChangeBatchResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> PreviewAsync(Guid workspaceId, PriceChangeRequest request, CatalogDbContext db, PriceChanges changes, CancellationToken ct)
    {
        var (parsed, invalid) = await ParseAsync(request, db, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        var (scope, rule) = parsed!.Value;
        var plan = await changes.PlanAsync(scope, rule, ct);
        if (plan.Products.Count > PriceChangeBatch.MaxItems)
        {
            return Error.Unprocessable("catalog.price_change_too_large").ToProblem();
        }

        var products = plan.Products.ToDictionary(p => p.Id);
        return Results.Ok(new PriceChangePreviewResponse(
            plan.Fingerprint, plan.Items.Select(i => i.ProductId).Distinct().Count(), plan.Items.Count, [.. plan.Items.Select(i => Line(i, products))],
            [.. plan.Invalid.Select(i => Line(i, products) with { NewPrice = null })]));
    }

    private static async Task<IResult> ApplyAsync(
        Guid workspaceId, PriceChangeRequest request, CatalogDbContext db, PriceChanges changes, HybridCache cache, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var (parsed, invalid) = await ParseAsync(request, db, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        var (scope, rule) = parsed!.Value;
        var plan = await changes.PlanAsync(scope, rule, ct);
        if (plan.Products.Count > PriceChangeBatch.MaxItems)
        {
            return Error.Unprocessable("catalog.price_change_too_large").ToProblem();
        }

        if (request.Fingerprint is { Length: > 0 } fingerprint && fingerprint != plan.Fingerprint)
        {
            return Error.Conflict("catalog.price_change_stale").ToProblem();
        }

        if (plan.Invalid.Count > 0)
        {
            return Error.Unprocessable("catalog.price_change_below_zero").ToProblem();
        }

        if (plan.Items.Count == 0)
        {
            return Error.Unprocessable("catalog.price_change_empty").ToProblem();
        }

        var now = clock.UtcNow;
        var batch = changes.Apply(workspaceId, scope, rule, plan, caller.UserId, now);
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), "catalog.prices_changed", "price_change", batch.Id.ToString(), workspaceId, now,
            after: new { rule.Kind, rule.Value, rule.RoundToToman, rule.RoundMode, batch.ProductCount, prices = batch.Items.Count });
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        var products = plan.Products.ToDictionary(p => p.Id);
        return Results.Json(ToResponse(batch, now, products), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAsync(Guid workspaceId, CatalogDbContext db, IClock clock, CancellationToken ct)
    {
        var batches = await db.PriceChanges.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(50).ToListAsync(ct);
        var now = clock.UtcNow;
        return Results.Ok(batches.Select(b => ToResponse(b, now, null)).ToList());
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid batchId, CatalogDbContext db, IClock clock, CancellationToken ct)
    {
        var batch = await db.PriceChanges.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null)
        {
            return Error.NotFound().ToProblem();
        }

        return Results.Ok(ToResponse(batch, clock.UtcNow, await ProductsAsync(db, batch, ct)));
    }

    private static async Task<IResult> UndoAsync(
        Guid workspaceId, Guid batchId, CatalogDbContext db, PriceChanges changes, HybridCache cache, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var batch = await db.PriceChanges.FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        if (batch.UndoneAt is not null)
        {
            return Error.Conflict("catalog.price_change_undone").ToProblem();
        }

        if (!batch.CanUndo(now))
        {
            return Error.Conflict("catalog.price_change_undo_expired").ToProblem();
        }

        var skipped = await changes.UndoAsync(batch, caller.UserId, now, ct);
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), "catalog.prices_change_undone", "price_change", batch.Id.ToString(), workspaceId, now,
            after: new { restored = batch.Items.Count - skipped, skipped });
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        return Results.Ok(ToResponse(batch, now, await ProductsAsync(db, batch, ct)));
    }

    /// <summary>Validates the request (the category must be the workspace's), giving the scope and rule or the problem to return.</summary>
    private static async Task<((PriceChangeScope Scope, PriceRule Rule)? Parsed, IResult? Invalid)> ParseAsync(PriceChangeRequest request, CatalogDbContext db, CancellationToken ct)
    {
        var errors = new List<(string Field, string Code)>();
        var s = request.Scope;
        var tag = string.IsNullOrWhiteSpace(s?.Tag) ? null : s.Tag.Trim();
        var status = string.IsNullOrWhiteSpace(s?.Status) ? null : s.Status.Trim();
        var ids = s?.ProductIds is { Count: > 0 } list ? list.Distinct().ToList() : null;
        if (s is null || (ids is null && s.CategoryId is null && tag is null && status is null && !s.All))
        {
            errors.Add(("scope", "catalog.price_change_scope_required"));
        }

        if (ids is { Count: > MaxProductIds })
        {
            errors.Add(("scope.productIds", "catalog.price_change_too_large"));
        }

        if (status is not null && !ProductStatuses.All.Contains(status))
        {
            errors.Add(("scope.status", "catalog.status_invalid"));
        }

        if (s?.CategoryId is { } category && !await db.Categories.AnyAsync(c => c.Id == category, ct))
        {
            errors.Add(("scope.categoryId", "catalog.category_not_found"));
        }

        decimal value = 0;
        switch (request.Kind)
        {
            case PriceChangeKinds.Percent:
                if (request.Percent is not { } percent || percent == 0 || percent < PriceRule.MinPercent || percent > PriceRule.MaxPercent || decimal.Round(percent, 2) != percent)
                {
                    errors.Add(("percent", "catalog.price_change_percent_invalid"));
                }
                else
                {
                    value = percent;
                }

                break;
            case PriceChangeKinds.Amount:
                if (request.Amount is not { } amount || amount.Amount == 0 || amount.Currency != Currency.Irr)
                {
                    errors.Add(("amount", "catalog.price_change_amount_invalid"));
                }
                else
                {
                    value = amount.Amount;
                }

                break;
            default:
                errors.Add(("kind", "catalog.price_change_kind_invalid"));
                break;
        }

        var roundTo = request.RoundTo ?? 1;
        if (!PriceRule.RoundingSteps.Contains(roundTo))
        {
            errors.Add(("roundTo", "catalog.price_change_rounding_invalid"));
        }

        var roundMode = request.RoundMode ?? RoundingModes.Nearest;
        if (!RoundingModes.All.Contains(roundMode))
        {
            errors.Add(("roundMode", "catalog.price_change_rounding_invalid"));
        }

        if (errors.Count > 0)
        {
            return (null, Error.Validation(errors[0].Code, errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).Distinct().ToArray(), StringComparer.Ordinal)).ToProblem());
        }

        return ((new PriceChangeScope(ids, s!.CategoryId, tag, status, s.All), new PriceRule(request.Kind!, value, roundTo, roundMode)), null);
    }

    private static async Task<Dictionary<Guid, Product>> ProductsAsync(CatalogDbContext db, PriceChangeBatch batch, CancellationToken ct)
    {
        var ids = batch.Items.Select(i => i.ProductId).Distinct().ToList();
        return await db.Products.IgnoreQueryFilters([ModuleDbContext.SoftDeleteFilter]).AsNoTracking().Where(p => ids.Contains(p.Id) && p.WorkspaceId == batch.WorkspaceId).ToDictionaryAsync(p => p.Id, ct);
    }

    private static PriceChangeLine Line(PriceChangeItem item, IReadOnlyDictionary<Guid, Product> products)
    {
        var product = products.GetValueOrDefault(item.ProductId);
        var variant = item.VariantId is { } id ? product?.Variants.FirstOrDefault(v => v.Id == id) : null;
        return new PriceChangeLine(item.ProductId, product?.Title ?? string.Empty, item.VariantId, variant?.Label, Money.Rial(item.OldPrice), Money.Rial(item.NewPrice));
    }

    private static PriceChangeBatchResponse ToResponse(PriceChangeBatch b, DateTimeOffset now, IReadOnlyDictionary<Guid, Product>? products) => new(
        b.Id, new PriceChangeScopeRequest(b.Scope.ProductIds, b.Scope.CategoryId, b.Scope.Tag, b.Scope.Status, b.Scope.All), b.Rule.Kind,
        b.Rule.Kind == PriceChangeKinds.Percent ? b.Rule.Value : null, b.Rule.Kind == PriceChangeKinds.Amount ? Money.Rial((long)b.Rule.Value) : null,
        b.Rule.RoundToToman, b.Rule.RoundMode, b.ProductCount, b.Items.Count, b.CreatedBy, b.CreatedAt, b.CanUndo(now), b.CreatedAt + PriceChangeBatch.UndoWindow,
        b.UndoneAt, b.UndoneBy, b.UndoSkipped, products is null ? null : [.. b.Items.Select(i => Line(i, products))]);

    private static async Task<IResult?> SaveAsync(CatalogDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("catalog.price_change_stale").ToProblem();
        }
    }
}
