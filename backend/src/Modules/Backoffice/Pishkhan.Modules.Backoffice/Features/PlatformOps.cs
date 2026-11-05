using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Backoffice.Features;

/// <summary>Filters of the dead letter list. <c>status</c>: open (default), replayed, discarded or all.</summary>
public sealed record DeadLetterSearch(string? Group, string? Status, string? Type, Guid? WorkspaceId, int? Page, int? PageSize);

/// <summary>A message a consumer gave up on. <c>consumer</c> is the handler's type name; <c>replayMessageId</c> the id the replay went out with.</summary>
public sealed record DeadLetterResponse(
    Guid Id, Guid MessageId, string Topic, string Group, string Consumer, string Key, string Type, Guid? WorkspaceId, string ExceptionType, string ExceptionMessage,
    int Attempts, DateTimeOffset FailedAt, string Status, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? ResolutionReason, Guid? ReplayMessageId);

/// <summary>A dead letter with the envelope as it reached the DLQ (payload included).</summary>
public sealed record DeadLetterDetail(DeadLetterResponse DeadLetter, JsonElement Envelope);

/// <summary>Open dead letters per consumer group, for the page's tabs and the dashboard.</summary>
public sealed record DeadLetterGroupCount(string Group, int Open, DateTimeOffset? OldestOpenAt);

/// <summary>Replays or discards 1 to 100 open dead letters. <c>reason</c>: 5 to 300 characters, kept in the audit log.</summary>
public sealed record DeadLetterActionRequest(IReadOnlyList<Guid> Ids, string Reason);

/// <summary><c>done</c>: dead letters changed; <c>skipped</c>: ids that were unknown or no longer open.</summary>
public sealed record DeadLetterActionResponse(int Done, IReadOnlyList<Guid> Skipped);

/// <summary>A scheduler instance (a Workers replica running Quartz). <c>alive</c>: checked in within twice its interval plus 30 seconds.</summary>
public sealed record WorkerInstance(string InstanceId, DateTimeOffset LastCheckinAt, int CheckinIntervalSeconds, bool Alive);

/// <summary>A recurring job. <c>state</c> as Quartz keeps it (WAITING, ACQUIRED, BLOCKED, PAUSED, ERROR).</summary>
public sealed record RecurringJobInfo(string Name, string? Cron, string State, DateTimeOffset? NextFireAt, DateTimeOffset? PreviousFireAt);

/// <summary>Workers and their jobs. <c>groups</c>: the Kafka worker groups (consumer groups) with their member count.</summary>
public sealed record WorkersOverview(IReadOnlyList<WorkerInstance> Instances, IReadOnlyList<RecurringJobInfo> Jobs, IReadOnlyList<WorkerGroupInfo> Groups);

public sealed record WorkerGroupInfo(string Group, string State, int Members, long Lag);

public sealed record TriggerJobRequest(string Reason);

