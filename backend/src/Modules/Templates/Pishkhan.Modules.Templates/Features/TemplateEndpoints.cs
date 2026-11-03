using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Templates.Domain;
using Pishkhan.Modules.Templates.Persistence;
using Pishkhan.Modules.Templates.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Templates.Features;

/// <summary>A template in the panel's list. <c>recommended</c>: it is for the workspace's industry. <c>lastApplied</c>: when this workspace last applied it.</summary>
public sealed record TemplateSummaryResponse(
    Guid Id, string Industry, int Version, string Name, string? Description, int RequiredFacts, int Faqs, int Flows, int Rules, bool HasAgentProfile,
    bool Recommended, DateTimeOffset? LastApplied);

public sealed record TemplateFaqPreview(string Key, string Question, string Answer);

public sealed record TemplateItemPreview(string Key, string Name);

public sealed record TemplateRulePreview(string Key, string Name, string TriggerKind);

public sealed record TemplateFactPreview(string Key, string Label, string Prompt);

/// <summary>Everything applying the template would add, for the onboarding preview.</summary>
public sealed record TemplatePreviewResponse(
    TemplateSummaryResponse Template, IReadOnlyList<TemplateFactPreview> RequiredFacts, IReadOnlyList<TemplateFaqPreview> Faqs,
    IReadOnlyList<TemplateItemPreview> Flows, IReadOnlyList<TemplateRulePreview> Rules, TemplateAgentProfile? AgentProfile);

/// <summary>
/// What applying a template added. FAQs and rules are added switched off for the owner to check; flows are published; the agent profile is
/// set only when the workspace had none (<c>profileApplied</c>). <c>skipped</c> counts items the workspace already had or had deleted.
/// </summary>
public sealed record TemplateApplicationResponse(
    Guid Id, Guid TemplateId, string Industry, int Version, int FaqsAdded, int FlowsAdded, int RulesAdded, bool ProfileApplied, int Skipped, DateTimeOffset AppliedAt);

/// <summary>The editable part of a template version. <c>content</c> uses the panel API's JSON shapes; see the changelog for details.</summary>
public sealed record TemplateRequest(string Name, string? Description, TemplateContent? Content);

/// <summary><c>industry</c>: clothing, cosmetics, jewelry, beauty_clinic, courses or other.</summary>
public sealed record CreateTemplateRequest(string Industry, TemplateRequest Template);

/// <summary>Why the template is published or archived, kept in the audit log (optional for publish, 5 to 300 characters when given).</summary>
public sealed record TemplateActionRequest(string? Reason);

