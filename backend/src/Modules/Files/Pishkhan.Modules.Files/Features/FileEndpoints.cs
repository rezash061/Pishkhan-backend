using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Files.Domain;
using Pishkhan.Modules.Files.Persistence;
using Pishkhan.Modules.Files.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Files.Features;

public sealed record FileResponse(Guid Id, string Purpose, string Status, string ContentType, long Size, string? OriginalName, string? Url, string? ThumbnailUrl, int? Width, int? Height, DateTimeOffset CreatedAt);

internal static class FileEndpoints
{
    /// <summary>Signed download URLs live 5 minutes (backend plan 8.1 step 3).</summary>
    private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/files").WithTags("Files");

        ws.MapPost("/", UploadAsync)
            .RequireWorkspacePermission(Permissions.FilesUpload)
            .RequireRateLimiting(RateLimitPolicies.Upload)
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(FilePolicy.MB * 26))
            .WithSummary("Upload a file into quarantine; it is scanned before use")
            .WithDescription("multipart/form-data with one part named `file` and query `purpose`. Returns 202; status becomes clean, infected, rejected or scan_failed. Only clean files have URLs.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<FileResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .WithStandardProblems();

        ws.MapGet("/{fileId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.FilesRead)
            .WithSummary("File status and, when clean, a 5-minute download URL")
            .Produces<FileResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Serves filesystem storage objects (dev and single-node installs). S3 deployments use presigned URLs instead.</summary>
    public static void MapUserContent(IEndpointRouteBuilder app)
    {
        app.MapGet("/usercontent/{bucket}/{**key}", async (string bucket, string key, long exp, string sig, string? name, HttpContext http, UrlSigner signer, IServiceProvider services) =>
            {
                if (bucket is not (Buckets.Files or Buckets.Public or Buckets.Exports)
                    || !signer.Verify(http.Request.Path, exp, sig, name is null ? null : Uri.EscapeDataString(name))
                    || services.GetService(typeof(FileSystemObjectStorage)) is not FileSystemObjectStorage storage
                    || !storage.TryResolve(bucket, key, out var path))
                {
                    return Results.NotFound();
                }

                var info = await storage.StatAsync(bucket, key, http.RequestAborted);
                var contentType = info?.ContentType ?? "application/octet-stream";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.CacheControl = "private, max-age=300";
                return FilePolicy.IsImage(contentType) && name is null
                    ? Results.File(path, contentType)
                    : Results.File(path, contentType, name ?? "download");
            })
            .ExcludeFromDescription()
            .AllowAnonymous();
    }

    private static async Task<IResult> UploadAsync(Guid workspaceId, string purpose, HttpContext http, FileIntake intake, ICurrentUser caller, CancellationToken ct)
    {
        if (!FilePolicy.Rules.TryGetValue(purpose, out var rule))
        {
            return Error.Validation("validation.file.purpose").ToProblem();
        }

        // Reject early on Content-Length, before reading the body.
        if (http.Request.ContentLength is { } length && length > rule.MaxBytes + 64 * 1024)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, type: ProblemTypes.Validation, title: "validation.file.too_large");
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = rule.MaxBytes + 64 * 1024;
        }

        var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType) ? mediaType.Boundary : default).Value;
        if (string.IsNullOrEmpty(boundary) || !string.Equals(mediaType?.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Unsupported("file.multipart_required").ToProblem();
        }

        var reader = new MultipartReader(boundary, http.Request.Body) { HeadersLengthLimit = 8 * 1024 };
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.IsFileDisposition() || disposition.Name.Value != "file")
            {
                continue;
            }

            var fileName = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : disposition.FileName.Value;
            Result<StoredFile> result;
            try
            {
                result = await intake.ReceiveAsync(workspaceId, purpose, section.Body, section.ContentType, fileName, caller.UserId, ct);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, type: ProblemTypes.Validation, title: "validation.file.too_large");
            }

            return result.ToHttp(file => Results.Accepted($"/api/v1/workspaces/{workspaceId}/files/{file.Id}", ToResponse(file, null)));
        }

        return Error.Validation("validation.file.missing").ToProblem();
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid fileId, FilesDbContext db, IObjectStorage storage, CancellationToken ct)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        return file is null ? Results.Problem(statusCode: 404, type: ProblemTypes.NotFound, title: "common.not_found") : Results.Ok(ToResponse(file, storage));
    }

    internal static FileResponse ToResponse(StoredFile file, IObjectStorage? storage)
    {
        string? url = null;
        string? thumb = null;
        if (storage is not null && file.Status == FileStatuses.Clean)
        {
            var downloadName = FilePolicy.IsImage(file.ContentTypeDetected) ? null : file.OriginalName ?? "file";
            url = storage.GetSignedUrl(file.Bucket, file.StorageKey, UrlLifetime, downloadName).ToString();
            thumb = file.Derived.ThumbnailKey is { } key ? storage.GetSignedUrl(file.Bucket, key, UrlLifetime).ToString() : null;
        }

        return new FileResponse(file.Id, file.Purpose, file.Status, file.ContentTypeDetected, file.Size, file.OriginalName, url, thumb, file.Derived.Width, file.Derived.Height, file.CreatedAt);
    }
}
