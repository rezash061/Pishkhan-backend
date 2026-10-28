using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Pishkhan.Infrastructure.Storage;

/// <summary>Buckets of backend plan 4.2.</summary>
public static class Buckets
{
    public const string Quarantine = "quarantine";
    public const string Files = "files";
    public const string Public = "public";
    public const string Exports = "exports";

    public static readonly IReadOnlyList<string> All = [Quarantine, Files, Public, Exports];
}

public sealed record StoredObjectInfo(long Size, string? ContentType);

/// <summary>S3-compatible object storage (MinIO, ArvanCloud) with a local filesystem implementation for dev and tests.</summary>
public interface IObjectStorage
{
    Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken = default);

    Task<StoredObjectInfo?> StatAsync(string bucket, string key, CancellationToken cancellationToken = default);

    Task CopyAsync(string sourceBucket, string sourceKey, string targetBucket, string targetKey, CancellationToken cancellationToken = default);

    Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>A short-lived download URL (default 5 minutes) on the user-content domain.</summary>
    Uri GetSignedUrl(string bucket, string key, TimeSpan lifetime, string? downloadFileName = null);

    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary><c>s3</c> or <c>filesystem</c>.</summary>
    public string Provider { get; set; } = "filesystem";

    public string RootPath { get; set; } = ".pishkhan/storage";

    public string? ServiceUrl { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public string Region { get; set; } = "us-east-1";

    public string BucketPrefix { get; set; } = "pishkhan-";

    /// <summary>Base URL of the user-content host that serves filesystem objects (<c>/usercontent/...</c>), for example https://usercontent.example.ir.</summary>
    public string UserContentBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>HMAC key (base64) for signing filesystem download URLs.</summary>
    public string? UrlSigningKey { get; set; }
}

public sealed class S3ObjectStorage(IAmazonS3 s3, IOptions<StorageOptions> options) : IObjectStorage
{
    private string Bucket(string bucket) => options.Value.BucketPrefix + bucket;

    public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var transfer = new Amazon.S3.Transfer.TransferUtility(s3);
        await transfer.UploadAsync(new Amazon.S3.Transfer.TransferUtilityUploadRequest
        {
            BucketName = Bucket(bucket),
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var response = await s3.GetObjectAsync(Bucket(bucket), key, cancellationToken);
        return response.ResponseStream;
    }

    public async Task<StoredObjectInfo?> StatAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var meta = await s3.GetObjectMetadataAsync(Bucket(bucket), key, cancellationToken);
            return new StoredObjectInfo(meta.ContentLength, meta.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task CopyAsync(string sourceBucket, string sourceKey, string targetBucket, string targetKey, CancellationToken cancellationToken = default) =>
        s3.CopyObjectAsync(Bucket(sourceBucket), sourceKey, Bucket(targetBucket), targetKey, cancellationToken);

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
        s3.DeleteObjectAsync(Bucket(bucket), key, cancellationToken);

    public Uri GetSignedUrl(string bucket, string key, TimeSpan lifetime, string? downloadFileName = null)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket(bucket),
            Key = key,
            Expires = DateTime.UtcNow.Add(lifetime),
            Verb = HttpVerb.GET,
        };
        if (downloadFileName is not null)
        {
            request.ResponseHeaderOverrides.ContentDisposition = ContentDisposition.Attachment(downloadFileName);
        }

        return new Uri(s3.GetPreSignedURL(request));
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket(Buckets.Files), MaxKeys = 1 }, cancellationToken);
            return true;
        }
        catch (AmazonServiceException)
        {
            return false;
        }
    }
}

