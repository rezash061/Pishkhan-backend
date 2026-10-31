using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Modules.Tenancy.Domain;
using Pishkhan.Modules.Tenancy.Persistence;
using Pishkhan.SharedKernel;
using Quartz;

namespace Pishkhan.Modules.Tenancy.Services;

/// <summary>
/// Daily (platform group): workspaces whose grace period is over become <c>deleted</c> and <see cref="WorkspaceDeleted"/> is published,
/// in one transaction. The modules then delete the data.
/// </summary>
[DisallowConcurrentExecution]
public sealed class WorkspacePurgeJob(TenancyDbContext db, ICacheInvalidator cache, IClock clock, ILogger<WorkspacePurgeJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context) => await RunAsync(context.CancellationToken);

    /// <summary>Returns the number of workspaces marked deleted.</summary>
    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var dueBefore = now - Workspace.DeletionGracePeriod;
        var count = 0;
        while (true)
        {
            db.ChangeTracker.Clear();
            var due = await db.Workspaces
                .Where(w => w.Status == WorkspaceStatus.PendingDeletion && w.DeletionRequestedAt <= dueBefore)
                .OrderBy(w => w.DeletionRequestedAt)
                .Take(50)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
            {
                return count;
            }

            foreach (var workspace in due)
            {
                var requestedAt = workspace.DeletionRequestedAt!.Value;
                workspace.MarkDeleted(now);
                db.Publish(new WorkspaceDeleted(workspace.Id, requestedAt), key: workspace.Id.ToString(), workspaceId: workspace.Id);
                var entry = AuditEntries.Create(AuditActor.System, "workspace.purge", "workspace", workspace.Id.ToString(), workspace.Id, now, after: new { requestedAt });
                db.Publish(entry, entry.PartitionKey(), workspaceId: workspace.Id);
                cache.Enqueue(db, CacheTags.Workspace(workspace.Id));
                logger.LogInformation("Workspace {WorkspaceId} deleted after its grace period (requested {RequestedAt})", workspace.Id, requestedAt);
            }

            await db.SaveChangesAsync(cancellationToken);
            count += due.Count;
        }
    }
}

/// <summary>
/// Platform group: deletes every row of the workspace in the module schemas (any table with a <c>workspace_id</c> column), in one
/// transaction. Kept: the audit log, the <c>tenancy.workspaces</c> row (a tombstone without data), and platform bookkeeping. Files
/// and their stored objects are deleted by the Files module. Tables are retried in passes, so foreign keys need no fixed order.
/// Running it twice deletes nothing more, so redelivery is safe.
/// </summary>
internal sealed class WorkspaceDataPurgeHandler(NpgsqlDataSource dataSource, ILogger<WorkspaceDataPurgeHandler> logger) : IIntegrationEventHandler<WorkspaceDeleted>
{
    /// <summary>Schemas never purged here: kept (audit, identity, backoffice), bookkeeping (platform), or owned by a module that cleans up itself (files).</summary>
    internal static readonly string[] KeptSchemas = ["audit", "identity", "backoffice", "platform", "files", "public", "information_schema"];

    private const string TablesSql = """
        SELECT n.nspname, c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'workspace_id' AND NOT a.attisdropped
        WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
          AND n.nspname NOT LIKE 'pg\_%' AND NOT (n.nspname = ANY(@kept))
        ORDER BY 1, 2
        """;

    public async Task HandleAsync(WorkspaceDeleted message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var pending = new List<string>();
        await using (var list = new NpgsqlCommand(TablesSql, connection, transaction))
        {
            list.Parameters.AddWithValue("kept", KeptSchemas);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add($"\"{reader.GetString(0)}\".\"{reader.GetString(1)}\"");
            }
        }

        var deleted = 0;
        while (pending.Count > 0)
        {
            var blocked = new List<string>();
            foreach (var table in pending)
            {
                await transaction.SaveAsync("purge", cancellationToken);
                try
                {
                    await using var delete = new NpgsqlCommand($"DELETE FROM {table} WHERE workspace_id = @ws", connection, transaction);
                    delete.Parameters.AddWithValue("ws", message.WorkspaceId);
                    deleted += await delete.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.ReleaseAsync("purge", cancellationToken);
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
                {
                    await transaction.RollbackAsync("purge", cancellationToken);
                    blocked.Add(table);
                }
            }

            if (blocked.Count == pending.Count)
            {
                throw new InvalidOperationException($"Workspace {message.WorkspaceId}: rows still referenced from outside the workspace in {string.Join(", ", blocked)}.");
            }

            pending = blocked;
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Purged {Rows} rows of workspace {WorkspaceId}", deleted, message.WorkspaceId);
    }
}
