using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Conversations.Features;

/// <summary>A flow that collected answers: its latest name, how many runs completed it and the answer keys seen.</summary>
public sealed record FormResponse(Guid FlowId, string FlowName, int SubmissionCount, DateTimeOffset LastSubmittedAt, IReadOnlyList<string> Fields);

public sealed record FormSubmissionResponse(
    Guid Id, Guid FlowId, string FlowName, int FlowVersion, Guid ContactId, string? ContactName, string? ContactUsername, Guid ConversationId,
    IReadOnlyDictionary<string, string> Answers, DateTimeOffset SubmittedAt);

/// <summary>Records a consent decision. <c>evidence</c> says what the customer agreed to or how (up to 500 characters).</summary>
public sealed record ConsentRequest(string Channel, bool Granted, string? Evidence);

public sealed record ConsentResponse(Guid Id, string Channel, bool Granted, string Source, string? Evidence, Guid? RecordedBy, DateTimeOffset CreatedAt);

/// <summary>The decision in force per channel (the latest), and the full history newest first.</summary>
public sealed record ContactConsents(IReadOnlyDictionary<string, bool> Current, IReadOnlyList<ConsentResponse> History);

/// <summary>Asks for an Excel file of the contacts matching the list filters. The member who asks is notified when it is ready.</summary>
public sealed record ExportRequest(string? Q, string? Tag, Guid? SegmentId, Guid? ChannelAccountId, bool? HasPhone, string? Consent);

/// <summary>
/// <c>status</c>: pending, ready, failed, downloaded (the one download was used) or expired (not downloaded within a day).
/// </summary>
public sealed record ExportResponse(Guid Id, string Status, int? RowCount, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt);

internal static class CrmRecords
{
    public const int MaxExportRows = 200_000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var contacts = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/contacts").WithTags("Contacts");

