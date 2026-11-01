using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pishkhan.Modules.Content.Domain;
using Pishkhan.Persian;

namespace Pishkhan.Modules.Content.Features;

/// <summary>
/// Checks CMS input. Blocks are structured JSON, never HTML (website plan section 3): each block is an object with an allowed
/// <c>type</c>; strings are plain text the site escapes; link and image fields must be https or a site path.
/// </summary>
internal static partial class ContentRules
{
    public const int MaxBlocks = 300;
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxDataBytes = 32 * 1024;
    public const int MaxString = 20_000;
    public const int MaxDepth = 6;

    /// <summary>The website's block renderer set (website <c>content/types.ts</c> and the admin plan's page blocks).</summary>
    public static readonly IReadOnlySet<string> BlockTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "p", "h2", "h3", "ul", "ol", "callout", "quote", "image", "hero", "features", "comparison", "faq", "cta", "testimonial", "pricing", "steps", "metrics",
    };

    private static readonly IReadOnlySet<string> LinkFields = new HashSet<string>(StringComparer.Ordinal) { "href", "url", "src", "image", "cover", "logo", "canonical" };

    /// <summary>Lower-case Latin letters, digits and hyphens, or Persian letters with hyphens (Persian-safe slugs).</summary>
    [GeneratedRegex(@"^[a-z0-9؀-ۿ‌]+(-[a-z0-9؀-ۿ‌]+)*$", RegexOptions.CultureInvariant)]
    public static partial Regex Slug();

    [GeneratedRegex(@"^/[^\s?#]{0,299}$", RegexOptions.CultureInvariant)]
    public static partial Regex SitePath();

    public static bool IsSlug(string? slug) => slug is { Length: > 0 and <= ContentEntry.MaxSlug } && Slug().IsMatch(slug);

    public static bool IsLink(string value) =>
        (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) && SitePath().IsMatch(value))
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Validates a block array; returns its compact JSON, or the error code.</summary>
    public static (string? Json, string? Error) Body(JsonElement? body)
    {
        if (body is not { } b || b.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return ("[]", null);
        }

        if (b.ValueKind != JsonValueKind.Array || b.GetArrayLength() > MaxBlocks)
        {
            return (null, "validation.content.body");
        }

        foreach (var block in b.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !BlockTypes.Contains(type.GetString()!))
            {
                return (null, "validation.content.block_type");
            }

            if (Check(block, 0) is { } error)
            {
                return (null, error);
            }
        }

        var json = b.GetRawText();
        return Encoding.UTF8.GetByteCount(json) > MaxBodyBytes ? (null, "validation.too_long") : (JsonSerializer.Serialize(b), null);
    }

    /// <summary>Validates the kind-specific fields (an object); returns its compact JSON, or the error code.</summary>
    public static (string? Json, string? Error) Data(JsonElement? data)
    {
        if (data is not { } d || d.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return ("{}", null);
        }

        if (d.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(d.GetRawText()) > MaxDataBytes)
        {
            return (null, "validation.content.data");
        }

        return Check(d, 0) is { } error ? (null, error) : (JsonSerializer.Serialize(d), null);
    }

    private static string? Check(JsonElement value, int depth)
    {
        if (depth > MaxDepth)
        {
            return "validation.content.too_deep";
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && LinkFields.Contains(property.Name)
                        && property.Value.GetString() is { Length: > 0 } link && !IsLink(link))
                    {
                        return "validation.content.link";
                    }

                    if (Check(property.Value, depth + 1) is { } error)
                    {
                        return error;
                    }
                }

                return null;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (Check(item, depth + 1) is { } error)
                    {
                        return error;
                    }
                }

                return null;
            case JsonValueKind.String:
                return value.GetString()!.Length > MaxString ? "validation.too_long" : null;
            default:
                return null;
        }
    }

    /// <summary>Normalised text of an entry (title, description and every string in its blocks) for search.</summary>
    public static string SearchText(EntryContent content)
    {
        var text = new StringBuilder();
        text.Append(content.Title).Append(' ').Append(content.Description).Append(' ');
        using (var body = JsonDocument.Parse(content.Body))
        {
            Collect(body.RootElement, text);
        }

        using (var data = JsonDocument.Parse(content.Data))
        {
            if (data.RootElement.TryGetProperty("keywords", out var keywords))
            {
                Collect(keywords, text);
            }
        }

        return PersianNormalizer.Normalize(text.ToString()).ToLowerInvariant();
    }

    private static void Collect(JsonElement value, StringBuilder text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name is not ("type" or "id" or "tone") && !LinkFields.Contains(property.Name))
                    {
                        Collect(property.Value, text);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    Collect(item, text);
                }

                break;
            case JsonValueKind.String:
                text.Append(value.GetString()).Append(' ');
                break;
        }
    }

    /// <summary>The website paths that show an entry (website app routes), to revalidate after a change.</summary>
    public static IReadOnlyList<string> Paths(string kind, string slug, string? category) => kind switch
    {
        ContentKinds.Post => ["/blog", $"/blog/{slug}", .. category is null ? Array.Empty<string>() : [$"/blog/category/{category}"]],
        ContentKinds.Help => ["/help", .. category is null ? Array.Empty<string>() : [$"/help/{category}", $"/help/{category}/{slug}"]],
        ContentKinds.CaseStudy => ["/case-studies", $"/case-studies/{slug}"],
        ContentKinds.Page => [slug == "home" ? "/" : $"/{slug}"],
        ContentKinds.Legal => [$"/{slug}"],
        _ => [],
    };

    /// <summary>The site's cache tags: <c>content</c> for everything, <c>content:{kind}</c> for one kind.</summary>
    public static IReadOnlyList<string> Tags(string kind) => ["content", $"content:{kind}"];
}
