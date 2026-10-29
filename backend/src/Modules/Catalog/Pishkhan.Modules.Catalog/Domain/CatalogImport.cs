using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Catalog.Domain;

public static class ImportStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>A problem with one row of an import sheet (1-based, the header is row 1).</summary>
public sealed record ImportRowError(int Row, string Column, string Code);

/// <summary>One Excel import: queued by the endpoint, run by the commerce group. Rows with errors are skipped; the rest are applied.</summary>
public sealed class CatalogImport : AggregateRoot<Guid>, ITenantOwned
{
    public const int MaxErrors = 200;

    private CatalogImport()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid FileId { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public string Status { get; private set; } = ImportStatuses.Queued;

    public int Rows { get; private set; }

    public int Created { get; private set; }

    public int Updated { get; private set; }

    public int Skipped { get; private set; }

    public List<ImportRowError> Errors { get; private set; } = [];

    /// <summary>Why the whole import failed (unreadable sheet, missing headers).</summary>
    public string? Error { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static CatalogImport Queue(Guid workspaceId, Guid fileId, Guid? requestedBy, DateTimeOffset now) =>
        new() { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, FileId = fileId, RequestedBy = requestedBy, CreatedAt = now, UpdatedAt = now };

    public bool Start(DateTimeOffset now)
    {
        if (Status != ImportStatuses.Queued)
        {
            return false;
        }

        Status = ImportStatuses.Running;
        UpdatedAt = now;
        return true;
    }

    public void Complete(int rows, int created, int updated, int skipped, IEnumerable<ImportRowError> errors, DateTimeOffset now)
    {
        Status = ImportStatuses.Completed;
        Rows = rows;
        Created = created;
        Updated = updated;
        Skipped = skipped;
        Errors = [.. errors.Take(MaxErrors)];
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = ImportStatuses.Failed;
        Error = error;
        CompletedAt = now;
        UpdatedAt = now;
    }
}