        contacts.MapGet("/{contactId:guid}/submissions", ContactSubmissionsAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("The contact's form submissions, newest first")
            .Produces<IReadOnlyList<FormSubmissionResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        contacts.MapGet("/{contactId:guid}/consents", ConsentsAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("The contact's consent per channel and its history")
            .Produces<ContactConsents>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        contacts.MapPost("/{contactId:guid}/consents", RecordConsentAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithSummary("Record that the contact granted or withdrew consent for a channel")
            .Produces<ContactConsents>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        contacts.MapPost("/exports", StartExportAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("Export the contacts matching the filters to Excel, in the background")
            .Produces<ExportResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        contacts.MapGet("/exports", ListExportsAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("The caller's exports, newest first")
            .Produces<IReadOnlyList<ExportResponse>>();

        contacts.MapGet("/exports/{exportId:guid}", GetExportAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .Produces<ExportResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        contacts.MapPost("/exports/{exportId:guid}/download", DownloadAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("A download URL for a ready export, valid 5 minutes and issued once (409 crm.export.not_ready or crm.export.gone otherwise)")
            .Produces<ExportDownload>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var crm = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/crm").WithTags("Contacts");

        crm.MapGet("/forms", ListFormsAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("Flows that collected answers, most recently submitted first")
            .Produces<IReadOnlyList<FormResponse>>();

        crm.MapGet("/forms/{flowId:guid}/submissions", SubmissionsAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("A flow's submissions, newest first")
            .Produces<Page<FormSubmissionResponse>>();
    }

    // ---- Forms ----

    private static async Task<IResult> ListFormsAsync(Guid workspaceId, ConversationsDbContext db, CancellationToken ct)
    {
        var groups = await db.FormSubmissions.AsNoTracking().GroupBy(s => s.FlowId)
            .Select(g => new { FlowId = g.Key, Count = g.Count(), Last = g.Max(s => s.SubmittedAt) })
            .OrderByDescending(g => g.Last).Take(200).ToListAsync(ct);
        var result = new List<FormResponse>(groups.Count);
        foreach (var g in groups)
        {
            // The latest submissions name the flow and its fields; older versions may have asked other questions.
            var recent = await db.FormSubmissions.AsNoTracking().Where(s => s.FlowId == g.FlowId).OrderByDescending(s => s.SubmittedAt).Take(50).ToListAsync(ct);
            var fields = recent.SelectMany(s => s.Answers.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            result.Add(new FormResponse(g.FlowId, recent[0].FlowName, g.Count, g.Last, fields));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> SubmissionsAsync(Guid workspaceId, Guid flowId, string? cursor, int? limit, ConversationsDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.FormSubmissions.AsNoTracking().Where(s => s.FlowId == flowId);
        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(s => EF.Functions.LessThan(ValueTuple.Create(s.SubmittedAt, s.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(s => s.SubmittedAt).ThenByDescending(s => s.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].SubmittedAt, rows[take - 1].Id) : null;
        return Results.Ok(new Page<FormSubmissionResponse>(await ToSubmissionsAsync(db, rows.Take(take).ToList(), ct), next));
    }

    private static async Task<IResult> ContactSubmissionsAsync(Guid workspaceId, Guid contactId, ConversationsDbContext db, CancellationToken ct)
    {
        if (!await db.Contacts.AnyAsync(c => c.Id == contactId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        var rows = await db.FormSubmissions.AsNoTracking().Where(s => s.ContactId == contactId).OrderByDescending(s => s.SubmittedAt).Take(100).ToListAsync(ct);
        return Results.Ok(await ToSubmissionsAsync(db, rows, ct));
    }

    private static async Task<List<FormSubmissionResponse>> ToSubmissionsAsync(ConversationsDbContext db, List<FormSubmission> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.ContactId).Distinct().ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.DisplayName, c.Username }).ToDictionaryAsync(c => c.Id, ct);
        return [.. rows.Select(r => new FormSubmissionResponse(
            r.Id, r.FlowId, r.FlowName, r.FlowVersion, r.ContactId, contacts.GetValueOrDefault(r.ContactId)?.DisplayName, contacts.GetValueOrDefault(r.ContactId)?.Username,
            r.ConversationId, r.Answers, r.SubmittedAt))];
    }

    // ---- Consents ----

    private static async Task<ContactConsents> ReadConsentsAsync(ConversationsDbContext db, Guid contactId, CancellationToken ct)
    {
        var history = await db.Consents.AsNoTracking().Where(k => k.ContactId == contactId).OrderByDescending(k => k.CreatedAt).ThenByDescending(k => k.Id).Take(200).ToListAsync(ct);
        var current = history.GroupBy(k => k.Channel).ToDictionary(g => g.Key, g => g.First().Granted, StringComparer.Ordinal);
        return new ContactConsents(current, [.. history.Select(k => new ConsentResponse(k.Id, k.Channel, k.Granted, k.Source, k.Evidence, k.RecordedBy, k.CreatedAt))]);
    }

    private static async Task<IResult> ConsentsAsync(Guid workspaceId, Guid contactId, ConversationsDbContext db, CancellationToken ct) =>
        await db.Contacts.AnyAsync(c => c.Id == contactId, ct) ? Results.Ok(await ReadConsentsAsync(db, contactId, ct)) : Error.NotFound().ToProblem();

    private static async Task<IResult> RecordConsentAsync(
        Guid workspaceId, Guid contactId, ConsentRequest request, ConversationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!ConsentChannels.All.Contains(request.Channel ?? string.Empty))
        {
            errors["channel"] = ["validation.consent.channel"];
        }

        if (request.Evidence is { Length: > 500 })
        {
            errors["evidence"] = ["validation.too_long"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (!await db.Contacts.AnyAsync(c => c.Id == contactId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        var evidence = string.IsNullOrWhiteSpace(request.Evidence) ? null : request.Evidence.Trim();
        db.Consents.Add(ContactConsent.Create(workspaceId, contactId, request.Channel!, request.Granted, "manual", evidence, caller.UserId, clock.UtcNow));
        await db.SaveChangesAsync(ct);
        return Results.Json(await ReadConsentsAsync(db, contactId, ct), statusCode: StatusCodes.Status201Created);
    }

    // ---- Exports ----

    private static ExportResponse ToExport(ContactExport e, DateTimeOffset now) =>
        new(e.Id, e.StatusAt(now), e.RowCount, e.Error, e.CreatedAt, e.CompletedAt, e.ExpiresAt);

    private static async Task<IResult> StartExportAsync(
        Guid workspaceId, ExportRequest request, ConversationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var filter = new ContactFilter(request.Q, request.Tag, request.SegmentId, request.ChannelAccountId, request.HasPhone, request.Consent);
        if ((await CrmEndpoints.FilterAsync(db, workspaceId, filter, now, ct)).Error is { } invalid)
        {
            return invalid;
        }

        if (await db.Exports.CountAsync(e => e.RequestedBy == caller.UserId && e.Status == ExportStatuses.Pending, ct) >= 3)
        {
            return Error.Conflict("crm.export.too_many").ToProblem();
        }

        var export = ContactExport.Create(workspaceId, caller.UserId, JsonSerializer.Serialize(filter, EnvelopeJson.Options), now);
        db.Exports.Add(export);
        db.Publish(new ContactExportRequested(export.Id, workspaceId), export.Id.ToString(), workspaceId: workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToExport(export, now), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ListExportsAsync(Guid workspaceId, ConversationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct) =>
        Results.Ok((await db.Exports.AsNoTracking().Where(e => e.RequestedBy == caller.UserId).OrderByDescending(e => e.CreatedAt).Take(20).ToListAsync(ct))
            .Select(e => ToExport(e, clock.UtcNow)).ToList());

    private static async Task<IResult> GetExportAsync(Guid workspaceId, Guid exportId, ConversationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var export = await db.Exports.AsNoTracking().FirstOrDefaultAsync(e => e.Id == exportId && e.RequestedBy == caller.UserId, ct);
        return export is null ? Error.NotFound().ToProblem() : Results.Ok(ToExport(export, clock.UtcNow));
    }

    private static async Task<IResult> DownloadAsync(
        Guid workspaceId, Guid exportId, ConversationsDbContext db, ICurrentUser caller, IObjectStorage storage, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var export = await db.Exports.AsNoTracking().FirstOrDefaultAsync(e => e.Id == exportId && e.RequestedBy == caller.UserId, ct);
        if (export is null)
        {
            return Error.NotFound().ToProblem();
        }

        switch (export.StatusAt(now))
        {
            case ExportStatuses.Pending or ExportStatuses.Failed:
                return Error.Conflict("crm.export.not_ready").ToProblem();
            case ExportStatuses.Downloaded or ExportStatuses.Expired:
                return Error.Conflict("crm.export.gone").ToProblem();
        }

        // One download: only the request that flips ready to downloaded gets a URL.
        var claimed = await db.Exports.Where(e => e.Id == exportId && e.Status == ExportStatuses.Ready)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, ExportStatuses.Downloaded).SetProperty(e => e.DownloadedAt, now).SetProperty(e => e.UpdatedAt, now), ct);
        if (claimed == 0)
        {
            return Error.Conflict("crm.export.gone").ToProblem();
        }

        var name = $"contacts-{Pishkhan.Persian.Jalali.FromUtc(export.CreatedAt).ToString().Replace('/', '-')}.xlsx";
        return Results.Ok(new ExportDownload(storage.GetSignedUrl(Buckets.Exports, export.StorageKey!, TimeSpan.FromMinutes(5), name)));
    }
}

public sealed record ExportDownload(Uri Url);