/// <summary>Stores objects under a root folder. Download URLs point at the Api's <c>/usercontent</c> endpoint and are HMAC signed.</summary>
public sealed class FileSystemObjectStorage(IOptions<StorageOptions> options, UrlSigner signer) : IObjectStorage
{
    private string PathFor(string bucket, string key)
    {
        if (key.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(key) || bucket.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        return Path.Combine(Path.GetFullPath(options.Value.RootPath), bucket, key);
    }

    public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = PathFor(bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, cancellationToken);
        await File.WriteAllTextAsync(path + ".meta", contentType, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new FileStream(PathFor(bucket, key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public async Task<StoredObjectInfo?> StatAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(bucket, key);
        if (!File.Exists(path))
        {
            return null;
        }

        var contentType = File.Exists(path + ".meta") ? await File.ReadAllTextAsync(path + ".meta", cancellationToken) : null;
        return new StoredObjectInfo(new FileInfo(path).Length, contentType);
    }

    public async Task CopyAsync(string sourceBucket, string sourceKey, string targetBucket, string targetKey, CancellationToken cancellationToken = default)
    {
        var target = PathFor(targetBucket, targetKey);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(PathFor(sourceBucket, sourceKey), target, overwrite: true);
        var meta = PathFor(sourceBucket, sourceKey) + ".meta";
        if (File.Exists(meta))
        {
            File.Copy(meta, target + ".meta", overwrite: true);
        }

        await Task.CompletedTask;
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(bucket, key);
        File.Delete(path);
        File.Delete(path + ".meta");
        return Task.CompletedTask;
    }

    public Uri GetSignedUrl(string bucket, string key, TimeSpan lifetime, string? downloadFileName = null) =>
        signer.Sign(new Uri(new Uri(options.Value.UserContentBaseUrl), $"/usercontent/{bucket}/{key}"), lifetime, downloadFileName);

    public Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(root);
        return Task.FromResult(Directory.Exists(root));
    }

    public bool TryResolve(string bucket, string key, out string path)
    {
        try
        {
            path = PathFor(bucket, key);
            return File.Exists(path);
        }
        catch (ArgumentException)
        {
            path = string.Empty;
            return false;
        }
    }
}

/// <summary>HMAC-signed, expiring URLs (user content, payment links later).</summary>
public sealed class UrlSigner
{
    private readonly byte[] _key;

    public UrlSigner(IOptions<StorageOptions> options)
    {
        _key = options.Value.UrlSigningKey is { Length: > 0 } k ? Convert.FromBase64String(k) : RandomNumberGenerator.GetBytes(32);
    }

    public Uri Sign(Uri url, TimeSpan lifetime, string? downloadFileName = null)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var name = downloadFileName is null ? string.Empty : Uri.EscapeDataString(downloadFileName);
        var signature = Compute(url.AbsolutePath, expires, name);
        var query = $"exp={expires}&sig={signature}" + (name.Length > 0 ? $"&name={name}" : string.Empty);
        return new UriBuilder(url) { Query = query }.Uri;
    }

    public bool Verify(string path, long expires, string signature, string? escapedName)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
        {
            return false;
        }

        var expected = Compute(path, expires, escapedName ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    private string Compute(string path, long expires, string name)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{path}\n{expires}\n{name}"));
        return Base64Url.Encode(mac);
    }
}

internal static class ContentDisposition
{
    public static string Attachment(string fileName) => $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    public static byte[] Decode(string value) => System.Buffers.Text.Base64Url.DecodeFromChars(value);
}

public static class StorageExtensions
{
    public static IServiceCollection AddPishkhanStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.Section));
        services.AddSingleton<UrlSigner>();
        var provider = configuration[$"{StorageOptions.Section}:Provider"] ?? "filesystem";
        if (string.Equals(provider, "s3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAmazonS3>(sp =>
            {
                var o = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
                return new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), new AmazonS3Config
                {
                    ServiceURL = o.ServiceUrl,
                    ForcePathStyle = true,
                    AuthenticationRegion = o.Region,
                });
            });
            services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        }
        else
        {
            services.AddSingleton<FileSystemObjectStorage>();
            services.AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<FileSystemObjectStorage>());
        }

        return services;
    }
}
