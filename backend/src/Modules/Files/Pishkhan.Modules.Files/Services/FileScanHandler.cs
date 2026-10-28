using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Antivirus;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Infrastructure.Timers;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Files.Domain;
using Pishkhan.Modules.Files.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Files.Services;

/// <summary>
/// Scans a quarantined file (media group, backend plan 8.1 step 2). Fail closed: only a clean verdict plus a successful
/// sanitization makes a file usable. ClamAV unreachable: the file stays pending and is re-queued in a minute.
/// Scan errors: retried by the consumer; after 5 attempts the file is <c>scan_failed</c>.
/// </summary>
internal sealed class FileScanHandler(FilesDbContext db, IObjectStorage storage, IAntivirusScanner scanner, IClock clock, ILogger<FileScanHandler> logger)
    : IIntegrationEventHandler<FileScanRequested>
{
    public const string RescanTimerKind = "files.rescan";
    public const int MaxAttempts = 5;

    public async Task HandleAsync(FileScanRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == message.FileId, cancellationToken);
        if (file is null || file.Status != FileStatuses.PendingScan)
        {
            return; // already processed (redelivery) or deleted
        }

        var now = clock.UtcNow;
        var engine = await scanner.GetVersionAsync(cancellationToken);
        if (engine is null)
        {
            logger.LogWarning("ClamAV unreachable; file {FileId} stays pending_scan", file.Id);
            TimerScheduler.Schedule(db, RescanTimerKind, now.AddMinutes(1), new FileScanRequested(file.Id, file.WorkspaceId), dedupeKey: $"files.rescan:{file.Id}");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // Same bytes already scanned with the current signatures: reuse the verdict.
        var previous = await db.Files.AsNoTracking()
            .IgnoreQueryFilters([Infrastructure.Persistence.ModuleDbContext.TenantFilter])
            .Where(f => f.Sha256 == file.Sha256 && f.Id != file.Id && (f.Status == FileStatuses.Clean || f.Status == FileStatuses.Infected))
            .OrderByDescending(f => f.ScannedAt)
            .Select(f => new { f.Status, f.ScanEngineVersion, f.Signature })
            .FirstOrDefaultAsync(cancellationToken);
        ScanResult result;
        if (previous?.Status == FileStatuses.Infected)
        {
            result = new ScanResult(ScanVerdict.Infected, previous.Signature, previous.ScanEngineVersion ?? engine);
        }
        else if (previous?.Status == FileStatuses.Clean && previous.ScanEngineVersion == engine)
        {
            result = new ScanResult(ScanVerdict.Clean, null, engine);
        }
        else
        {
            await using var content = await storage.OpenReadAsync(Buckets.Quarantine, file.StorageKey, cancellationToken);
            result = await scanner.ScanAsync(content, cancellationToken);
        }

        switch (result.Verdict)
        {
            case ScanVerdict.Infected:
                file.MarkInfected(result.EngineVersion, result.Signature, now);
                logger.LogWarning("File {FileId} is infected ({Signature}); kept in quarantine", file.Id, result.Signature);
                break;
            case ScanVerdict.Error:
                if (file.RecordScanAttempt() < MaxAttempts)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    throw new InvalidOperationException("Scan error: " + result.Error);
                }

                file.MarkScanFailed(result.Error, now);
                break;
            default:
                await PromoteCleanAsync(file, result.EngineVersion, cancellationToken);
                break;
        }

        db.Publish(new FileScanCompleted(file.Id, file.WorkspaceId, file.Purpose, file.Status, file.ContentTypeDetected, file.Size), key: file.Id.ToString(), workspaceId: file.WorkspaceId);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PromoteCleanAsync(StoredFile file, string engine, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (FilePolicy.IsImage(file.ContentTypeDetected))
        {
            await using var content = await storage.OpenReadAsync(Buckets.Quarantine, file.StorageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            var output = ImageSanitizer.Sanitize(buffer, file.ContentTypeDetected);
            if (output is null)
            {
                file.MarkRejected("file.image.undecodable", now);
                return;
            }

            await using (output.Image)
            await using (output.Thumbnail)
            {
                await storage.PutAsync(Buckets.Files, file.StorageKey, output.Image, output.ContentType, cancellationToken);
                var thumbKey = file.StorageKey + "_thumb.webp";
                await storage.PutAsync(Buckets.Files, thumbKey, output.Thumbnail, "image/webp", cancellationToken);
                file.MarkClean(engine, Buckets.Files, new FileDerivatives(thumbKey, output.Width, output.Height), now);
            }

            return;
        }

        if (file.ContentTypeDetected.Contains("openxmlformats", StringComparison.Ordinal))
        {
            await using var content = await storage.OpenReadAsync(Buckets.Quarantine, file.StorageKey, cancellationToken);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            if (OfficeInspector.Check(buffer, file.ContentTypeDetected) is { } reason)
            {
                file.MarkRejected(reason, now);
                return;
            }
        }

        await storage.CopyAsync(Buckets.Quarantine, file.StorageKey, Buckets.Files, file.StorageKey, cancellationToken);
        file.MarkClean(engine, Buckets.Files, new FileDerivatives(null, null, null), now);
    }
}

/// <summary>Re-queues files whose scan was postponed because ClamAV was down.</summary>
internal sealed class FileRescanTimerHandler(FilesDbContext db) : IdempotentHandler<TimerFired, FilesDbContext>(db)
{
    protected override Task HandleOnceAsync(TimerFired message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (message.Kind == FileScanHandler.RescanTimerKind && message.WorkspaceId is { } workspaceId)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<FileScanRequested>(message.Payload, EnvelopeJson.Options)!;
            Db.Publish(request, key: request.FileId.ToString(), workspaceId: workspaceId);
        }

        return Task.CompletedTask;
    }
}
