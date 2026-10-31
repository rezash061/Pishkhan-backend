using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pishkhan.AI;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Evals;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Agent.Services;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Agent.Features;

/// <summary>A provider. <c>source</c>: configuration (appsettings or environment) or database (set here, wins). The key is never returned.</summary>
public sealed record AiProviderResponse(
    string Name, string Kind, string? BaseUrl, bool HasApiKey, bool Enabled, string Source, string? LastTestError, DateTimeOffset? LastTestedAt);

/// <summary>
/// <c>kind</c>: openai (any OpenAI-compatible API) or fake. <c>apiKey</c> is write-only: null keeps the stored key,
/// <c>clearApiKey</c> removes it. A disabled provider is skipped by every route.
/// </summary>
public sealed record AiProviderRequest(string Kind, string? BaseUrl, string? ApiKey, bool ClearApiKey, bool Enabled, string Reason);

public sealed record AiProviderTestRequest(string Model);

/// <summary><c>error</c> is null when the provider answered.</summary>
public sealed record AiProviderTestResponse(bool Ok, string? Error, int LatencyMs);

/// <summary>One step of a fallback chain. Prices are Rial per million tokens (cost metering).</summary>
public sealed record AiRouteStep(
    string Provider, string Model, float? Temperature, int? MaxOutputTokens, int TimeoutSeconds, decimal? InputPricePerMillion, decimal? OutputPricePerMillion);

/// <summary>A task's chain, tried in order. <c>source</c>: database (set here) or configuration; none when the task has no route.</summary>
public sealed record AiTaskRoutes(string Task, string Source, IReadOnlyList<AiRouteStep> Routes);

/// <summary>An empty <c>routes</c> list removes the back-office chain, so the task falls back to configuration.</summary>
public sealed record AiRoutesRequest(IReadOnlyList<AiRouteStep> Routes, string Reason);

/// <summary>A prompt key. <c>activeSource</c>: database (activated here through the eval gate), configuration, or latest (no activation: the highest version).</summary>
public sealed record PromptKeyResponse(string Key, string ActiveVersion, string ActiveSource, int VersionCount);

/// <summary>One version. <c>source</c>: configuration or database. <c>latestRun</c> is the newest eval run of this version.</summary>
public sealed record PromptVersionResponse(
    string Key, string Version, string Text, string Source, string? BaseVersion, bool Active, DateTimeOffset? CreatedAt, EvalRunResponse? LatestRun);

/// <summary>A new version of <c>baseVersion</c> (for the diff view). Text up to 20 000 characters; <c>{{name}}</c> placeholders are kept.</summary>
public sealed record PromptVersionRequest(string Text, string? BaseVersion);

/// <summary>Activates a version. <c>evalRunId</c> must be a completed run of exactly this key and version that passes the gate.</summary>
public sealed record PromptActivateRequest(Guid EvalRunId, string Reason);

public sealed record EvalGateCheck(string Name, decimal Required, decimal? Actual, bool Passed);

/// <summary>The activation gate for a run: every check must pass.</summary>
public sealed record EvalGate(bool Passed, IReadOnlyList<EvalGateCheck> Checks);

/// <summary>An eval run. <c>status</c>: running, completed, failed.</summary>
public sealed record EvalRunResponse(
    Guid Id, Guid SetId, string SetName, string Task, string PromptKey, string PromptVersion, string? Route, string Status, int CaseCount, int ErrorCount,
    decimal? IntentAccuracy, long InputTokens, long OutputTokens, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, EvalGate Gate);

public sealed record EvalResultResponse(
    Guid CaseId, string Ref, string? Vertical, string Input, string? ExpectedIntent, string? PredictedIntent, bool? Correct, string? Route, int LatencyMs, string? Error);

public sealed record EvalRunDetail(EvalRunResponse Run, IReadOnlyList<EvalResultResponse> Results);

public sealed record EvalSetResponse(Guid Id, string Name, string? Description, int CaseCount, DateTimeOffset UpdatedAt);

