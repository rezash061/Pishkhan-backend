using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Files.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;

namespace Pishkhan.Modules.Files.Services;

/// <summary>
/// Media group: a deleted workspace's stored objects (original, quarantine copy, thumbnail) and file rows are deleted, a batch at a
/// time. Objects go first, so a crash leaves rows to retry from; deleting a missing object is not an error.
/// </summary>
internal sealed class WorkspaceFilesPurgeHandler(FilesDbContext db, IObjectStorage storage, ILogger<WorkspaceFilesPurgeHandler> logger)
    : IIntegrationEventHandler<WorkspaceDeleted>
{
    private const int BatchSize = 200;

    public async Task HandleAsync(WorkspaceDeleted message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var deleted = 0;
        while (true)
        {
            db.ChangeTracker.Clear();
            var batch = await db.Files.Where(f => f.WorkspaceId == message.WorkspaceId).OrderBy(f => f.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var file in batch)
            {
                await storage.DeleteAsync(Buckets.Quarantine, file.StorageKey, cancellationToken);
                await storage.DeleteAsync(Buckets.Files, file.StorageKey, cancellationToken);
                if (file.Derived.ThumbnailKey is { } thumbnail)
                {
                    await storage.DeleteAsync(Buckets.Files, thumbnail, cancellationToken);
                }
            }

            db.Files.RemoveRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            deleted += batch.Count;
        }

        logger.LogInformation("Deleted {Files} files of workspace {WorkspaceId}", deleted, message.WorkspaceId);
    }
}
