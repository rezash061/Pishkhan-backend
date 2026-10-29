using Pishkhan.Contracts;

namespace Pishkhan.Modules.Files.Contracts;

public static class FilePurposes
{
    public const string ProductImage = "product_image";
    public const string KnowledgeDocument = "knowledge_document";
    public const string ImportSheet = "import_sheet";
    public const string Avatar = "avatar";
    public const string Receipt = "receipt";
    public const string MessageAttachment = "message_attachment";
    public const string CmsMedia = "cms_media";
}

public static class FileStatuses
{
    public const string PendingScan = "pending_scan";
    public const string Clean = "clean";
    public const string Infected = "infected";
    public const string ScanFailed = "scan_failed";
    public const string Rejected = "rejected";
}

[IntegrationEvent(Topics.FileScanRequested)]
public sealed record FileScanRequested(Guid FileId, Guid WorkspaceId) : IIntegrationEvent;

/// <summary>Owners of the file's purpose react to this: attach to a product, start OCR on a receipt, index a document.</summary>
[IntegrationEvent(Topics.FileScanCompleted)]
public sealed record FileScanCompleted(Guid FileId, Guid WorkspaceId, string Purpose, string Status, string? ContentType, long Size) : IIntegrationEvent;

public sealed record CleanFile(Guid Id, Guid WorkspaceId, string Purpose, string ContentType, long Size, string Bucket, string StorageKey);

/// <summary>In-process API for other modules.</summary>
public interface IFileService
{
    /// <summary>Stores bytes that did not come through the upload endpoint (for example Instagram media) in quarantine and requests a scan.</summary>
    Task<Guid> IngestAsync(Guid workspaceId, string purpose, Stream content, string? declaredContentType, string? originalName, CancellationToken cancellationToken);

    /// <summary>The file if, and only if, it is clean. Never returns quarantined, infected or failed files.</summary>
    Task<CleanFile?> GetCleanAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Signed URLs of the clean files among <paramref name="fileIds"/> (of the current workspace), valid for <paramref name="lifetime"/>.
    /// Files that are not clean are left out. Used where a third party fetches the file, such as Instagram card images.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Uri>> GetCleanUrlsAsync(IReadOnlyCollection<Guid> fileIds, TimeSpan lifetime, CancellationToken cancellationToken);
}
