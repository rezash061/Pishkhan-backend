using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Files.Domain;
using Pishkhan.Modules.Files.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Files.Services;

/// <summary>Receives a stream into quarantine: type detection from magic bytes, size cap, SHA-256, then a scan request.</summary>
internal sealed class FileIntake(FilesDbContext db, IObjectStorage storage, IClock clock) : IFileService
{
    public async Task<Result<StoredFile>> ReceiveAsync(Guid workspaceId, string purpose, Stream content, string? declaredContentType, string? originalName, Guid? uploadedBy, CancellationToken cancellationToken)
    {
        if (!FilePolicy.Rules.TryGetValue(purpose, out var rule))
        {
            return Error.Validation("validation.file.purpose");
        }

        var header = new byte[ContentSniffer.HeaderSize];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (headerLength == 0)
        {
            return Error.Validation("validation.file.empty");
        }

        var name = Sanitize(originalName);
        var extension = name is null ? null : Path.GetExtension(name).ToLowerInvariant();
        var detected = ContentSniffer.Detect(header.AsSpan(0, headerLength), extension, declaredContentType);
        if (detected is null || !rule.ContentTypes.Contains(detected, StringComparer.Ordinal))
        {
            return Error.Unsupported("file.type_not_allowed");
        }

        // Declared type and extension must agree with what the bytes are.
        if (extension is { Length: > 0 } && FilePolicy.Extensions.TryGetValue(detected, out var allowed) && !allowed.Contains(extension, StringComparer.Ordinal))
        {
            return Error.Unsupported("file.extension_mismatch");
        }

        if (declaredContentType is { Length: > 0 } && !string.Equals(Normalize(declaredContentType), detected, StringComparison.Ordinal) && declaredContentType != "application/octet-stream")
        {
            return Error.Unsupported("file.content_type_mismatch");
        }

        var id = Uuid.NewV7(clock.UtcNow);
        var hashing = new HashingCappedStream(new PrefixedStream(header.AsMemory(0, headerLength), content), rule.MaxBytes);
        var key = $"{workspaceId:N}/{id:N}";
        try
        {
            await storage.PutAsync(Buckets.Quarantine, key, hashing, detected, cancellationToken);
        }
        catch (FileTooLargeException)
        {
            await storage.DeleteAsync(Buckets.Quarantine, key, CancellationToken.None);
            return Error.Validation("validation.file.too_large");
        }

        var file = StoredFile.Receive(id, workspaceId, purpose, name, declaredContentType, detected, hashing.BytesRead, hashing.Sha256Hex(), Buckets.Quarantine, uploadedBy, clock.UtcNow);
        db.Files.Add(file);
        db.Publish(new FileScanRequested(file.Id, workspaceId), key: file.Id.ToString(), workspaceId: workspaceId);
        await db.SaveChangesAsync(cancellationToken);
        return file;
    }

    public async Task<Guid> IngestAsync(Guid workspaceId, string purpose, Stream content, string? declaredContentType, string? originalName, CancellationToken cancellationToken)
    {
        var result = await ReceiveAsync(workspaceId, purpose, content, declaredContentType, originalName, null, cancellationToken);
        return result.IsSuccess ? result.Value.Id : throw new DomainException(result.Error!.Code);
    }

    public async Task<CleanFile?> GetCleanAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.Status == FileStatuses.Clean, cancellationToken);
        return file is null ? null : new CleanFile(file.Id, file.WorkspaceId, file.Purpose, file.ContentTypeDetected, file.Size, file.Bucket, file.StorageKey);
    }

    public async Task<IReadOnlyDictionary<Guid, Uri>> GetCleanUrlsAsync(IReadOnlyCollection<Guid> fileIds, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return new Dictionary<Guid, Uri>();
        }

        var files = await db.Files.AsNoTracking().Where(f => fileIds.Contains(f.Id) && f.Status == FileStatuses.Clean).ToListAsync(cancellationToken);
        return files.ToDictionary(f => f.Id, f => storage.GetSignedUrl(f.Bucket, f.StorageKey, lifetime));
    }

    private static string Normalize(string contentType) => contentType.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/jpg" or "image/pjpeg" => "image/jpeg",
        "audio/x-m4a" => "audio/mp4",
        "audio/mp3" => "audio/mpeg",
        "application/csv" => "text/csv",
        var other => other,
    };

    /// <summary>The original name is metadata only: path parts and control characters removed, at most 200 characters.</summary>
    internal static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var fileName = name.Replace('\\', '/').Split('/')[^1];
        var cleaned = new string(fileName.Where(c => !char.IsControl(c) && c is not ('<' or '>' or ':' or '"' or '|' or '?' or '*')).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, 200)];
    }
}