/// <summary>Runs the intent classifier prompt (<c>intent.classify</c>) at <c>promptVersion</c> over the set.</summary>
public sealed record EvalRunRequest(Guid SetId, string PromptKey, string PromptVersion);

public sealed record EvalRunSearch(string? PromptKey, string? PromptVersion, Guid? SetId, int? Page, int? PageSize);

/// <summary>AI operations in the back office (admin plan 4.5, BE-1.12): providers, model routes, prompt versions with the eval gate, eval sets and runs.</summary>
internal static partial class AiAdminEndpoints
{
    private const int MaxTextLength = 20_000;
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.AiOps, StaffRoles.ReadOnly];
    private static readonly string[] Tasks = [AiTasks.Classify, AiTasks.GenerateReply, AiTasks.ExtractReceipt, AiTasks.Transcribe, AiTasks.Embed, AiTasks.Summarize, AiTasks.Content];

    [GeneratedRegex("^[a-z][a-z0-9_-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_.]{1,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().MapGroup("/ai").WithTags("AI");
        var read = admin.MapGroup("/").RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var ops = admin.MapGroup("/").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.AiOps));
        var super = admin.MapGroup("/").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.SuperAdmin));

        read.MapGet("/providers", ListProviders)
            .WithSummary("AI providers in effect").WithDescription("Roles: SuperAdmin, AIOps, ReadOnly. Keys are never returned.")
            .Produces<IReadOnlyList<AiProviderResponse>>();
        super.MapPut("/providers/{name}", PutProviderAsync)
            .RequireStepUp()
            .WithSummary("Create or change a provider").WithDescription("SuperAdmin, step-up. Takes effect on every host within the refresh interval; audited as ai.provider.update.")
            .Produces<AiProviderResponse>().WithStandardProblems();
        super.MapPost("/providers/{name}/test", TestProviderAsync)
            .WithSummary("Send one tiny request through a provider").WithDescription("SuperAdmin. The provider as it is in effect now; the result is kept on a back-office provider.")
            .Produces<AiProviderTestResponse>().ProducesProblem(StatusCodes.Status404NotFound).WithStandardProblems();

        read.MapGet("/routes", ListRoutes)
            .WithSummary("Model routes per task").WithDescription("Roles: SuperAdmin, AIOps, ReadOnly. Every task, with its fallback chain in effect.")
            .Produces<IReadOnlyList<AiTaskRoutes>>();
        ops.MapPut("/routes/{task}", PutRoutesAsync)
            .WithSummary("Set a task's fallback chain").WithDescription("AIOps or SuperAdmin. Up to 5 steps over providers in effect; an empty list falls back to configuration. Audited as ai.routes.update.")
            .Produces<AiTaskRoutes>().ProducesProblem(StatusCodes.Status404NotFound).WithStandardProblems();

        read.MapGet("/prompts", ListPrompts)
            .WithSummary("Prompt keys with their active version").Produces<IReadOnlyList<PromptKeyResponse>>();
        read.MapGet("/prompts/{key}", GetPromptAsync)
            .WithSummary("Every version of a prompt, newest first, with its latest eval run")
            .Produces<IReadOnlyList<PromptVersionResponse>>().ProducesProblem(StatusCodes.Status404NotFound);
        ops.MapPost("/prompts/{key}/versions", CreateVersionAsync)
            .WithSummary("Write a new version").WithDescription("AIOps or SuperAdmin. Numbered after the highest existing version; not active until it passes the eval gate. Audited as ai.prompt.create.")
            .Produces<PromptVersionResponse>(StatusCodes.Status201Created).WithStandardProblems();
        ops.MapPost("/prompts/{key}/versions/{version}/activate", ActivateAsync)
            .WithSummary("Activate a version through the eval gate")
            .WithDescription("AIOps or SuperAdmin. 409 `ai.prompt.eval_gate` (with the gate in `errors`) unless the run is a completed run of this version that passes every check. Audited as ai.prompt.activate.")
            .Produces<PromptKeyResponse>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).WithStandardProblems();

        read.MapGet("/eval-sets", ListSetsAsync).WithSummary("Eval sets with their case counts").Produces<IReadOnlyList<EvalSetResponse>>();
        ops.MapPost("/eval-sets/{name}/import", ImportAsync)
            .WithSummary("Import or update cases from JSON Lines")
            .WithDescription("AIOps or SuperAdmin. Body: UTF-8 JSON Lines (`application/x-ndjson` or `text/plain`), one anonymized case per line; a known `ref` updates its case. Creates the set when new.")
            .Accepts<string>("application/x-ndjson", "text/plain")
            .Produces<EvalImportReport>().WithStandardProblems();
        read.MapGet("/eval-runs", ListRunsAsync).WithSummary("Eval runs, newest first").Produces<PagedResult<EvalRunResponse>>();
        read.MapGet("/eval-runs/{runId:guid}", GetRunAsync)
            .WithSummary("A run with its per-case results (failures first)").Produces<EvalRunDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        ops.MapPost("/eval-runs", StartRunAsync)
            .WithSummary("Start an eval run").WithDescription("AIOps or SuperAdmin. Runs in the background (202); poll the run. Only `intent.classify` is evaluated for now.")
            .Produces<EvalRunResponse>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status404NotFound).WithStandardProblems();
    }

    private static IResult ListProviders(AiSettingsStore store, AgentDbContext db)
    {
        var snapshot = store.GetSnapshot();
        var rows = db.AiProviders.AsNoTracking().ToDictionary(p => p.Name, StringComparer.Ordinal);
        var names = snapshot.Effective.Providers.Keys.Concat(rows.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return Results.Ok(names.Select(n => ToProvider(n, snapshot, rows.GetValueOrDefault(n))).ToList());
    }

    private static async Task<IResult> PutProviderAsync(
        string name, AiProviderRequest request, AgentDbContext db, AiSettingsStore store, ISecretProtector secrets, AiAudit audit, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!NamePattern().IsMatch(name))
        {
            errors["name"] = ["validation.invalid"];
        }

        if (request.Kind is not ("openai" or "fake"))
        {
            errors["kind"] = ["validation.invalid"];
        }

        if (request.BaseUrl is { Length: > 0 } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || url.Length > 500))
        {
            errors["baseUrl"] = ["validation.invalid"];
        }

        if (request.ApiKey is { Length: > 500 })
        {
            errors["apiKey"] = ["validation.too_long"];
        }

        AddReason(errors, request.Reason);
        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var row = await db.AiProviders.FirstOrDefaultAsync(p => p.Name == name, ct);
        var before = row is null ? null : new { row.Kind, row.BaseUrl, row.Enabled, hasApiKey = row.ApiKeyEnc is not null };
        if (row is null)
        {
            row = new AiProvider { Name = name, Kind = request.Kind };
            db.AiProviders.Add(row);
        }

        row.Kind = request.Kind;
        row.BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? null : request.BaseUrl.Trim();
        row.Enabled = request.Enabled;
        row.UpdatedBy = audit.StaffId;
        if (request.ClearApiKey)
        {
            row.ApiKeyEnc = null;
        }
        else if (!string.IsNullOrEmpty(request.ApiKey))
        {
            row.ApiKeyEnc = secrets.Protect(request.ApiKey, AiSettingsStore.ApiKeyPurpose);
        }

        // The key itself never reaches the audit log; only whether one is set and whether it changed.
        await audit.RecordAsync(db, "ai.provider.update", "ai_provider", name, request.Reason, ct, before,
            new { row.Kind, row.BaseUrl, row.Enabled, hasApiKey = row.ApiKeyEnc is not null, apiKeyChanged = request.ClearApiKey || !string.IsNullOrEmpty(request.ApiKey) });
        await db.SaveChangesAsync(ct);
        await store.RefreshAsync(ct);
        return Results.Ok(ToProvider(name, store.GetSnapshot(), row));
    }

    private static async Task<IResult> TestProviderAsync(
        string name, AiProviderTestRequest request, AgentDbContext db, AiSettingsStore store, IAiProviderProbe probe, IClock clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 200)
        {
            return Invalid(new() { ["model"] = ["validation.invalid"] });
        }

        if (!store.GetSnapshot().Effective.Providers.TryGetValue(name, out var provider))
        {
            return Error.NotFound("ai.provider.not_found").ToProblem();
        }

        var started = Stopwatch.GetTimestamp();
        var error = await probe.TestAsync(provider, request.Model.Trim(), ct);
        var latency = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (await db.AiProviders.FirstOrDefaultAsync(p => p.Name == name, ct) is { } row)
        {
            row.LastTestError = error is { Length: > 1000 } ? error[..1000] : error;
            row.LastTestedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new AiProviderTestResponse(error is null, error, latency));
    }

    private static IResult ListRoutes(AiSettingsStore store) =>
        Results.Ok(Tasks.Select(t => ToRoutes(t, store.GetSnapshot())).ToList());

    private static async Task<IResult> PutRoutesAsync(string task, AiRoutesRequest request, AgentDbContext db, AiSettingsStore store, AiAudit audit, IClock clock, CancellationToken ct)
    {
        if (!Tasks.Contains(task))
        {
            return Error.NotFound("ai.task.not_found").ToProblem();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var providers = store.GetSnapshot().Effective.Providers;
        var steps = request.Routes ?? [];
        if (steps.Count > 5)
        {
            errors["routes"] = ["validation.too_many"];
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var at = $"routes[{i}].";
            if (string.IsNullOrWhiteSpace(s.Provider) || !providers.ContainsKey(s.Provider))
            {
                errors[at + "provider"] = ["ai.provider.not_found"];
            }

            if (string.IsNullOrWhiteSpace(s.Model) || s.Model.Length > 200)
            {
                errors[at + "model"] = ["validation.invalid"];
            }

            if (s.Temperature is < 0 or > 2)
            {
                errors[at + "temperature"] = ["validation.range"];
            }

            if (s.MaxOutputTokens is < 1 or > 32_000)
            {
                errors[at + "maxOutputTokens"] = ["validation.range"];
            }

            if (s.TimeoutSeconds is < 1 or > 300)
            {
                errors[at + "timeoutSeconds"] = ["validation.range"];
            }

            if (s.InputPricePerMillion is < 0 || s.OutputPricePerMillion is < 0)
            {
                errors[at + "price"] = ["validation.range"];
            }
        }

        if (steps.Select(s => $"{s.Provider}/{s.Model}").Distinct(StringComparer.Ordinal).Count() != steps.Count)
        {
            errors["routes"] = ["ai.routes.duplicate"];
        }

        AddReason(errors, request.Reason);
        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var before = ToRoutes(task, store.GetSnapshot());
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.AiRoutes.Where(r => r.Task == task).ExecuteDeleteAsync(token);
            var now = clock.UtcNow;
            db.AiRoutes.AddRange(steps.Select((s, i) => new AiRoute
            {
                Id = Uuid.NewV7(now), Task = task, Position = i, Provider = s.Provider, Model = s.Model.Trim(), Temperature = s.Temperature,
                MaxOutputTokens = s.MaxOutputTokens, TimeoutSeconds = s.TimeoutSeconds, InputPricePerMillion = s.InputPricePerMillion,
                OutputPricePerMillion = s.OutputPricePerMillion, UpdatedBy = audit.StaffId,
            }));
            await audit.RecordAsync(db, "ai.routes.update", "ai_route", task, request.Reason, token, before.Routes, steps);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }, ct);

        await store.RefreshAsync(ct);
        return Results.Ok(ToRoutes(task, store.GetSnapshot()));
    }

    private static IResult ListPrompts(AiSettingsStore store)
    {
        var snapshot = store.GetSnapshot();
        return Results.Ok(snapshot.Effective.Prompts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new PromptKeyResponse(kv.Key, ActiveVersion(snapshot, kv.Key)!, ActiveSource(snapshot, kv.Key), kv.Value.Count))
            .ToList());
    }

    private static async Task<IResult> GetPromptAsync(string key, AiSettingsStore store, AgentDbContext db, IAiSettings settings, CancellationToken ct)
    {
        var snapshot = store.GetSnapshot();
        if (!snapshot.Effective.Prompts.TryGetValue(key, out var versions))
        {
            return Error.NotFound().ToProblem();
        }

        var rows = await db.PromptVersions.AsNoTracking().Where(v => v.Key == key).ToDictionaryAsync(v => v.Version, StringComparer.Ordinal, ct);
        var runs = await RunsQuery(db).Where(x => x.Run.PromptKey == key).OrderByDescending(x => x.Run.StartedAt).ToListAsync(ct);
        var active = ActiveVersion(snapshot, key);
        var gate = settings.Current.EvalGate;
        return Results.Ok(versions.Keys.OrderByDescending(VersionOrder).ThenByDescending(v => v, StringComparer.Ordinal).Select(v =>
        {
            var row = rows.GetValueOrDefault(v);
            var latest = runs.FirstOrDefault(x => x.Run.PromptVersion == v);
            return new PromptVersionResponse(
                key, v, versions[v], row is null ? "configuration" : "database", row?.BaseVersion, v == active, row?.CreatedAt,
                latest is null ? null : ToRun(latest.Run, latest.SetName, gate));
        }).ToList());
    }

    private static async Task<IResult> CreateVersionAsync(
        string key, PromptVersionRequest request, AgentDbContext db, AiSettingsStore store, AiAudit audit, IClock clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!KeyPattern().IsMatch(key))
        {
            errors["key"] = ["validation.invalid"];
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            errors["text"] = ["validation.required"];
        }
        else if (request.Text.Length > MaxTextLength)
        {
            errors["text"] = ["validation.too_long"];
        }

        var existing = store.GetSnapshot().Effective.Prompts.GetValueOrDefault(key) ?? [];
        if (request.BaseVersion is { } baseVersion && !existing.ContainsKey(baseVersion))
        {
            errors["baseVersion"] = ["validation.invalid"];
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var dbVersions = await db.PromptVersions.AsNoTracking().Where(v => v.Key == key).Select(v => v.Version).ToListAsync(ct);
        var next = existing.Keys.Concat(dbVersions).Select(VersionOrder).DefaultIfEmpty(0).Max() + 1;
        var version = new PromptVersion
        {
            Id = Uuid.NewV7(clock.UtcNow),
            Key = key,
            Version = next.ToString(CultureInfo.InvariantCulture),
            Text = request.Text,
            BaseVersion = request.BaseVersion,
            CreatedBy = audit.StaffId,
        };
        db.PromptVersions.Add(version);
        await audit.RecordAsync(db, "ai.prompt.create", "prompt", $"{key}@{version.Version}", null, ct, null, new { key, version.Version, request.BaseVersion, length = request.Text.Length });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("ai.prompt.version_taken").ToProblem();
        }

        await store.RefreshAsync(ct);
        return Results.Created($"/admin/v1/ai/prompts/{key}", new PromptVersionResponse(key, version.Version, version.Text, "database", version.BaseVersion, false, version.CreatedAt, null));
    }

    private static async Task<IResult> ActivateAsync(
        string key, string version, PromptActivateRequest request, AgentDbContext db, AiSettingsStore store, AiAudit audit, IAiSettings settings, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddReason(errors, request.Reason);
        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var snapshot = store.GetSnapshot();
        if (!snapshot.Effective.Prompts.TryGetValue(key, out var versions) || !versions.ContainsKey(version))
        {
            return Error.NotFound("ai.prompt.not_found").ToProblem();
        }

        var run = await RunsQuery(db).FirstOrDefaultAsync(x => x.Run.Id == request.EvalRunId, ct);
        if (run is null)
        {
            return Error.NotFound("ai.eval_run.not_found").ToProblem();
        }

        var result = ToRun(run.Run, run.SetName, settings.Current.EvalGate);
        if (run.Run.PromptKey != key || run.Run.PromptVersion != version || !result.Gate.Passed)
        {
            var reasons = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (run.Run.PromptKey != key || run.Run.PromptVersion != version)
            {
                reasons["evalRunId"] = ["ai.eval_run.other_version"];
            }

            foreach (var check in result.Gate.Checks.Where(c => !c.Passed))
            {
                reasons["gate." + check.Name] = [string.Create(CultureInfo.InvariantCulture, $"required {check.Required}, actual {check.Actual?.ToString(CultureInfo.InvariantCulture) ?? "none"}")];
            }

            return new Error("ai.prompt.eval_gate", ErrorType.Conflict, Fields: reasons).ToProblem();
        }

        var previous = ActiveVersion(snapshot, key);
        var activation = await db.PromptActivations.FirstOrDefaultAsync(a => a.Key == key, ct);
        if (activation is null)
        {
            activation = new PromptActivation { Key = key, Version = version };
            db.PromptActivations.Add(activation);
        }

        activation.Version = version;
        activation.EvalRunId = run.Run.Id;
        activation.ActivatedBy = audit.StaffId;
        await audit.RecordAsync(db, "ai.prompt.activate", "prompt", $"{key}@{version}", request.Reason, ct, new { version = previous },
            new { version, evalRunId = run.Run.Id, run.Run.IntentAccuracy, run.Run.CaseCount, run.Run.ErrorCount });
        await db.SaveChangesAsync(ct);
        await store.RefreshAsync(ct);
        var refreshed = store.GetSnapshot();
        return Results.Ok(new PromptKeyResponse(key, ActiveVersion(refreshed, key)!, ActiveSource(refreshed, key), refreshed.Effective.Prompts[key].Count));
    }

    private static async Task<IResult> ListSetsAsync(AgentDbContext db, CancellationToken ct) =>
        Results.Ok(await db.EvalSets.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new EvalSetResponse(s.Id, s.Name, s.Description, db.EvalCases.Count(c => c.SetId == s.Id), s.UpdatedAt))
            .ToListAsync(ct));

    private static async Task<IResult> ImportAsync(string name, HttpRequest http, EvalService evals, AiAudit audit, AgentDbContext db, CancellationToken ct)
    {
        if (!NamePattern().IsMatch(name))
        {
            return Invalid(new() { ["name"] = ["validation.invalid"] });
        }

        if (http.ContentLength is > 10 * 1024 * 1024)
        {
            return Invalid(new() { ["body"] = ["validation.too_long"] });
        }

        using var reader = new StreamReader(http.Body, System.Text.Encoding.UTF8);
        var report = await evals.ImportAsync(name, reader, ct);
        await audit.RecordAsync(db, "ai.eval_set.import", "eval_set", name, null, ct, null, new { report.Imported, report.Updated, errors = report.Errors.Count });
        await db.SaveChangesAsync(ct);
        return Results.Ok(report);
    }

    private static async Task<IResult> ListRunsAsync([AsParameters] EvalRunSearch search, AgentDbContext db, IAiSettings settings, CancellationToken ct)
    {
        var query = RunsQuery(db);
        if (!string.IsNullOrWhiteSpace(search.PromptKey))
        {
            query = query.Where(x => x.Run.PromptKey == search.PromptKey);
        }

        if (!string.IsNullOrWhiteSpace(search.PromptVersion))
        {
            query = query.Where(x => x.Run.PromptVersion == search.PromptVersion);
        }

        if (search.SetId is { } setId)
        {
            query = query.Where(x => x.Run.SetId == setId);
        }

        var page = Math.Max(1, search.Page ?? 1);
        var pageSize = Math.Clamp(search.PageSize ?? 25, 1, 100);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.Run.StartedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var gate = settings.Current.EvalGate;
        return Results.Ok(new PagedResult<EvalRunResponse>([.. rows.Select(x => ToRun(x.Run, x.SetName, gate))], total, page, pageSize));
    }

    private static async Task<IResult> GetRunAsync(Guid runId, AgentDbContext db, IAiSettings settings, CancellationToken ct)
    {
        var run = await RunsQuery(db).FirstOrDefaultAsync(x => x.Run.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound().ToProblem();
        }

        var results = await db.EvalResults.AsNoTracking().Where(r => r.RunId == runId)
            .Join(db.EvalCases.AsNoTracking(), r => r.CaseId, c => c.Id, (r, c) => new EvalResultResponse(
                c.Id, c.ExternalRef, c.Vertical, c.Input, c.ExpectedIntent, r.PredictedIntent, r.IntentCorrect, r.Route, r.LatencyMs, r.Error))
            .ToListAsync(ct);
        var ordered = results.OrderBy(r => r.Error is null && r.Correct != false).ThenBy(r => r.Ref, StringComparer.Ordinal).ToList();
        return Results.Ok(new EvalRunDetail(ToRun(run.Run, run.SetName, settings.Current.EvalGate), ordered));
    }

    private static async Task<IResult> StartRunAsync(
        EvalRunRequest request, AgentDbContext db, EvalService evals, AiSettingsStore store, AiAudit audit, IAiSettings settings, IServiceScopeFactory scopes,
        IHostApplicationLifetime lifetime, ILoggerFactory loggers, CancellationToken ct)
    {
        if (request.PromptKey != EvalService.ClassifyPromptKey)
        {
            return Invalid(new() { ["promptKey"] = ["ai.eval.unsupported_prompt"] });
        }

        var set = await db.EvalSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.SetId, ct);
        if (set is null)
        {
            return Error.NotFound("ai.eval_set.not_found").ToProblem();
        }

        // A version written on another host a moment ago is picked up here.
        await store.RefreshAsync(ct);
        if (!store.Current.Prompts.TryGetValue(request.PromptKey, out var versions) || !versions.ContainsKey(request.PromptVersion))
        {
            return Error.NotFound("ai.prompt.not_found").ToProblem();
        }

        var run = await evals.StartClassifyAsync(set.Id, request.PromptVersion, ct);
        await audit.RecordAsync(db, "ai.eval_run.start", "eval_run", run.Id.ToString(), null, ct, null, new { set = set.Name, request.PromptKey, request.PromptVersion });
        await db.SaveChangesAsync(ct);

        var logger = loggers.CreateLogger(typeof(AiAdminEndpoints));
        _ = Task.Run(async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<EvalService>().ExecuteAsync(run.Id, lifetime.ApplicationStopping);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Eval run {RunId} failed", run.Id);
                await using var cleanup = scopes.CreateAsyncScope();
                await cleanup.ServiceProvider.GetRequiredService<AgentDbContext>().EvalRuns.Where(r => r.Id == run.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "failed").SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow), CancellationToken.None);
            }
        }, CancellationToken.None);

        return Results.Accepted($"/admin/v1/ai/eval-runs/{run.Id}", ToRun(run, set.Name, settings.Current.EvalGate));
    }

    internal static EvalGate Gate(EvalRun run, AiEvalGateOptions gate)
    {
        var errorRate = run.CaseCount == 0 ? (decimal?)null : decimal.Round((decimal)run.ErrorCount / run.CaseCount, 4);
        EvalGateCheck[] checks =
        [
            new("completed", 1, run.Status == "completed" ? 1 : 0, run.Status == "completed"),
            new("cases", gate.MinCases, run.CaseCount, run.CaseCount >= gate.MinCases),
            new("intent_accuracy", gate.MinIntentAccuracy, run.IntentAccuracy, run.IntentAccuracy is { } a && a >= gate.MinIntentAccuracy),
            new("error_rate", gate.MaxErrorRate, errorRate, errorRate is { } e && e <= gate.MaxErrorRate),
        ];
        return new EvalGate(checks.All(c => c.Passed), checks);
    }

    private static IQueryable<RunRow> RunsQuery(AgentDbContext db) =>
        db.EvalRuns.AsNoTracking().Join(db.EvalSets.AsNoTracking(), r => r.SetId, s => s.Id, (r, s) => new RunRow { Run = r, SetName = s.Name });

    private static EvalRunResponse ToRun(EvalRun r, string setName, AiEvalGateOptions gate) =>
        new(r.Id, r.SetId, setName, r.Task, r.PromptKey, r.PromptVersion, r.Route, r.Status, r.CaseCount, r.ErrorCount, r.IntentAccuracy, r.InputTokens,
            r.OutputTokens, r.StartedAt, r.FinishedAt, Gate(r, gate));

    private static AiProviderResponse ToProvider(string name, AiSettingsStore.Snapshot snapshot, AiProvider? row)
    {
        if (row is not null)
        {
            return new AiProviderResponse(name, row.Kind, row.BaseUrl, row.ApiKeyEnc is not null, row.Enabled, "database", row.LastTestError, row.LastTestedAt);
        }

        var p = snapshot.Effective.Providers[name];
        return new AiProviderResponse(name, p.Kind, p.BaseUrl, !string.IsNullOrEmpty(p.ApiKey), true, "configuration", null, null);
    }

    private static AiTaskRoutes ToRoutes(string task, AiSettingsStore.Snapshot snapshot)
    {
        var routes = snapshot.Effective.Routes.GetValueOrDefault(task) ?? [];
        var source = snapshot.DbTasks.Contains(task) ? "database" : routes.Count > 0 ? "configuration" : "none";
        return new AiTaskRoutes(task, source, [.. routes.Select(r =>
        {
            var price = snapshot.Effective.Prices.GetValueOrDefault(r.Key);
            return new AiRouteStep(r.Provider, r.Model, r.Temperature, r.MaxOutputTokens, r.TimeoutSeconds, price?.InputPerMillion, price?.OutputPerMillion);
        })]);
    }

    private static string? ActiveVersion(AiSettingsStore.Snapshot snapshot, string key) =>
        snapshot.Effective.ActivePrompts.TryGetValue(key, out var v) ? v
        : snapshot.Effective.Prompts.TryGetValue(key, out var versions) ? versions.Keys.Max(StringComparer.Ordinal) : null;

    private static string ActiveSource(AiSettingsStore.Snapshot snapshot, string key) =>
        snapshot.Activations.ContainsKey(key) ? "database" : snapshot.Effective.ActivePrompts.ContainsKey(key) ? "configuration" : "latest";

    private static int VersionOrder(string version) => int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static void AddReason(Dictionary<string, string[]> errors, string? reason)
    {
        var length = reason?.Trim().Length ?? 0;
        if (length is < 5 or > 300)
        {
            errors["reason"] = [length < 5 ? "validation.too_short" : "validation.too_long"];
        }
    }

    private static IResult Invalid(Dictionary<string, string[]> errors) => Error.Validation("validation.failed", errors).ToProblem();

    private sealed class RunRow
    {
        public required EvalRun Run { get; init; }

        public required string SetName { get; init; }
    }
}

/// <summary>AI configuration changes are platform-level audit entries by the signed-in staff member.</summary>
internal sealed class AiAudit(ICurrentUser caller, IStaffDirectory staff, IClock clock)
{
    public Guid StaffId => caller.UserId;

    public async Task RecordAsync(ModuleDbContext db, string action, string entityType, string entityId, string? reason, CancellationToken ct, object? before, object? after)
    {
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), action, entityType, entityId, null, clock.UtcNow,
            before: before, after: after, reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
    }
}
