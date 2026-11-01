using System.IO.Compression;
using System.Text;
using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Pishkhan.UnitTests;

public sealed class KnowledgeExtractionTests
{
    [Fact]
    public void Plain_text_keeps_persian_and_drops_blank_lines_and_the_bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("ارسال   به شهرستان\r\n\r\n\n  دو تا چهار روز کاری  \n")).ToArray();
        TextExtractor.FromPlainText(new MemoryStream(bytes)).ShouldBe("ارسال به شهرستان\nدو تا چهار روز کاری");
    }

    [Fact]
    public void Docx_paragraphs_and_tables_become_lines()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="{w}"><w:body>
              <w:p><w:r><w:t>شرایط </w:t></w:r><w:r><w:t>مرجوعی</w:t></w:r></w:p>
              <w:p><w:r><w:t>تا هفت روز</w:t><w:br/><w:t>با فاکتور</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>سایز</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>M</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            entry.Write(xml);
        }

        buffer.Position = 0;
        TextExtractor.FromDocx(buffer).ShouldBe("شرایط مرجوعی\nتا هفت روز\nبا فاکتور\n| سایز\n| M");
    }

    [Fact]
    public void Docx_without_a_document_part_is_unreadable()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("other.xml");
        }

        buffer.Position = 0;
        Should.Throw<KnowledgeExtractionException>(() => TextExtractor.FromDocx(buffer)).Code.ShouldBe("knowledge.file_unreadable");
    }

    [Fact]
    public void Pdf_text_is_read_in_order_and_garbage_is_unreadable()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Shipping takes two to four days.", 12, new PdfPoint(25, 700), font);
        page.AddText("Returns within seven days.", 12, new PdfPoint(25, 680), font);
        var text = TextExtractor.FromPdf(new MemoryStream(builder.Build()));
        text.ShouldContain("Shipping takes two to four days.");
        text.ShouldContain("Returns within seven days.");
        text.IndexOf("Shipping", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("Returns", StringComparison.Ordinal));

        Should.Throw<KnowledgeExtractionException>(() => TextExtractor.FromPdf(new MemoryStream("not a pdf"u8.ToArray()))).Code.ShouldBe("knowledge.file_unreadable");
    }

    [Fact]
    public void Pdf_presentation_forms_are_normalised_to_letters()
    {
        // "سلام" as Arabic presentation forms (isolated/initial/final glyphs), as many Persian PDFs store it.
        TextExtractor.Tidy("ﺳﻼﻡ".Normalize(NormalizationForm.FormKC)).ShouldBe("سلام");
    }

    [Fact]
    public void Html_keeps_visible_text_and_the_title_and_drops_scripts_and_chrome()
    {
        const string html = """
            <html><head><title> درباره ما | فروشگاه نمونه </title><style>body{color:red}</style></head>
            <body>
              <header><nav>خانه · محصولات</nav></header>
              <script>var x = "<p>secret</p>";</script>
              <h1>درباره&nbsp;ما</h1>
              <p>ما از سال ۱۳۹۵ <b>پوشاک</b> می‌فروشیم.</p><!-- note -->
              <ul><li>ارسال رایگان</li><li>مرجوعی ۷ روزه</li></ul>
              <footer>تماس</footer>
            </body></html>
            """;
        var result = TextExtractor.FromHtml(html);
        result.Title.ShouldBe("درباره ما | فروشگاه نمونه");
        result.Text.ShouldBe("درباره ما\nما از سال ۱۳۹۵ پوشاک می‌فروشیم.\nارسال رایگان\nمرجوعی ۷ روزه");
    }

    [Fact]
    public void Long_text_is_capped_at_a_line_break()
    {
        var line = new string('ا', 999);
        var text = TextExtractor.Tidy(string.Join('\n', Enumerable.Repeat(line, 100)));
        text.Length.ShouldBeLessThanOrEqualTo(KnowledgeSource.MaxBody);
        text.ShouldEndWith(line);
    }
}
