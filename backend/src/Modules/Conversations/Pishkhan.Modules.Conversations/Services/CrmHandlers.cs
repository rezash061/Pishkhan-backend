using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Features;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Services;

/// <summary>Engine group: stores a completed flow run's answers as a form submission of the conversation's contact.</summary>
internal sealed class FormSubmittedHandler(ConversationsDbContext db, IClock clock) : IdempotentHandler<FormSubmitted, ConversationsDbContext>(db)
{
    protected override async Task HandleOnceAsync(FormSubmitted message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var contactId = await Db.Conversations.Where(c => c.Id == message.ConversationId).Select(c => (Guid?)c.ContactId).FirstOrDefaultAsync(cancellationToken);
        if (contactId is null || await Db.FormSubmissions.AnyAsync(s => s.RunId == message.RunId, cancellationToken))
        {
            return;
        }

        Db.FormSubmissions.Add(FormSubmission.Create(
            message.WorkspaceId, message.FlowId, message.FlowVersion, message.FlowName.Length <= 200 ? message.FlowName : message.FlowName[..200], message.RunId,
            message.ConversationId, contactId.Value, message.Answers, message.SubmittedAt, clock.UtcNow));
    }
}

/// <summary>
/// Platform group: writes the contacts matching an export's filters to an Excel file in the exports bucket (newest last, up to
/// <see cref="CrmRecords.MaxExportRows"/>), then notifies the member who asked. Dates are Jalali; custom fields get a column each.
/// </summary>
internal sealed class ContactExportHandler(ConversationsDbContext db, IObjectStorage storage, IClock clock, ILogger<ContactExportHandler> logger)
    : IdempotentHandler<ContactExportRequested, ConversationsDbContext>(db)
{
    private const int Batch = 2_000;

    public const string NotificationKind = "crm_export";

    protected override async Task HandleOnceAsync(ContactExportRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var export = await Db.Exports.FirstOrDefaultAsync(e => e.Id == message.ExportId, cancellationToken);
        if (export is null || export.Status != ExportStatuses.Pending)
        {
            return;
        }

        var now = clock.UtcNow;
        var filter = JsonSerializer.Deserialize<ContactFilter>(export.FilterJson, EnvelopeJson.Options) ?? new ContactFilter(null, null, null, null, null, null);
        var (query, error) = await CrmEndpoints.FilterAsync(Db, export.WorkspaceId, filter, now, cancellationToken);
        if (error is not null || query is null)
        {
            Finish(export, ExportStatuses.Failed, null, "crm.export.filter_invalid", now);
            return;
        }

        var fields = await Db.ContactFields.AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Key).ToListAsync(cancellationToken);
        using var workbook = new XLWorkbook { RightToLeft = true };
        var sheet = workbook.Worksheets.Add("مخاطبین");
        string[] fixedColumns = ["شناسه", "نام", "نام کاربری", "تلفن", "ایمیل", "برچسب‌ها", "تاریخ ایجاد", "آخرین پیام"];
        var headers = fixedColumns.Concat(fields.Select(f => f.Label)).ToList();
        for (var i = 0; i < headers.Count; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        sheet.Row(1).Style.Font.Bold = true;
        var row = 1;
        DateTimeOffset? afterAt = null;
        Guid afterId = default;
        while (row - 1 < CrmRecords.MaxExportRows)
        {
            var page = query;
            if (afterAt is { } at)
            {
                var id = afterId;
                page = page.Where(c => EF.Functions.GreaterThan(ValueTuple.Create(c.CreatedAt, c.Id), ValueTuple.Create(at, id)));
            }

            var contacts = await page.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(Math.Min(Batch, CrmRecords.MaxExportRows - (row - 1))).ToListAsync(cancellationToken);
            if (contacts.Count == 0)
            {
                break;
            }

            var ids = contacts.Select(c => c.Id).ToList();
            var seen = await Db.Conversations.AsNoTracking().Where(c => ids.Contains(c.ContactId))
                .GroupBy(c => c.ContactId).Select(g => new { g.Key, At = g.Max(c => c.LastInboundAt) }).ToDictionaryAsync(x => x.Key, x => x.At, cancellationToken);
            foreach (var c in contacts)
            {
                row++;
                sheet.Cell(row, 1).Value = c.Id.ToString();
                sheet.Cell(row, 2).Value = c.DisplayName ?? string.Empty;
                sheet.Cell(row, 3).Value = c.Username ?? string.Empty;
                sheet.Cell(row, 4).Value = c.Fields.GetValueOrDefault("phone") ?? string.Empty;
                sheet.Cell(row, 5).Value = c.Fields.GetValueOrDefault("email") ?? string.Empty;
                sheet.Cell(row, 6).Value = string.Join("، ", c.Tags);
                sheet.Cell(row, 7).Value = Jalali.FromUtc(c.CreatedAt).ToString();
                sheet.Cell(row, 8).Value = seen.GetValueOrDefault(c.Id) is { } last ? Jalali.FromUtc(last).ToString() : string.Empty;
                for (var f = 0; f < fields.Count; f++)
                {
                    sheet.Cell(row, fixedColumns.Length + f + 1).Value = c.Fields.GetValueOrDefault(fields[f].Key) ?? string.Empty;
                }
            }

            afterAt = contacts[^1].CreatedAt;
            afterId = contacts[^1].Id;
            if (contacts.Count < Batch)
            {
                break;
            }
        }

        // Phones and ids stay text, so Excel keeps leading zeros and digits exactly.
        sheet.Columns(1, headers.Count).Style.NumberFormat.Format = "@";
        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        buffer.Position = 0;
        var key = $"{export.WorkspaceId:N}/contacts/{export.Id:N}.xlsx";
        await storage.PutAsync(Buckets.Exports, key, buffer, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", cancellationToken);
        export.StorageKey = key;
        Finish(export, ExportStatuses.Ready, row - 1, null, now);
        logger.LogInformation("Contacts export {ExportId}: {Rows} row(s)", export.Id, row - 1);
    }

    private void Finish(ContactExport export, string status, int? rows, string? error, DateTimeOffset now)
    {
        export.Status = status;
        export.RowCount = rows;
        export.Error = error;
        export.CompletedAt = now;
        export.ExpiresAt = status == ExportStatuses.Ready ? now + ContactExport.Lifetime : null;
        var (title, body) = status == ExportStatuses.Ready
            ? ("خروجی مخاطبین آماده است", $"{PersianDigits.ToPersian(rows!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))} مخاطب؛ تا ۲۴ ساعت یک بار قابل دریافت است.")
            : ("خروجی مخاطبین ساخته نشد", "فیلترها دیگر معتبر نیستند؛ دوباره تلاش کنید.");
        Db.Publish(
            new NotificationRequested(export.WorkspaceId, export.RequestedBy, NotificationKind, title, body, $"/contacts?export={export.Id}"),
            export.RequestedBy.ToString(), workspaceId: export.WorkspaceId);
    }
}
