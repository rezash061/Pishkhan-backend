using Pishkhan.Modules.Files.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Files.Domain;

/// <summary>
/// A file that entered the system. It is usable only when <see cref="Status"/> is clean (fail closed, backend plan 8.1).
/// Storage key is <c>{workspaceId}/{fileId}</c>; the original name is metadata only.
/// </summary>
public sealed class StoredFile : AggregateRoot<Guid>, ITenantOwned
{
    private StoredFile()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Purpose { get; private set; } = string.Empty;

    public string? OriginalName { get; private set; }

    public string? ContentTypeDeclared { get; private set; }

    public string ContentTypeDetected { get; private set; } = string.Empty;

    public long Size { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public string Bucket { get; private set; } = string.Empty;

    public string StorageKey { get; private set; } = string.Empty;

    public string Status { get; private set; } = FileStatuses.PendingScan;

    public string? ScanEngineVersion { get; private set; }

    public string? Signature { get; private set; }

    public DateTimeOffset? ScannedAt { get; private set; }

    public int ScanAttempts { get; private set; }

    public string? RejectReason { get; private set; }

    public Guid? UploadedBy { get; private set; }

    public FileDerivatives Derived { get; private set; } = new(null, null, null);

    public static StoredFile Receive(Guid id, Guid workspaceId, string purpose, string? originalName, string? declared, string detected, long size, string sha256, string bucket, Guid? uploadedBy, DateTimeOffset now) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        Purpose = purpose,
        OriginalName = originalName,
        ContentTypeDeclared = declared,
        ContentTypeDetected = detected,
        Size = size,
        Sha256 = sha256,
        Bucket = bucket,
        StorageKey = $"{workspaceId:N}/{id:N}",
        Status = FileStatuses.PendingScan,
        UploadedBy = uploadedBy,
        CreatedAt = now,
    };

    public void MarkClean(string engineVersion, string bucket, FileDerivatives derived, DateTimeOffset now)
    {
        Status = FileStatuses.Clean;
        ScanEngineVersion = engineVersion;
        Bucket = bucket;
        Derived = derived;
        ScannedAt = now;
    }

    public void MarkInfected(string engineVersion, string? signature, DateTimeOffset now)
    {
        Status = FileStatuses.Infected;
        ScanEngineVersion = engineVersion;
        Signature = signature;
        ScannedAt = now;
    }

    public void MarkRejected(string reason, DateTimeOffset now)
    {
        Status = FileStatuses.Rejected;
        RejectReason = reason;
        ScannedAt = now;
    }

    public int RecordScanAttempt() => ++ScanAttempts;

    public void MarkScanFailed(string? error, DateTimeOffset now)
    {
        Status = FileStatuses.ScanFailed;
        RejectReason = error is { Length: > 300 } ? error[..300] : error;
        ScannedAt = now;
    }

    /// <summary>Admin re-scan of a failed file.</summary>
    public void ResetForRescan()
    {
        Status = FileStatuses.PendingScan;
        ScanAttempts = 0;
        RejectReason = null;
    }
}

/// <summary>Keys of derived objects (re-encoded image, thumbnail) and image dimensions.</summary>
public sealed record FileDerivatives(string? ThumbnailKey, int? Width, int? Height);
