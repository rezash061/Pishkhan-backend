using System.IO.Compression;
using SkiaSharp;

namespace Pishkhan.Modules.Files.Services;

/// <summary>
/// Re-encodes images (drops EXIF, GPS and anything appended to the pixel data), caps dimensions, and makes a WebP thumbnail.
/// </summary>
internal static class ImageSanitizer
{
    public const int MaxDimension = 4096;
    public const int ThumbnailSize = 320;

    public sealed record Output(MemoryStream Image, string ContentType, MemoryStream Thumbnail, int Width, int Height);

    public static Output? Sanitize(Stream input, string contentType)
    {
        using var codec = SKCodec.Create(input);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 50_000_000)
        {
            return null; // decompression bomb guard
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            return null;
        }

        using var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);
        using var capped = Resize(oriented, MaxDimension);
        var format = contentType switch
        {
            "image/png" => SKEncodedImageFormat.Png,
            "image/webp" or "image/gif" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg,
        };

        var image = Encode(capped, format, 88);
        using var thumb = Resize(capped, ThumbnailSize);
        var thumbnail = Encode(thumb, SKEncodedImageFormat.Webp, 80);
        var outType = format switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => "image/jpeg",
        };
        return new Output(image, outType, thumbnail, capped.Width, capped.Height);
    }

    private static MemoryStream Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }

    private static SKBitmap Resize(SKBitmap source, int max)
    {
        if (source.Width <= max && source.Height <= max)
        {
            return source.Copy();
        }

        var scale = Math.Min((float)max / source.Width, (float)max / source.Height);
        var info = new SKImageInfo(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return bitmap.Copy();
        }

        var rotate = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
        var result = new SKBitmap(rotate ? bitmap.Height : bitmap.Width, rotate ? bitmap.Width : bitmap.Height);
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, bitmap.Width / 2f, bitmap.Height / 2f);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(result.Width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, result.Height);
                canvas.RotateDegrees(270);
                break;
        }

        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return result;
    }
}

/// <summary>
/// Structural checks for OOXML (DOCX, XLSX) after the AV scan: entry count, total uncompressed size and compression ratio
/// (zip-bomb protection), and no macros (<c>vbaProject.bin</c>).
/// </summary>
internal static class OfficeInspector
{
    public const long MaxUncompressed = 100 * 1024 * 1024;
    public const int MaxEntries = 2000;
    public const double MaxRatio = 100;

    public static string? Check(Stream content, string contentType)
    {
        try
        {
            using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > MaxEntries)
            {
                return "file.archive.too_many_entries";
            }

            long total = 0;
            var hasContentTypes = false;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                if (total > MaxUncompressed || (entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > MaxRatio))
                {
                    return "file.archive.bomb";
                }

                if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && entry.FullName.Contains("activeX", StringComparison.OrdinalIgnoreCase))
                {
                    return "file.office.macros";
                }

                hasContentTypes |= entry.FullName == "[Content_Types].xml";
            }

            var expectedFolder = contentType.Contains("wordprocessingml", StringComparison.Ordinal) ? "word/" : "xl/";
            return hasContentTypes && zip.Entries.Any(e => e.FullName.StartsWith(expectedFolder, StringComparison.Ordinal)) ? null : "file.office.invalid";
        }
        catch (InvalidDataException)
        {
            return "file.archive.invalid";
        }
    }
}
