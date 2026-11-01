using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Domain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Pishkhan.Modules.Knowledge.Services;

/// <summary>Why a file or page gave no text; the code is the source's <c>error</c>.</summary>
internal sealed class KnowledgeExtractionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>The text of a document or page, and the page's own title when it has one.</summary>
internal sealed record ExtractedText(string Text, string? Title);

/// <summary>
/// Plain text from the formats a knowledge source may come in. Inputs are already limited in size (20 MB uploads, scanned clean and
/// checked for zip bombs by the Files module; 2 MB pages), and the output is capped at <see cref="KnowledgeSource.MaxBody"/>.
/// </summary>
internal static partial class TextExtractor
{
    public const string Pdf = "application/pdf";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string PlainText = "text/plain";

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static string FromDocument(Stream content, string contentType) => contentType switch
    {
        PlainText => FromPlainText(content),
        Docx => FromDocx(content),
        Pdf => FromPdf(content),
        _ => throw new KnowledgeExtractionException("knowledge.file_type"),
    };

    public static string FromPlainText(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Tidy(reader.ReadToEnd());
    }

    /// <summary>The paragraphs of <c>word/document.xml</c>, one per line (tables read row by row, cell by cell).</summary>
    public static string FromDocx(Stream content)
    {
        using var zip = new ZipArchive(content, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new KnowledgeExtractionException("knowledge.file_unreadable");
        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
        var text = new StringBuilder();
        xml.Read();
        while (!xml.EOF)
        {
            if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == WordNamespace && xml.LocalName == "t")
            {
                text.Append(xml.ReadElementContentAsString()); // leaves the reader on the next node
                continue;
            }

            if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == WordNamespace)
            {
                switch (xml.LocalName)
                {
                    case "tab":
                        text.Append(' ');
                        break;
                    case "br":
                        text.Append('\n');
                        break;
                    case "tc":
                        text.Append(" | ");
                        break;
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.NamespaceURI == WordNamespace && xml.LocalName is "p" or "tr")
            {
                text.Append('\n');
            }

            xml.Read();
        }

        return Tidy(text.ToString());
    }

    /// <summary>
    /// Text in reading order, page by page. Persian PDFs often store presentation forms (contextual glyphs), so the text is NFKC-normalised
    /// back to plain letters. A scanned PDF has no text layer and gives <c>knowledge.empty</c>.
    /// </summary>
    public static string FromPdf(Stream content)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                text.Append(ContentOrderTextExtractor.GetText(page)).Append('\n');
                if (text.Length > KnowledgeSource.MaxBody * 2)
                {
                    break;
                }
            }

            return Tidy(text.ToString().Normalize(NormalizationForm.FormKC));
        }
        catch (Exception ex) when (ex is not KnowledgeExtractionException and not OperationCanceledException)
        {
            throw new KnowledgeExtractionException("knowledge.file_unreadable");
        }
    }

    /// <summary>The visible text of an HTML page (no scripts, styles, navigation chrome or comments), blocks on their own lines.</summary>
    public static ExtractedText FromHtml(string html)
    {
        var title = TitleRegex().Match(html) is { Success: true } m ? Tidy(WebUtility.HtmlDecode(TagRegex().Replace(m.Groups[1].Value, " "))) : null;
        var body = BodyRegex().Match(html) is { Success: true } b ? b.Groups[1].Value : html;
        body = HiddenRegex().Replace(body, " ");
        body = CommentRegex().Replace(body, " ");
        body = BlockRegex().Replace(body, "\n");
        body = TagRegex().Replace(body, " ");
        return new ExtractedText(Tidy(WebUtility.HtmlDecode(body)), string.IsNullOrEmpty(title) ? null : title);
    }

    /// <summary>Spaces collapsed, blank lines dropped, and the result capped (at a line break when there is one).</summary>
    public static string Tidy(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(l => SpacesRegex().Replace(l, " ").Trim())
            .Where(l => l.Length > 0);
        var joined = string.Join('\n', lines);
        if (joined.Length <= KnowledgeSource.MaxBody)
        {
            return joined;
        }

        var cut = joined.LastIndexOf('\n', KnowledgeSource.MaxBody - 1);
        return joined[..(cut > KnowledgeSource.MaxBody / 2 ? cut : KnowledgeSource.MaxBody)];
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"<(script|style|noscript|svg|template|nav|header|footer|iframe|form)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex HiddenRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, 1000)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article|/blockquote|/dd|/dt)\b[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, 1000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[\s​ ]+", RegexOptions.None, 1000)]
    private static partial Regex SpacesRegex();
}

/// <summary>Reads the text of a file or URL source (AI group, before indexing).</summary>
internal sealed class KnowledgeContentReader(IFileService files, IObjectStorage storage, SafeHttpClient http)
{
    public const long MaxPageBytes = 2 * 1024 * 1024;

    public async Task<ExtractedText> ReadAsync(KnowledgeSource source, CancellationToken ct)
    {
        string text;
        string? title = null;
        if (source.Kind == KnowledgeSourceKinds.File)
        {
            var file = await files.GetCleanAsync(source.FileId!.Value, ct);
            if (file is null || file.Purpose != FilePurposes.KnowledgeDocument)
            {
                throw new KnowledgeExtractionException("knowledge.file_unavailable");
            }

            await using var content = await storage.OpenReadAsync(file.Bucket, file.StorageKey, ct);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            text = TextExtractor.FromDocument(buffer, file.ContentType);
        }
        else
        {
            SafeDownload page;
            try
            {
                page = await http.DownloadAsync(new Uri(source.Url!), MaxPageBytes, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is SafeHttpException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new KnowledgeExtractionException(ex is SafeHttpException { Message: "response.too_large" } ? "knowledge.page_too_large" : "knowledge.page_unreachable");
            }

            using (page.Content)
            {
                var bytes = page.Content.ToArray();
                switch (page.ContentType)
                {
                    case "text/html" or "application/xhtml+xml" or null:
                        var html = TextExtractor.FromHtml(Encoding.UTF8.GetString(bytes));
                        (text, title) = (html.Text, html.Title);
                        break;
                    case TextExtractor.PlainText:
                        text = TextExtractor.FromPlainText(new MemoryStream(bytes));
                        break;
                    case TextExtractor.Pdf:
                        text = TextExtractor.FromPdf(new MemoryStream(bytes));
                        break;
                    default:
                        throw new KnowledgeExtractionException("knowledge.page_type");
                }
            }
        }

        return string.IsNullOrWhiteSpace(text) ? throw new KnowledgeExtractionException("knowledge.empty") : new ExtractedText(text, title);
    }
}
