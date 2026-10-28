using System.Text;

namespace Pishkhan.Modules.Files.Domain;

/// <summary>
/// Detects the real type from magic bytes with our own small signature table (only allowed types are recognized).
/// Office documents are ZIP containers: they are detected as ZIP here and their structure is verified after the scan.
/// </summary>
public static class ContentSniffer
{
    public const int HeaderSize = 512;
    public const string Zip = "application/zip";

    public static string? Detect(ReadOnlySpan<byte> header, string? extension, string? declared)
    {
        if (header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return "image/jpeg";
        }

        if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (header.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (header.StartsWith("OggS"u8))
        {
            return "audio/ogg";
        }

        if (header.StartsWith("ID3"u8) || (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && (header[1] & 0x06) != 0))
        {
            return "audio/mpeg";
        }

        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xF6) == 0xF0)
        {
            return "audio/aac";
        }

        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(header[8..12]);
            return brand is "M4A " or "M4B " ? "audio/mp4" : (extension is ".m4a" ? "audio/mp4" : "video/mp4");
        }

        if (header.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return extension switch
            {
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                _ => Zip,
            };
        }

        if (LooksLikeText(header))
        {
            return extension == ".csv" || declared == "text/csv" ? "text/csv" : "text/plain";
        }

        return null;
    }

    /// <summary>UTF-8 without NUL or most control characters.</summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        foreach (var b in header)
        {
            if (b == 0 || (b < 0x09) || (b > 0x0D && b < 0x20 && b != 0x1B))
            {
                return false;
            }
        }

        // The header may cut a multi-byte character; validate all but the last 3 bytes.
        var check = header.Length > 3 ? header[..^3] : header;
        return Utf8.IsValid(check) || Utf8.IsValid(header);
    }
}

internal static class Utf8
{
    public static bool IsValid(ReadOnlySpan<byte> bytes) => System.Text.Unicode.Utf8.IsValid(bytes);
}
