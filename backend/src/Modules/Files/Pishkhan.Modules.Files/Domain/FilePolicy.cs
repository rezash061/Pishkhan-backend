using Pishkhan.Modules.Files.Contracts;

namespace Pishkhan.Modules.Files.Domain;

/// <summary>Allowed detected content types and maximum size per purpose (backend plan 8.1).</summary>
public static class FilePolicy
{
    public const long MB = 1024 * 1024;

    private static readonly string[] Images = ["image/jpeg", "image/png", "image/webp", "image/gif"];
    private static readonly string[] Audio = ["audio/ogg", "audio/mp4", "audio/mpeg", "audio/aac"];

    public sealed record Rule(IReadOnlyList<string> ContentTypes, long MaxBytes);

    public static readonly IReadOnlyDictionary<string, Rule> Rules = new Dictionary<string, Rule>(StringComparer.Ordinal)
    {
        [FilePurposes.ProductImage] = new(Images, 10 * MB),
        [FilePurposes.Avatar] = new(Images, 2 * MB),
        [FilePurposes.Receipt] = new([.. Images, "application/pdf"], 10 * MB),
        [FilePurposes.MessageAttachment] = new([.. Images, .. Audio, "video/mp4", "application/pdf"], 25 * MB),
        [FilePurposes.KnowledgeDocument] = new(["application/pdf", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "text/plain"], 20 * MB),
        [FilePurposes.ImportSheet] = new(["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text/csv"], 5 * MB),
        [FilePurposes.CmsMedia] = new([.. Images, "application/pdf"], 10 * MB),
    };

    /// <summary>Extensions that agree with each detected type. Macro-enabled Office types are never allowed.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Extensions = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["image/jpeg"] = [".jpg", ".jpeg", ".jfif"],
        ["image/png"] = [".png"],
        ["image/webp"] = [".webp"],
        ["image/gif"] = [".gif"],
        ["application/pdf"] = [".pdf"],
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = [".docx"],
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = [".xlsx"],
        ["text/plain"] = [".txt"],
        ["text/csv"] = [".csv"],
        ["audio/ogg"] = [".ogg", ".oga", ".opus"],
        ["audio/mp4"] = [".m4a", ".mp4"],
        ["audio/mpeg"] = [".mp3"],
        ["audio/aac"] = [".aac"],
        ["video/mp4"] = [".mp4", ".mov"],
    };

    public static bool IsImage(string contentType) => Images.Contains(contentType, StringComparer.Ordinal);
}
