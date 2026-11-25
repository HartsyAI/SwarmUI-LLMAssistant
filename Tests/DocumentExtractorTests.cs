using System.Text;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Reading each kind of document: the text that comes out, the caps, files with no text, and damaged or hostile files, which are always a result and never an exception.</summary>
public sealed class DocumentExtractorTests
{
    private static readonly DocumentLimits Limits = new(200, 200_000);

    private static ExtractedDocument Read(string name, byte[] data, DocumentLimits limits = null) => DocumentExtraction.Extract(name, data, limits ?? Limits);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ---- PDF ----

    [Fact]
    public void A_pdf_gives_its_text_page_by_page_with_words_kept_apart()
    {
        ExtractedDocument doc = Read("report.pdf", DocumentFixtures.Pdf("The quarterly report\nRevenue grew by twelve percent", "Second page about costs"));
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        Assert.Equal("pdf", doc.Kind);
        Assert.Equal(2, doc.TotalPages);
        Assert.False(doc.Truncated);
        Assert.Contains("The quarterly report", doc.Pages[0]);
        Assert.Contains("Revenue grew by twelve percent", doc.Pages[0]);
        Assert.Contains("Second page about costs", doc.Pages[1]);
        string text = ChatDocuments.TextWithPages(doc);
        Assert.StartsWith("[Page 1]\n", text);
        Assert.Contains("[Page 2]\nSecond page about costs", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_hand_written_pdf_is_read_packed_or_not(bool flate)
    {
        ExtractedDocument doc = Read("raw.pdf", DocumentFixtures.RawPdf([DocumentFixtures.Lines("Hello from a hand written file", "And its second line")], flate));
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        Assert.Contains("Hello from a hand written file", doc.Text);
        Assert.Contains("And its second line", doc.Text);
    }

    [Fact]
    public void A_pdf_with_only_drawings_has_no_text_layer()
    {
        // What a scan looks like to a text reader: marks on the page and no characters.
        ExtractedDocument doc = Read("scan.pdf", DocumentFixtures.RawPdf([Encoding.ASCII.GetBytes("0 0 595 842 re f"), Encoding.ASCII.GetBytes("10 10 m 20 20 l S")]));
        Assert.Equal(DocumentStatus.NoTextLayer, doc.Status);
        Assert.Contains("no text layer", doc.Error);
        Assert.Contains("no OCR", doc.Error);
        Assert.Equal("", doc.Text);
        Assert.Equal(2, doc.TotalPages);
    }

    [Fact]
    public void A_pdf_with_some_pages_without_text_is_read_and_says_so_keeping_page_numbers()
    {
        ExtractedDocument doc = Read("mixed.pdf", DocumentFixtures.Pdf("First page words", "", "Third page words"));
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        Assert.Equal(3, doc.Pages.Count);
        Assert.Equal("", doc.Pages[1]);
        Assert.Contains(doc.Warnings, w => w.StartsWith("1 of 3 pages"));
        Assert.Contains("[Page 3]\nThird page words", ChatDocuments.TextWithPages(doc));
        Assert.DoesNotContain("[Page 2]", ChatDocuments.TextWithPages(doc));
    }

    [Fact]
    public void Text_that_means_nothing_is_not_handed_on()
    {
        // Control codes drawn as characters: what a font with no usable text mapping yields.
        byte[] stream = [.. Encoding.ASCII.GetBytes("BT /F1 12 Tf 40 780 Td ("), .. Enumerable.Range(1, 8).SelectMany(_ => new byte[] { 1, 2, 3, 4, 5, 6, 7 }), .. Encoding.ASCII.GetBytes(") Tj ET")];
        ExtractedDocument doc = Read("garbled.pdf", DocumentFixtures.RawPdf([stream]));
        Assert.Equal(DocumentStatus.NoTextLayer, doc.Status);
        Assert.Equal("", doc.Text);
    }

    [Fact]
    public void The_page_and_character_caps_cut_a_pdf_and_say_so()
    {
        byte[] pdf = DocumentFixtures.Pdf("one one one", "two two two", "three three three");
        ExtractedDocument pages = Read("p.pdf", pdf, new DocumentLimits(2, 1000));
        Assert.True(pages.Truncated);
        Assert.Equal(2, pages.Pages.Count);
        Assert.Equal(3, pages.TotalPages);
        Assert.Contains(pages.Warnings, w => w.Contains("first 2 of 3 pages"));
        ExtractedDocument chars = Read("c.pdf", pdf, new DocumentLimits(10, 15));
        Assert.True(chars.Truncated);
        Assert.Equal(15, chars.Pages.Sum(p => p.Length));
    }

    [Fact]
    public void A_password_protected_pdf_says_it_needs_a_password()
    {
        string garbage = string.Concat(Enumerable.Repeat("a1", 32));
        byte[] pdf = DocumentFixtures.RawPdf([DocumentFixtures.Lines("secret")], trailer: "/Encrypt {extra} /ID [<0011223344556677> <0011223344556677>]",
            extraObject: $"<< /Filter /Standard /V 1 /R 2 /O <{garbage}> /U <{garbage}> /P -4 >>");
        ExtractedDocument doc = Read("locked.pdf", pdf);
        Assert.Equal(DocumentStatus.Encrypted, doc.Status);
        Assert.Contains("password", doc.Error);
    }

    [Theory]
    [InlineData("%PDF-1.7\nthis is not a pdf at all")]
    [InlineData("%PDF-")]
    [InlineData("hello")]
    public void A_damaged_pdf_is_a_result_not_an_exception(string content)
    {
        ExtractedDocument doc = Read("bad.pdf", Utf8(content));
        Assert.Equal(DocumentStatus.Malformed, doc.Status);
        Assert.NotEmpty(doc.Error);
    }

    [Fact]
    public void Damaged_copies_of_a_good_pdf_never_throw()
    {
        byte[] good = DocumentFixtures.RawPdf([DocumentFixtures.Lines("alpha beta gamma"), DocumentFixtures.Lines("delta")], true);
        Random random = new(1234);
        for (int round = 0; round < 300; round++)
        {
            byte[] bad = round % 3 == 0 ? good[..random.Next(6, good.Length)] : [.. good];
            for (int change = 0; change < (round % 7) + 1; change++)
            {
                bad[random.Next(5, bad.Length)] = (byte)random.Next(256);
            }
            ExtractedDocument doc = Read("fuzz.pdf", bad);
            Assert.True(doc.Status == DocumentStatus.Ok || doc.Error.Length > 0);
        }
    }

    [Fact]
    public void A_pdf_that_unpacks_far_beyond_the_limit_is_refused()
    {
        byte[] bomb = new byte[PdfDocumentExtractor.MaxStreamBytes + 1024 * 1024];
        byte[] pdf = DocumentFixtures.RawPdf([bomb], true);
        Assert.True(pdf.Length < 1024 * 1024);
        Assert.Equal(DocumentStatus.TooLarge, Read("bomb.pdf", pdf).Status);
    }

    // ---- Word ----

    [Fact]
    public void A_word_file_keeps_headings_paragraphs_tables_tabs_and_breaks()
    {
        string body = DocumentFixtures.Paragraph("Project Plan", "Title") + DocumentFixtures.Paragraph("Goals", "Heading2")
            + "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"100\"/></w:tabs></w:pPr><w:r><w:t xml:space=\"preserve\">Ship </w:t></w:r><w:r><w:t>it</w:t><w:tab/><w:t>soon</w:t><w:br/><w:t>today</w:t></w:r></w:p>"
            + "<w:tbl><w:tr><w:tc>" + DocumentFixtures.Paragraph("Name") + "</w:tc><w:tc>" + DocumentFixtures.Paragraph("Cost") + "</w:tc></w:tr>"
            + "<w:tr><w:tc>" + DocumentFixtures.Paragraph("Widget") + "</w:tc><w:tc>" + DocumentFixtures.Paragraph("12") + "</w:tc></w:tr></w:tbl>"
            + DocumentFixtures.Paragraph("The end");
        ExtractedDocument doc = Read("plan.docx", DocumentFixtures.Docx(body));
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        Assert.Equal("docx", doc.Kind);
        Assert.Equal(0, doc.TotalPages);
        Assert.Equal("# Project Plan\n\n## Goals\n\nShip it\tsoon\ntoday\n\nName | Cost\nWidget | 12\n\nThe end", doc.Text);
    }

    [Fact]
    public void Deleted_text_field_codes_and_fallback_copies_are_left_out()
    {
        string body = "<w:p><w:r><w:t>kept</w:t></w:r><w:del><w:r><w:delText>deleted</w:delText></w:r></w:del><w:r><w:instrText>HYPERLINK x</w:instrText></w:r>"
            + "<w:moveFrom><w:r><w:t>moved away</w:t></w:r></w:moveFrom></w:p>"
            + "<w:p><w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:t>box</w:t></mc:Choice><mc:Fallback><w:t>box copy</w:t></mc:Fallback></mc:AlternateContent></w:r></w:p>";
        Assert.Equal("kept\n\nbox", Read("d.docx", DocumentFixtures.Docx(body)).Text);
    }

    [Fact]
    public void A_word_file_over_the_character_cap_is_cut_and_says_so()
    {
        ExtractedDocument doc = Read("long.docx", DocumentFixtures.Docx(string.Concat(Enumerable.Repeat(DocumentFixtures.Paragraph("0123456789"), 500))), new DocumentLimits(1, 100));
        Assert.True(doc.Truncated);
        Assert.Equal(100, doc.Text.Length);
    }

    [Fact]
    public void An_empty_word_file_has_no_text()
        => Assert.Equal(DocumentStatus.NoTextLayer, Read("e.docx", DocumentFixtures.Docx("<w:p/>")).Status);

    [Fact]
    public void Damaged_word_files_are_results_not_exceptions()
    {
        Assert.Equal(DocumentStatus.Malformed, Read("a.docx", Utf8("not a zip")).Status);
        Assert.Equal(DocumentStatus.Malformed, Read("b.docx", DocumentFixtures.Zip(("xl/workbook.xml", Utf8("<a/>")))).Status);
        Assert.Equal(DocumentStatus.Malformed, Read("c.docx", DocumentFixtures.Docx(null, "<w:document><unclosed>")).Status);
        byte[] good = DocumentFixtures.Docx(DocumentFixtures.Paragraph("hello there"));
        Random random = new(99);
        for (int round = 0; round < 300; round++)
        {
            byte[] bad = round % 3 == 0 ? good[..random.Next(1, good.Length)] : [.. good];
            bad[random.Next(bad.Length)] = (byte)random.Next(256);
            ExtractedDocument doc = Read("fuzz.docx", bad);
            Assert.True(doc.Status == DocumentStatus.Ok || doc.Error.Length > 0);
        }
    }

    [Fact]
    public void A_document_type_definition_is_refused()
    {
        string xml = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>"
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>&b;</w:t></w:r></w:p></w:body></w:document>";
        Assert.Equal(DocumentStatus.Malformed, Read("x.docx", DocumentFixtures.Docx(null, xml)).Status);
    }

    [Fact]
    public void A_word_file_that_unpacks_far_beyond_the_limit_is_refused()
    {
        byte[] docx = DocumentFixtures.Docx("<!--" + new string(' ', (int)DocxDocumentExtractor.MaxXmlBytes + 1024) + "-->");
        Assert.True(docx.Length < 1024 * 1024);
        Assert.Equal(DocumentStatus.TooLarge, Read("bomb.docx", docx).Status);
    }

    [Theory]
    [InlineData("old.doc")]
    [InlineData("locked.docx")]
    public void Old_and_locked_office_files_are_refused_with_the_way_out(string name)
    {
        ExtractedDocument doc = Read(name, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);
        Assert.Equal(DocumentStatus.Unsupported, doc.Status);
        Assert.Contains(".docx", doc.Error);
    }

    // ---- Text, Markdown, CSV, HTML ----

    [Theory]
    [InlineData("notes.txt", "plain notes\nsecond line")]
    [InlineData("guide.md", "# Guide\n\nUse **bold** words.")]
    [InlineData("table.csv", "name,cost\nwidget,12")]
    [InlineData("code.py", "def f():\n    return '<b>not a tag</b>'")]
    [InlineData("README", "a file with no extension")]
    public void Text_files_are_read_as_written(string name, string content)
    {
        ExtractedDocument doc = Read(name, Utf8(content.Replace("\n", "\r\n")));
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        Assert.Equal("text", doc.Kind);
        Assert.Equal(content, doc.Text);
    }

    [Fact]
    public void A_log_with_colour_codes_is_still_text()
        => Assert.Equal(DocumentStatus.Ok, Read("build.log", Utf8(string.Concat(Enumerable.Repeat("\u001B[31mfail\u001B[0m\n", 50)))).Status);

    [Fact]
    public void Encodings_are_recognised()
    {
        Assert.Equal("café", Read("a.txt", [0xEF, 0xBB, 0xBF, .. Utf8("café")]).Text);
        Assert.Equal("café", Read("b.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("café")]).Text);
        Assert.Equal("café", Read("c.txt", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("café")]).Text);
        // Windows-1252: an accented letter and curly quotes, which are not valid UTF-8.
        Assert.Equal("café “quoted”", Read("d.txt", [0x63, 0x61, 0x66, 0xE9, 0x20, 0x93, 0x71, 0x75, 0x6F, 0x74, 0x65, 0x64, 0x94]).Text);
    }

    [Fact]
    public void Binary_files_are_refused_not_turned_into_garbage()
    {
        byte[] noise = new byte[4000];
        new Random(5).NextBytes(noise);
        foreach (string name in new[] { "x.txt", "x.bin", "x.html", "photo.png" })
        {
            ExtractedDocument doc = Read(name, noise);
            Assert.Equal(DocumentStatus.Unsupported, doc.Status);
            Assert.Equal("", doc.Text);
        }
    }

    [Fact]
    public void A_web_page_keeps_its_visible_text()
    {
        ExtractedDocument doc = Read("page.html", Utf8("<html><head><title>t</title><style>p{}</style></head><body><script>steal()</script><h1>Title</h1><p>Fish &amp; chips</p></body></html>"));
        Assert.Equal("html", doc.Kind);
        Assert.Contains("Title", doc.Text);
        Assert.Contains("Fish & chips", doc.Text);
        Assert.DoesNotContain("steal", doc.Text);
        Assert.DoesNotContain("<", doc.Text);
    }

    [Fact]
    public void A_file_as_large_as_the_upload_limit_is_cut_short_not_refused()
    {
        byte[] csv = Utf8(string.Concat(Enumerable.Repeat("id,name,value  \r\n1,widget,12\r\n\r\n\r\n\r\n", 340_000)));
        Assert.True(csv.Length > 10 * 1024 * 1024);
        ExtractedDocument doc = Read("big.csv", csv);
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        Assert.True(doc.Truncated);
        Assert.Equal(200_000, doc.Text.Length);
    }

    [Fact]
    public void Long_text_is_cut_at_the_cap_without_splitting_a_character()
    {
        ExtractedDocument doc = Read("l.txt", Utf8("abcd\U0001F600efgh"), new DocumentLimits(1, 5));
        Assert.True(doc.Truncated);
        Assert.Equal("abcd", doc.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void A_file_with_nothing_in_it_says_so(string content)
        => Assert.Equal(DocumentStatus.NoTextLayer, Read("empty.txt", Utf8(content)).Status);

    [Fact]
    public void The_content_decides_a_pdf_whatever_it_is_called()
        => Assert.Equal("pdf", Read("notes.txt", DocumentFixtures.Pdf("really a pdf")).Kind);

    [Fact]
    public async Task Extraction_runs_off_the_caller_and_can_be_cancelled()
    {
        ExtractedDocument doc = await DocumentExtraction.ExtractAsync("a.txt", Utf8("hello"), Limits, CancellationToken.None);
        Assert.Equal("hello", doc.Text);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentExtraction.ExtractAsync("a.txt", Utf8("hello"), Limits, cancelled.Token));
    }
}
