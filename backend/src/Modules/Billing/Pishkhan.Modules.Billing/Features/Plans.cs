using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Billing.Features;

/// <summary>
/// The editable content of a plan version. Prices are Money in Rial (<c>{ amount, currency: "IRR" }</c>). <c>limits</c> maps a meter
/// (ai_conversations, ai_messages, channel_accounts, seats, receipt_verifications, sms) to its limit per month; null or missing is
/// unlimited. <c>features</c> are the pricing card lines.
/// </summary>
public sealed record PlanRequest(
    string Name, string? Tagline, string? Description, Money Monthly, Money? Annual, Money? OveragePerConversation,
    Dictionary<string, int?>? Limits, List<PlanFeature>? Features, bool IsPublic, bool IsFree, bool Highlighted, int SortOrder);

/// <summary><c>code</c>: 2 to 30 lowercase letters, digits or dashes, starting with a letter; the plan's id across versions.</summary>
public sealed record CreatePlanRequest(string Code, PlanRequest Plan);

/// <summary>Why the plan is published or archived, kept in the audit log (optional for publish, 5 to 300 characters when given).</summary>
public sealed record PlanActionRequest(string? Reason);

/// <summary>One version of a plan. <c>status</c>: draft, published, archived.</summary>
public sealed record PlanResponse(
    Guid Id, string Code, int Version, string Status, string Name, string? Tagline, string? Description, Money Monthly, Money? Annual,
    Money? OveragePerConversation, IReadOnlyDictionary<string, int?> Limits, IReadOnlyList<PlanFeature> Features, bool IsPublic, bool IsFree,
    bool Highlighted, int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

public sealed record PlanSearch(string? Code, string? Status, int? Page, int? PageSize);

/// <summary>
/// A plan as the website's pricing page shows it (website contract <c>PublicPlan</c>). <c>aiConversations</c>, <c>channelAccounts</c>
/// and <c>seats</c> are null when unlimited.
/// </summary>
public sealed record PublicPlan(
    string Code, string Name, string? Tagline, string? Description, Money Monthly, Money? Annual, int? AiConversations, Money? OveragePerConversation,
    int? ChannelAccounts, int? Seats, bool Highlighted, bool IsFree, IReadOnlyDictionary<string, int?> Limits, IReadOnlyList<PlanFeature> Features);

/// <summary>Plans and pricing (backend plan 9.13, admin plan 4.4): the back-office editor and the public price list.</summary>
internal static partial class PlanEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Finance, StaffRoles.Support, StaffRoles.ReadOnly];
    private static readonly HybridCacheEntryOptions PublicEntry = new() { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(1) };

    [GeneratedRegex("^[a-z][a-z0-9-]{1,29}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static void MapAdmin(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Plans");
        var read = admin.MapGroup("/plans").RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var write = admin.MapGroup("/plans").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Finance));

        read.MapGet("/", ListAsync)
            .WithSummary("Plan versions, in display order, newest version first")
            .WithDescription("Roles: SuperAdmin, Finance, Support, ReadOnly. Filter by `code` or `status` (draft, published, archived).")
            .Produces<PagedResult<PlanResponse>>()
            .WithStandardProblems();

        read.MapGet("/{planId:guid}", GetAsync)
            .WithSummary("One plan version")
            .Produces<PlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        write.MapPost("/", CreateAsync)
            .WithSummary("Create a plan (version 1, draft)")
            .WithDescription("Finance or SuperAdmin. 409 `billing.plan.code_taken` when the code exists. Audited as plan.create.")
            .Produces<PlanResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        write.MapPut("/{planId:guid}", UpdateAsync)
            .WithSummary("Edit a draft version")
            .WithDescription("Finance or SuperAdmin. Published and archived versions are fixed (409 `billing.plan.not_draft`): start a new version instead.")
            .Produces<PlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        write.MapPost("/{planId:guid}/versions", NewVersionAsync)
            .WithSummary("Start a new draft version from this one")
            .WithDescription("Finance or SuperAdmin. 409 `billing.plan.draft_exists` when the plan already has a draft.")
            .Produces<PlanResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        write.MapPost("/{planId:guid}/publish", PublishAsync)
            .RequireStepUp()
            .WithSummary("Publish a draft; the plan's previous published version is archived")
            .WithDescription("Finance or SuperAdmin, step-up. Subscriptions keep the version they are on until renewal. The public price list updates within a minute.")
            .Produces<PlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        write.MapPost("/{planId:guid}/archive", ArchiveAsync)
            .RequireStepUp()
            .WithSummary("Withdraw a version from sale")
            .WithDescription("Finance or SuperAdmin, step-up, `reason` required. Archiving the published version takes the plan off the price list.")
            .Produces<PlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();
    }

    public static void MapPublic(IEndpointRouteBuilder app) =>
        app.MapPublicApi().WithTags("Website")
            .MapGet("/plans", PublicListAsync)
            .WithSummary("Plans on sale, in display order (pricing page)")
            .WithDescription("Published public plans. Cached; a publish shows within a minute.")
            .Produces<IReadOnlyList<PublicPlan>>();

    private static async Task<IResult> ListAsync([AsParameters] PlanSearch search, BillingDbContext db, CancellationToken ct)
    {
        var query = db.Plans.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search.Code))
        {
            var code = search.Code.Trim().ToLowerInvariant();
            query = query.Where(p => p.Code == code);
        }

        if (!string.IsNullOrWhiteSpace(search.Status))
        {
            if (ParseStatus(search.Status) is not { } status)
            {
                return Invalid("status", "validation.invalid");
            }

            query = query.Where(p => p.Status == status);
        }

        var page = Math.Max(1, search.Page ?? 1);
        var pageSize = Math.Clamp(search.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(p => p.SortOrder).ThenBy(p => p.Code).ThenByDescending(p => p.VersionNumber)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new PagedResult<PlanResponse>([.. rows.Select(ToResponse)], total, page, pageSize));
    }

    private static async Task<IResult> GetAsync(Guid planId, BillingDbContext db, CancellationToken ct) =>
        await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct) is { } plan ? Results.Ok(ToResponse(plan)) : Error.NotFound().ToProblem();

    private static async Task<IResult> CreateAsync(CreatePlanRequest request, BillingDbContext db, PlanAudit audit, IClock clock, CancellationToken ct)
    {
        var code = request.Code?.Trim().ToLowerInvariant() ?? string.Empty;
        var errors = Validate(request.Plan, "plan.");
        if (!CodePattern().IsMatch(code))
        {
            errors["code"] = ["validation.invalid"];
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        if (await db.Plans.AnyAsync(p => p.Code == code, ct))
        {
            return Error.Conflict("billing.plan.code_taken").ToProblem();
        }

        var plan = Plan.Create(code, ToDraft(request.Plan), audit.StaffId, clock.UtcNow);
        db.Plans.Add(plan);
        await audit.RecordAsync(db, plan, "plan.create", null, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/v1/plans/{plan.Id}", ToResponse(plan));
    }

    private static async Task<IResult> UpdateAsync(Guid planId, PlanRequest request, BillingDbContext db, PlanAudit audit, CancellationToken ct)
    {
        if (Validate(request, string.Empty) is { Count: > 0 } errors)
        {
            return Invalid(errors);
        }

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (plan.Status != PlanStatus.Draft)
        {
            return Error.Conflict("billing.plan.not_draft").ToProblem();
        }

        plan.Update(ToDraft(request), audit.StaffId);
        await audit.RecordAsync(db, plan, "plan.update", null, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(plan));
    }

    private static async Task<IResult> NewVersionAsync(Guid planId, BillingDbContext db, PlanAudit audit, IClock clock, CancellationToken ct)
    {
        var source = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (source is null)
        {
            return Error.NotFound().ToProblem();
        }

        var versions = await db.Plans.AsNoTracking().Where(p => p.Code == source.Code).Select(p => new { p.VersionNumber, p.Status }).ToListAsync(ct);
        if (versions.Any(v => v.Status == PlanStatus.Draft))
        {
            return Error.Conflict("billing.plan.draft_exists").ToProblem();
        }

        var next = source.NextVersion(versions.Max(v => v.VersionNumber) + 1, audit.StaffId, clock.UtcNow);
        db.Plans.Add(next);
        await audit.RecordAsync(db, next, "plan.version", null, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("billing.plan.draft_exists").ToProblem();
        }

        return Results.Created($"/admin/v1/plans/{next.Id}", ToResponse(next));
    }

    private static async Task<IResult> PublishAsync(Guid planId, PlanActionRequest? request, BillingDbContext db, PlanAudit audit, ICacheInvalidator cache, IClock clock, CancellationToken ct)
    {
        if (Reason(request?.Reason, required: false) is { } invalid)
        {
            return invalid;
        }

        // Archive the previous version and publish this one in one transaction: the unique index allows one published version per code.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, token);
            if (plan is null)
            {
                return Error.NotFound().ToProblem();
            }

            if (plan.Status != PlanStatus.Draft)
            {
                return Error.Conflict("billing.plan.not_draft").ToProblem();
            }

            var now = clock.UtcNow;
            var previous = await db.Plans.FirstOrDefaultAsync(p => p.Code == plan.Code && p.Status == PlanStatus.Published, token);
            if (previous is not null)
            {
                previous.Archive(audit.StaffId);
                await db.SaveChangesAsync(token);
            }

            plan.Publish(audit.StaffId, now);
            await audit.RecordAsync(db, plan, "plan.publish", request?.Reason, token, new { replacedVersion = previous?.VersionNumber });
            cache.Enqueue(db, CacheTags.Plans);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Results.Ok(ToResponse(plan));
        }, ct);
    }

    private static async Task<IResult> ArchiveAsync(Guid planId, PlanActionRequest request, BillingDbContext db, PlanAudit audit, ICacheInvalidator cache, CancellationToken ct)
    {
        if (Reason(request.Reason, required: true) is { } invalid)
        {
            return invalid;
        }

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null)
        {
            return Error.NotFound().ToProblem();
        }

        var wasPublished = plan.Status == PlanStatus.Published;
        plan.Archive(audit.StaffId);
        await audit.RecordAsync(db, plan, "plan.archive", request.Reason, ct);
        if (wasPublished)
        {
            cache.Enqueue(db, CacheTags.Plans);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(plan));
    }

    private static async Task<IResult> PublicListAsync(BillingDbContext db, HybridCache cache, CancellationToken ct) =>
        Results.Ok(await cache.GetOrCreateAsync(
            "plans:public",
            db,
            static async (db, token) =>
            {
                var plans = await db.Plans.AsNoTracking().Where(p => p.Status == PlanStatus.Published && p.IsPublic)
                    .OrderBy(p => p.SortOrder).ThenBy(p => p.MonthlyPrice).ToListAsync(token);
                return plans.Select(ToPublic).ToList();
            },
            PublicEntry,
            [CacheTags.Plans],
            ct));

    internal static Dictionary<string, string[]> Validate(PlanRequest? request, string prefix)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[prefix.TrimEnd('.') is { Length: > 0 } p ? p : "body"] = ["validation.required"];
            return errors;
        }

        void Add(string field, string code) => errors[prefix + field] = [code];

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            Add("name", "validation.required");
        }
        else if (request.Name.Trim().Length > 50)
        {
            Add("name", "validation.too_long");
        }

        if (request.Tagline is { Length: > 120 })
        {
            Add("tagline", "validation.too_long");
        }

        if (request.Description is { Length: > 1000 })
        {
            Add("description", "validation.too_long");
        }

        foreach (var (field, money) in new[] { ("monthly", (Money?)request.Monthly), ("annual", request.Annual), ("overagePerConversation", request.OveragePerConversation) })
        {
            if (money is { } m && (m.Currency != Currency.Irr || m.Amount < 0))
            {
                Add(field, "validation.money.invalid");
            }
        }

        if (request.IsFree && request.Monthly.Amount != 0)
        {
            Add("monthly", "billing.plan.free_has_price");
        }

        foreach (var (meter, limit) in request.Limits ?? [])
        {
            if (!BillingMeters.All.Contains(meter))
            {
                Add("limits." + meter, "billing.meter.unknown");
            }
            else if (limit is < 0)
            {
                Add("limits." + meter, "validation.range");
            }
        }

        var features = request.Features ?? [];
        if (features.Count > 20)
        {
            Add("features", "validation.too_many");
        }

        for (var i = 0; i < features.Count; i++)
        {
            var f = features[i];
            if (string.IsNullOrWhiteSpace(f.Key) || f.Key.Length > 40 || string.IsNullOrWhiteSpace(f.Label) || f.Label.Length > 80 || f.Limit is { Length: > 40 })
            {
                Add($"features[{i}]", "validation.invalid");
            }
        }

        return errors;
    }

    private static PlanDraft ToDraft(PlanRequest r) =>
        new(r.Name, r.Tagline, r.Description, r.Monthly.Amount, r.Annual?.Amount, r.OveragePerConversation?.Amount, r.Limits ?? [], r.Features ?? [],
            r.IsPublic, r.IsFree, r.Highlighted, r.SortOrder);

    private static PlanResponse ToResponse(Plan p) =>
        new(p.Id, p.Code, p.VersionNumber, Plan.StatusName(p.Status), p.Name, p.Tagline, p.Description, Money.Rial(p.MonthlyPrice), Rial(p.AnnualPrice),
            Rial(p.OveragePerConversation), p.Limits, p.Features, p.IsPublic, p.IsFree, p.Highlighted, p.SortOrder, p.CreatedAt, p.UpdatedAt, p.PublishedAt);

    private static PublicPlan ToPublic(Plan p) =>
        new(p.Code, p.Name, p.Tagline, p.Description, Money.Rial(p.MonthlyPrice), Rial(p.AnnualPrice), p.Limit(BillingMeters.AiConversations),
            Rial(p.OveragePerConversation), p.Limit(BillingMeters.ChannelAccounts), p.Limit(BillingMeters.Seats), p.Highlighted, p.IsFree, p.Limits, p.Features);

    private static Money? Rial(long? amount) => amount is { } a ? Money.Rial(a) : null;

    private static PlanStatus? ParseStatus(string value) =>
        Enum.GetValues<PlanStatus>().Cast<PlanStatus?>().FirstOrDefault(s => Plan.StatusName(s!.Value) == value.Trim().ToLowerInvariant());

    private static IResult? Reason(string? reason, bool required)
    {
        var length = reason?.Trim().Length ?? 0;
        return (length == 0 && !required) || length is >= 5 and <= 300 ? null : Invalid("reason", length < 5 ? "validation.too_short" : "validation.too_long");
    }

    private static IResult Invalid(string field, string code) => Invalid(new Dictionary<string, string[]> { [field] = [code] });

    private static IResult Invalid(Dictionary<string, string[]> errors) => Error.Validation("validation.failed", errors).ToProblem();
}

/// <summary>Plan changes are platform-level audit entries by the signed-in staff member.</summary>
internal sealed class PlanAudit(ICurrentUser caller, IStaffDirectory staff, IClock clock)
{
    public Guid StaffId => caller.UserId;

    public async Task RecordAsync(ModuleDbContext db, Plan plan, string action, string? reason, CancellationToken ct, object? extra = null)
    {
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), action, "plan", plan.Id.ToString(), null, clock.UtcNow,
            after: new { plan.Code, version = plan.VersionNumber, status = Plan.StatusName(plan.Status), monthly = plan.MonthlyPrice, plan.Limits, extra },
            reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
    }
}