/// <summary>AdminApi: Kafka topics and lag, dead letters, workers and recurring jobs (AD-1.7, admin plan 4.10).</summary>
internal static class PlatformOpsEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const string SchedulerName = "pishkhan";
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support, StaffRoles.AiOps, StaffRoles.ReadOnly];
    private static readonly string[] PayloadReaders = [StaffRoles.SuperAdmin, StaffRoles.Support];

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().MapGroup("/platform").WithTags("Platform");
        var read = admin.MapGroup(string.Empty).RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var payload = admin.MapGroup(string.Empty).RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, PayloadReaders));
        var super = admin.MapGroup(string.Empty).RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.SuperAdmin));

        read.MapGet("/kafka", KafkaAsync)
            .WithSummary("Kafka topics, retained messages and consumer group lag")
            .WithDescription("Roles: SuperAdmin, Support, AIOps, ReadOnly. Read from the brokers, cached 15 seconds. 503 `platform.kafka_unavailable` when the brokers do not answer.")
            .Produces<KafkaOverview>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        read.MapGet("/dead-letters", ListDeadLettersAsync)
            .WithSummary("Dead letters, newest first")
            .WithDescription("Roles: SuperAdmin, Support, AIOps, ReadOnly. Without the payload.")
            .Produces<PagedResult<DeadLetterResponse>>()
            .WithStandardProblems();

        read.MapGet("/dead-letters/summary", DeadLetterSummaryAsync)
            .WithSummary("Open dead letters per consumer group")
            .Produces<IReadOnlyList<DeadLetterGroupCount>>();

        payload.MapGet("/dead-letters/{id:guid}", GetDeadLetterAsync)
            .WithSummary("A dead letter with its envelope and payload")
            .WithDescription("Roles: SuperAdmin, Support (the payload may hold customer messages).")
            .Produces<DeadLetterDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        super.MapPost("/dead-letters/replay", ReplayAsync)
            .RequireStepUp()
            .WithSummary("Replay open dead letters to the consumer that failed them")
            .WithDescription("SuperAdmin, step-up. Each goes back to its topic with a new message id, handled only by the consumer that failed it. Audited as platform.dead_letter_replay.")
            .Produces<DeadLetterActionResponse>()
            .WithStandardProblems();

        super.MapPost("/dead-letters/discard", DiscardAsync)
            .RequireStepUp()
            .WithSummary("Discard open dead letters")
            .WithDescription("SuperAdmin, step-up. The message is not processed; the row stays for the record. Audited as platform.dead_letter_discard.")
            .Produces<DeadLetterActionResponse>()
            .WithStandardProblems();

        read.MapGet("/workers", WorkersAsync)
            .WithSummary("Worker replicas, recurring jobs and worker groups")
            .WithDescription("Roles: SuperAdmin, Support, AIOps, ReadOnly. Replicas and jobs come from the Quartz cluster tables; groups from Kafka (empty when the brokers do not answer).")
            .Produces<WorkersOverview>();

        super.MapPost("/jobs/{name}/trigger", TriggerJobAsync)
            .RequireStepUp()
            .WithSummary("Run a recurring job now")
            .WithDescription("SuperAdmin, step-up. A replica picks it up within about 30 seconds; the schedule continues afterwards. 409 `platform.job_busy` unless the job is waiting. Audited as platform.job_trigger.")
            .Produces<RecurringJobInfo>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
    }

    private static async Task<IResult> KafkaAsync(KafkaInspector inspector, ILoggerFactory logs, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await inspector.GetAsync(ct));
        }
        catch (Exception ex) when (ex is Confluent.Kafka.KafkaException or TimeoutException)
        {
            logs.CreateLogger(typeof(PlatformOpsEndpoints)).LogWarning(ex, "Kafka overview failed");
            return Results.Problem(title: "platform.kafka_unavailable", statusCode: StatusCodes.Status503ServiceUnavailable, extensions: new Dictionary<string, object?> { ["code"] = "platform.kafka_unavailable" });
        }
    }

    private static async Task<IResult> ListDeadLettersAsync([AsParameters] DeadLetterSearch search, PlatformDbContext db, IStaffDirectory staff, CancellationToken ct)
    {
        var query = db.DeadLetters.AsNoTracking();
        var status = string.IsNullOrWhiteSpace(search.Status) ? DeadLetter.Open : search.Status.Trim().ToLowerInvariant();
        if (status != "all")
        {
            if (status is not (DeadLetter.Open or DeadLetter.Replayed or DeadLetter.Discarded))
            {
                return Invalid("status", "validation.invalid");
            }

            query = query.Where(d => d.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(search.Group))
        {
            query = query.Where(d => d.ConsumerGroup == search.Group.Trim());
        }

        if (!string.IsNullOrWhiteSpace(search.Type))
        {
            query = query.Where(d => d.Type == search.Type.Trim());
        }

        if (search.WorkspaceId is { } workspaceId)
        {
            query = query.Where(d => d.WorkspaceId == workspaceId);
        }

        var page = Math.Max(1, search.Page ?? 1);
        var pageSize = Math.Clamp(search.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(d => d.FailedAt).ThenByDescending(d => d.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var names = await NamesAsync(rows, staff, ct);
        return Results.Ok(new PagedResult<DeadLetterResponse>([.. rows.Select(d => ToResponse(d, names))], total, page, pageSize));
    }

    private static async Task<IResult> DeadLetterSummaryAsync(PlatformDbContext db, CancellationToken ct) =>
        Results.Ok(await SummaryAsync(db, ct));

    internal static async Task<IReadOnlyList<DeadLetterGroupCount>> SummaryAsync(PlatformDbContext db, CancellationToken ct)
    {
        var open = await db.DeadLetters.AsNoTracking()
            .Where(d => d.Status == DeadLetter.Open)
            .GroupBy(d => d.ConsumerGroup)
            .Select(g => new { Group = g.Key, Count = g.Count(), Oldest = g.Min(d => d.FailedAt) })
            .ToListAsync(ct);
        return [.. Topics.ConsumerGroups.Select(g => open.FirstOrDefault(o => o.Group == g) is { } o ? new DeadLetterGroupCount(g, o.Count, o.Oldest) : new DeadLetterGroupCount(g, 0, null))];
    }

    private static async Task<IResult> GetDeadLetterAsync(Guid id, PlatformDbContext db, IStaffDirectory staff, CancellationToken ct)
    {
        var row = await db.DeadLetters.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row is null)
        {
            return Error.NotFound().ToProblem();
        }

        var names = await NamesAsync([row], staff, ct);
        return Results.Ok(new DeadLetterDetail(ToResponse(row, names), row.Envelope.RootElement.Clone()));
    }

    private static Task<IResult> ReplayAsync(DeadLetterActionRequest request, PlatformDbContext db, IStaffDirectory staff, ICurrentUser caller, IClock clock, CancellationToken ct) =>
        ResolveAsync(request, db, staff, caller, clock, "platform.dead_letter_replay", (row, now) => DeadLetterStore.Replay(db, row, now), ct);

    private static Task<IResult> DiscardAsync(DeadLetterActionRequest request, PlatformDbContext db, IStaffDirectory staff, ICurrentUser caller, IClock clock, CancellationToken ct) =>
        ResolveAsync(request, db, staff, caller, clock, "platform.dead_letter_discard", (row, _) => row.Status = DeadLetter.Discarded, ct);

    private static async Task<IResult> ResolveAsync(
        DeadLetterActionRequest request, PlatformDbContext db, IStaffDirectory staff, ICurrentUser caller, IClock clock, string action, Action<DeadLetter, DateTimeOffset> resolve,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (ReasonError(request.Reason) is { } reasonError)
        {
            errors["reason"] = [reasonError];
        }

        var ids = (request.Ids ?? []).Distinct().ToList();
        if (ids.Count is 0 or > 100)
        {
            errors["ids"] = [ids.Count == 0 ? "validation.required" : "validation.too_many"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var now = clock.UtcNow;
        var reason = request.Reason.Trim();
        var rows = await db.DeadLetters.Where(d => ids.Contains(d.Id) && d.Status == DeadLetter.Open).ToListAsync(ct);
        foreach (var row in rows)
        {
            resolve(row, now);
            row.ResolvedAt = now;
            row.ResolvedBy = caller.UserId;
            row.ResolutionReason = reason;
        }

        if (rows.Count > 0)
        {
            var entry = AuditEntries.Create(
                AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), action, "dead_letter", rows.Count == 1 ? rows[0].Id.ToString() : null, null, now,
                after: new { count = rows.Count, items = rows.Select(r => new { r.Id, r.MessageId, r.Type, group = r.ConsumerGroup, r.Consumer }) }, reason: reason);
            db.Publish(entry, entry.PartitionKey(), workspaceId: null);
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new DeadLetterActionResponse(rows.Count, [.. ids.Except(rows.Select(r => r.Id))]));
    }

    private static async Task<IResult> WorkersAsync(NpgsqlDataSource dataSource, KafkaInspector inspector, IClock clock, ILoggerFactory logs, CancellationToken ct)
    {
        var now = clock.UtcNow;
        List<WorkerInstance> instances = [];
        List<RecurringJobInfo> jobs = [];
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            instances = (await connection.QueryAsync<(string Instance, long Checkin, long Interval)>(new CommandDefinition(
                    "SELECT instance_name, last_checkin_time, checkin_interval FROM platform.qrtz_scheduler_state WHERE sched_name = @s ORDER BY instance_name",
                    new { s = SchedulerName }, cancellationToken: ct)))
                .Select(r =>
                {
                    var at = FromTicks(r.Checkin)!.Value;
                    var interval = TimeSpan.FromMilliseconds(r.Interval);
                    return new WorkerInstance(r.Instance, at, (int)interval.TotalSeconds, now - at < interval * 2 + TimeSpan.FromSeconds(30));
                })
                .ToList();
            jobs = await JobsAsync(connection, null, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // The Migrator creates the Quartz tables; a database without them (tests, a fresh dev box) has no workers yet.
        }

        IReadOnlyList<WorkerGroupInfo> groups = [];
        try
        {
            var kafka = await inspector.GetAsync(ct);
            groups = [.. kafka.Groups.Select(g => new WorkerGroupInfo(g.Group, g.State, g.Members, g.Lag))];
        }
        catch (Exception ex) when (ex is Confluent.Kafka.KafkaException or TimeoutException)
        {
            logs.CreateLogger(typeof(PlatformOpsEndpoints)).LogWarning(ex, "Kafka groups unavailable for the workers page");
        }

        return Results.Ok(new WorkersOverview(instances, jobs, groups));
    }

    private static async Task<IResult> TriggerJobAsync(
        string name, TriggerJobRequest request, NpgsqlDataSource dataSource, PlatformDbContext db, IStaffDirectory staff, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (ReasonError(request.Reason) is { } reasonError)
        {
            return Invalid("reason", reasonError);
        }

        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        List<RecurringJobInfo> existing;
        try
        {
            existing = await JobsAsync(connection, name, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            existing = [];
        }

        if (existing.Count == 0)
        {
            return Error.NotFound().ToProblem();
        }

        // Pulling the cron trigger's next fire time to now makes the cluster fire it at its next poll; the cron then schedules the following run.
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE platform.qrtz_triggers SET next_fire_time = @t WHERE sched_name = @s AND job_name = @n AND trigger_state = 'WAITING'",
            new { t = now.UtcTicks, s = SchedulerName, n = name }, cancellationToken: ct));
        if (changed == 0)
        {
            return Error.Conflict("platform.job_busy").ToProblem();
        }

        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), "platform.job_trigger", "job", name, null, now,
            before: new { nextFireAt = existing[0].NextFireAt }, after: new { nextFireAt = now }, reason: request.Reason.Trim());
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
        await db.SaveChangesAsync(ct);
        return Results.Ok((await JobsAsync(connection, name, ct))[0]);
    }

    private static async Task<List<RecurringJobInfo>> JobsAsync(NpgsqlConnection connection, string? name, CancellationToken ct) =>
        (await connection.QueryAsync<(string Job, string? Cron, string State, long? Next, long? Previous)>(new CommandDefinition(
            """
            SELECT t.job_name, c.cron_expression, t.trigger_state, t.next_fire_time, t.prev_fire_time
            FROM platform.qrtz_triggers t
            LEFT JOIN platform.qrtz_cron_triggers c ON c.sched_name = t.sched_name AND c.trigger_name = t.trigger_name AND c.trigger_group = t.trigger_group
            WHERE t.sched_name = @s AND (@n::text IS NULL OR t.job_name = @n)
            ORDER BY t.job_name
            """,
            new { s = SchedulerName, n = name }, cancellationToken: ct)))
        .Select(r => new RecurringJobInfo(r.Job, r.Cron, r.State, FromTicks(r.Next), FromTicks(r.Previous)))
        .ToList();

    /// <summary>Quartz.NET keeps instants as UTC ticks; 0 or negative means none.</summary>
    private static DateTimeOffset? FromTicks(long? ticks) => ticks is > 0 ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null;

    private static async Task<Dictionary<Guid, string?>> NamesAsync(IEnumerable<DeadLetter> rows, IStaffDirectory staff, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string?>();
        foreach (var id in rows.Where(r => r.ResolvedBy is not null).Select(r => r.ResolvedBy!.Value).Distinct())
        {
            names[id] = await staff.GetUsernameAsync(id, ct);
        }

        return names;
    }

    private static DeadLetterResponse ToResponse(DeadLetter d, Dictionary<Guid, string?> names) =>
        new(d.Id, d.MessageId, d.Topic, d.ConsumerGroup, d.Consumer, d.Key, d.Type, d.WorkspaceId, d.ExceptionType, d.ExceptionMessage, d.Attempts, d.FailedAt, d.Status,
            d.ResolvedAt, d.ResolvedBy is { } by ? names.GetValueOrDefault(by) : null, d.ResolutionReason, d.ReplayMessageId);

    internal static string? ReasonError(string? reason) => reason?.Trim().Length switch
    {
        null or < 5 => "validation.too_short",
        > 300 => "validation.too_long",
        _ => null,
    };

    internal static IResult Invalid(string field, string code) =>
        Error.Validation("validation.failed", new Dictionary<string, string[]> { [field] = [code] }).ToProblem();
}