/// <summary>One version of an industry template. <c>status</c>: draft, published, archived. <c>issues</c> must be empty to publish.</summary>
public sealed record AdminTemplateResponse(
    Guid Id, string Industry, int Version, string Status, string Name, string? Description, TemplateContent Content, IReadOnlyList<string> Issues,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

public sealed record TemplateSearch(string? Industry, string? Status, int? Page, int? PageSize);

/// <summary>Industry templates (BE-2.9, gap G32): the panel's list, preview and apply, and the back-office editor.</summary>
internal static class TemplateEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.AiOps, StaffRoles.Support, StaffRoles.ReadOnly];

    public static void MapPanel(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/templates").WithTags("Templates");

        ws.MapGet("/", ListPublishedAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Industry templates the workspace can apply, its own industry's first")
            .Produces<IReadOnlyList<TemplateSummaryResponse>>();

        ws.MapGet("/{templateId:guid}", PreviewAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("What applying the template would add")
            .Produces<TemplatePreviewResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost("/{templateId:guid}/apply", ApplyAsync)
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithIdempotency()
            .WithSummary("Apply an industry template")
            .WithDescription("Adds the template's FAQs (switched off), flows (published), rules (switched off) and, when the workspace has none, the AI agent profile. Applying again adds only what the workspace never had; nothing it changed or deleted is touched.")
            .Produces<TemplateApplicationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet("/applications", ListApplicationsAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Templates applied to the workspace, newest first")
            .Produces<IReadOnlyList<TemplateApplicationResponse>>();
    }

    public static void MapAdmin(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Templates");
        var read = admin.MapGroup("/industry-templates").RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var write = admin.MapGroup("/industry-templates").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.AiOps));

        read.MapGet("/", AdminListAsync)
            .WithSummary("Industry template versions")
            .Produces<PagedResult<AdminTemplateResponse>>();

        read.MapGet("/{templateId:guid}", AdminGetAsync)
            .WithSummary("One template version")
            .Produces<AdminTemplateResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        write.MapPost("/", CreateAsync)
            .WithSummary("Create the first draft for an industry without a template")
            .Produces<AdminTemplateResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        write.MapPut("/{templateId:guid}", UpdateAsync)
            .WithSummary("Edit a draft; issues are returned, not rejected")
            .Produces<AdminTemplateResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        write.MapPost("/{templateId:guid}/versions", NewVersionAsync)
            .WithSummary("Start a new draft from this version")
            .Produces<AdminTemplateResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        write.MapPost("/{templateId:guid}/publish", PublishAsync)
            .RequireStepUp()
            .WithSummary("Publish a draft; the industry's published version is archived")
            .Produces<AdminTemplateResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        write.MapPost("/{templateId:guid}/archive", ArchiveAsync)
            .RequireStepUp()
            .WithSummary("Archive a version; an archived published version leaves the industry without a template")
            .Produces<AdminTemplateResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ---- Panel ----

    private static TemplateSummaryResponse Summary(IndustryTemplate t, string? industry, DateTimeOffset? lastApplied) =>
        new(t.Id, t.Industry, t.Version, t.Name, t.Description, t.Content.RequiredFacts?.Count ?? 0, t.Content.Faqs?.Count ?? 0,
            t.Content.Flows?.Count ?? 0, t.Content.Rules?.Count ?? 0, t.Content.AgentProfile is not null, t.Industry == industry, lastApplied);

    private static TemplateApplicationResponse ToResponse(TemplateApplication a) =>
        new(a.Id, a.TemplateId, a.Industry, a.Version, a.FaqsAdded, a.FlowsAdded, a.RulesAdded, a.ProfileApplied, a.Skipped, a.CreatedAt);

    private static async Task<IResult> ListPublishedAsync(Guid workspaceId, TemplatesDbContext db, IWorkspaceQueries workspaces, CancellationToken ct)
    {
        var industry = await workspaces.GetIndustryAsync(workspaceId, ct);
        var templates = await db.Templates.AsNoTracking().Where(t => t.Status == TemplateStatuses.Published).ToListAsync(ct);
        var applied = await db.Applications.AsNoTracking()
            .GroupBy(a => a.Industry)
            .Select(g => new { Industry = g.Key, Last = g.Max(a => a.CreatedAt) })
            .ToDictionaryAsync(x => x.Industry, x => x.Last, StringComparer.Ordinal, ct);
        var ordered = templates
            .OrderByDescending(t => t.Industry == industry)
            .ThenBy(t => Array.IndexOf(TemplateContentValidator.Industries, t.Industry));
        return Results.Ok(ordered.Select(t => Summary(t, industry, applied.TryGetValue(t.Industry, out var at) ? at : null)).ToList());
    }

    private static async Task<IResult> PreviewAsync(Guid workspaceId, Guid templateId, TemplatesDbContext db, IWorkspaceQueries workspaces, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.Status == TemplateStatuses.Published, ct);
        if (template is null)
        {
            return Error.NotFound().ToProblem();
        }

        var industry = await workspaces.GetIndustryAsync(workspaceId, ct);
        var lastApplied = await db.Applications.AsNoTracking().Where(a => a.Industry == template.Industry).MaxAsync(a => (DateTimeOffset?)a.CreatedAt, ct);
        var c = template.Content;
        return Results.Ok(new TemplatePreviewResponse(
            Summary(template, industry, lastApplied),
            [.. (c.RequiredFacts ?? []).Select(f => new TemplateFactPreview(f.Key, f.Label, f.Prompt))],
            [.. (c.Faqs ?? []).Select(f => new TemplateFaqPreview(f.Key, f.Question, f.Answer))],
            [.. (c.Flows ?? []).Select(f => new TemplateItemPreview(f.Key, f.Name))],
            [.. (c.Rules ?? []).Select(r => new TemplateRulePreview(r.Key, r.Name, r.TriggerKind))],
            c.AgentProfile));
    }

    private static async Task<IResult> ApplyAsync(
        Guid workspaceId, Guid templateId, TemplatesDbContext db, TemplateApplier applier, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.Status == TemplateStatuses.Published, ct);
        if (template is null)
        {
            return Error.NotFound().ToProblem();
        }

        var outcome = await applier.ApplyAsync(workspaceId, template.Content, caller.UserId, ct);
        var now = clock.UtcNow;
        var application = TemplateApplication.Record(
            workspaceId, template, caller.UserId, outcome.FaqsAdded, outcome.FlowsAdded, outcome.RulesAdded, outcome.ProfileApplied, outcome.Skipped, now);
        db.Applications.Add(application);
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), "workspace.template_applied", "industry_template", template.Id.ToString(), workspaceId, now,
            after: new
            {
                template.Industry, template.Version, outcome.FaqsAdded, outcome.FlowsAdded, outcome.RulesAdded, outcome.ProfileApplied, outcome.Skipped,
            });
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(application));
    }

    private static async Task<IResult> ListApplicationsAsync(Guid workspaceId, TemplatesDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Applications.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(50).ToListAsync(ct)).Select(ToResponse).ToList());

    // ---- Back office ----

    private static AdminTemplateResponse ToAdmin(IndustryTemplate t, TemplateContentValidator validator) =>
        new(t.Id, t.Industry, t.Version, t.Status, t.Name, t.Description, t.Content, validator.Validate(t.Content), t.CreatedAt, t.UpdatedAt, t.PublishedAt);

    private static async Task<IResult> AdminListAsync([AsParameters] TemplateSearch search, TemplatesDbContext db, TemplateContentValidator validator, CancellationToken ct)
    {
        var query = db.Templates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search.Industry))
        {
            query = query.Where(t => t.Industry == search.Industry);
        }

        if (!string.IsNullOrWhiteSpace(search.Status))
        {
            if (!TemplateStatuses.All.Contains(search.Status))
            {
                return Invalid("status", "validation.invalid");
            }

            query = query.Where(t => t.Status == search.Status);
        }

        var page = Math.Max(1, search.Page ?? 1);
        var pageSize = Math.Clamp(search.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(t => t.Industry).ThenByDescending(t => t.Version).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new PagedResult<AdminTemplateResponse>([.. rows.Select(t => ToAdmin(t, validator))], total, page, pageSize));
    }

    private static async Task<IResult> AdminGetAsync(Guid templateId, TemplatesDbContext db, TemplateContentValidator validator, CancellationToken ct) =>
        await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct) is { } template
            ? Results.Ok(ToAdmin(template, validator))
            : Error.NotFound().ToProblem();

    private static async Task<IResult> CreateAsync(
        CreateTemplateRequest request, TemplatesDbContext db, TemplateContentValidator validator, TemplateAudit audit, IClock clock, CancellationToken ct)
    {
        var errors = ValidateRequest(request.Template, "template.");
        if (!TemplateContentValidator.Industries.Contains(request.Industry ?? string.Empty))
        {
            errors["industry"] = ["validation.industry.invalid"];
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        if (await db.Templates.AnyAsync(t => t.Industry == request.Industry!, ct))
        {
            return Error.Conflict("templates.industry_taken").ToProblem();
        }

        var now = clock.UtcNow;
        var template = IndustryTemplate.Create(request.Industry!, request.Template.Name, request.Template.Description, request.Template.Content!, audit.StaffId, now);
        db.Templates.Add(template);
        await audit.RecordAsync(db, template, "industry_template.create", null, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("templates.industry_taken").ToProblem();
        }

        return Results.Created($"/admin/v1/industry-templates/{template.Id}", ToAdmin(template, validator));
    }

    private static async Task<IResult> UpdateAsync(
        Guid templateId, TemplateRequest request, TemplatesDbContext db, TemplateContentValidator validator, TemplateAudit audit, IClock clock, CancellationToken ct)
    {
        if (ValidateRequest(request, string.Empty) is { Count: > 0 } errors)
        {
            return Invalid(errors);
        }

        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (template.Status != TemplateStatuses.Draft)
        {
            return Error.Conflict("templates.not_draft").ToProblem();
        }

        template.Update(request.Name, request.Description, request.Content!, audit.StaffId, clock.UtcNow);
        await audit.RecordAsync(db, template, "industry_template.update", null, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToAdmin(template, validator));
    }

    private static async Task<IResult> NewVersionAsync(
        Guid templateId, TemplatesDbContext db, TemplateContentValidator validator, TemplateAudit audit, IClock clock, CancellationToken ct)
    {
        var source = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (source is null)
        {
            return Error.NotFound().ToProblem();
        }

        var versions = await db.Templates.AsNoTracking().Where(t => t.Industry == source.Industry).Select(t => new { t.Version, t.Status }).ToListAsync(ct);
        if (versions.Any(v => v.Status == TemplateStatuses.Draft))
        {
            return Error.Conflict("templates.draft_exists").ToProblem();
        }

        var next = source.NextVersion(versions.Max(v => v.Version) + 1, audit.StaffId, clock.UtcNow);
        db.Templates.Add(next);
        await audit.RecordAsync(db, next, "industry_template.version", null, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("templates.draft_exists").ToProblem();
        }

        return Results.Created($"/admin/v1/industry-templates/{next.Id}", ToAdmin(next, validator));
    }

    private static async Task<IResult> PublishAsync(
        Guid templateId, TemplateActionRequest? request, TemplatesDbContext db, TemplateContentValidator validator, TemplateAudit audit, IClock clock, CancellationToken ct)
    {
        if (Reason(request?.Reason, required: false) is { } invalid)
        {
            return invalid;
        }

        // Archive the previous version and publish this one in one transaction: the unique index allows one published version per industry.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == templateId, token);
            if (template is null)
            {
                return Error.NotFound().ToProblem();
            }

            if (template.Status != TemplateStatuses.Draft)
            {
                return Error.Conflict("templates.not_draft").ToProblem();
            }

            if (validator.Validate(template.Content) is { Count: > 0 } issues)
            {
                return Results.Problem(
                    title: "templates.invalid", detail: string.Join("; ", issues.Take(20)), statusCode: StatusCodes.Status422UnprocessableEntity,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "templates.invalid", ["issues"] = issues });
            }

            var now = clock.UtcNow;
            var previous = await db.Templates.FirstOrDefaultAsync(t => t.Industry == template.Industry && t.Status == TemplateStatuses.Published, token);
            if (previous is not null)
            {
                previous.Archive(audit.StaffId, now);
                await db.SaveChangesAsync(token);
            }

            template.Publish(audit.StaffId, now);
            await audit.RecordAsync(db, template, "industry_template.publish", request?.Reason, token, new { replacedVersion = previous?.Version });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Results.Ok(ToAdmin(template, validator));
        }, ct);
    }

    private static async Task<IResult> ArchiveAsync(
        Guid templateId, TemplateActionRequest request, TemplatesDbContext db, TemplateContentValidator validator, TemplateAudit audit, IClock clock, CancellationToken ct)
    {
        if (Reason(request.Reason, required: true) is { } invalid)
        {
            return invalid;
        }

        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null)
        {
            return Error.NotFound().ToProblem();
        }

        template.Archive(audit.StaffId, clock.UtcNow);
        await audit.RecordAsync(db, template, "industry_template.archive", request.Reason, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToAdmin(template, validator));
    }

    private static Dictionary<string, string[]> ValidateRequest(TemplateRequest? request, string prefix)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[prefix.TrimEnd('.') is { Length: > 0 } p ? p : "body"] = ["validation.required"];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > IndustryTemplate.MaxName)
        {
            errors[prefix + "name"] = ["validation.name.length"];
        }

        if (request.Description is { Length: > IndustryTemplate.MaxDescription })
        {
            errors[prefix + "description"] = ["validation.text.length"];
        }

        if (request.Content is null)
        {
            errors[prefix + "content"] = ["validation.required"];
        }

        return errors;
    }

    private static IResult? Reason(string? reason, bool required)
    {
        var length = reason?.Trim().Length ?? 0;
        return (length == 0 && !required) || length is >= 5 and <= 300 ? null : Invalid("reason", length < 5 ? "validation.too_short" : "validation.too_long");
    }

    private static IResult Invalid(string field, string code) => Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [code] });

    private static IResult Invalid(Dictionary<string, string[]> errors) => Error.Validation("validation.failed", errors).ToProblem();
}

/// <summary>Template changes are platform-level audit entries by the signed-in staff member.</summary>
internal sealed class TemplateAudit(ICurrentUser caller, IStaffDirectory staff, IClock clock)
{
    public Guid StaffId => caller.UserId;

    public async Task RecordAsync(ModuleDbContext db, IndustryTemplate template, string action, string? reason, CancellationToken ct, object? extra = null)
    {
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), action, "industry_template", template.Id.ToString(), null,
            clock.UtcNow,
            after: new { template.Industry, template.Version, template.Status, faqs = template.Content.Faqs?.Count, flows = template.Content.Flows?.Count, rules = template.Content.Rules?.Count, extra },
            reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
    }
}
