using System.Text;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Reading each kind of document: the text that comes out, the caps, files with no text, and damaged or hostile files, which are always a result and never an exception.</summary>
public sealed class DocumentExtractorTests
{
    private static readonly DocumentLimits Limits = new(200, 200_000);

    /// <summary>Reads a file. A PDF is read in this process, because these tests are about the parser and what holds it back (and measure what it allocates); <see cref="PdfWorkerTests"/>
    /// covers the separate process the server reads one in.</summary>
    private static ExtractedDocument Read(string name, byte[] data, DocumentLimits limits = null) => data.AsSpan().StartsWith("%PDF-"u8)
        ? new PdfDocumentExtractor().ReadInThisProcess(data, limits ?? Limits, CancellationToken.None)
        : DocumentExtraction.Extract(name, data, limits ?? Limits);

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

    // Small files made to cost far more than their size. 16 MB a stream, 128 MB unpacked, 8 MB of instructions and 200,000 characters a page.

    private const string FontResource = "/Resources << /Font << /F1 5 0 R >> >>";

    private static readonly (string, byte[]) Helvetica = ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>How much reading a file allocates on this thread.</summary>
    private static (ExtractedDocument Result, long Allocated) Cost(byte[] pdf)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        ExtractedDocument result = Read("bomb.pdf", pdf);
        return (result, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void AssertRefusedCheaply(byte[] pdf, DocumentStatus status = DocumentStatus.TooLarge)
    {
        Assert.True(pdf.Length < 2 * 1024 * 1024, $"{pdf.Length}");
        (ExtractedDocument result, long allocated) = Cost(pdf);
        Assert.Equal(status, result.Status);
        Assert.True(allocated < 200 * 1024 * 1024, $"allocated {allocated / 1_000_000} MB");
    }

    [Fact]
    public void A_stream_that_unpacks_past_the_limit_is_refused()
        => AssertRefusedCheaply(DocumentFixtures.RawPdf([new byte[17 * 1024 * 1024]], true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_page_that_lists_one_stream_over_and_over_is_refused(bool flate)
    {
        // Unpacked, the stream is never seen by a decoder; the page would still be built from a hundred copies of it.
        byte[] pad = Ascii(new string(' ', 1_000_000));
        string contents = string.Join(' ', Enumerable.Repeat("4 0 R", 500));
        AssertRefusedCheaply(DocumentFixtures.Objects($"{FontResource} /Contents [{contents}]", (flate ? "/Filter /FlateDecode" : "", flate ? DocumentFixtures.Deflate(pad) : pad), Helvetica));
    }

    [Fact]
    public void A_page_that_lists_too_many_streams_is_refused()
        => AssertRefusedCheaply(DocumentFixtures.Objects($"{FontResource} /Contents [{string.Join(' ', Enumerable.Repeat("4 0 R", 1001))}]", ("", Ascii(" ")), Helvetica));

    [Fact]
    public void A_page_of_millions_of_characters_is_refused_before_they_are_kept()
    {
        byte[] text = Ascii($"BT /F1 1 Tf 10 10 Td ({new string('a', 4_000_000)}) Tj ET");
        AssertRefusedCheaply(DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", ("/Filter /FlateDecode", DocumentFixtures.Deflate(text)), Helvetica));
    }

    [Theory]
    [InlineData("/X Do ", 3000, 100)]
    [InlineData("/X Do ", 1500, 1000)]
    public void A_form_drawn_over_and_over_is_refused(string draw, int times, int letters)
    {
        byte[] form = Ascii($"BT /F1 1 Tf 10 10 Td ({new string('a', letters)}) Tj ET");
        AssertRefusedCheaply(DocumentFixtures.Objects("/Resources << /Font << /F1 5 0 R >> /XObject << /X 6 0 R >> >> /Contents 4 0 R", ("", Ascii(string.Concat(Enumerable.Repeat(draw, times)))), Helvetica,
            ("/Type /XObject /Subtype /Form /BBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >>", form)));
    }

    [Fact]
    public void A_form_drawn_a_few_times_is_read()
    {
        byte[] form = Ascii("BT /F1 12 Tf 10 700 Td (inside the form) Tj ET");
        ExtractedDocument doc = Read("form.pdf", DocumentFixtures.Objects("/Resources << /Font << /F1 5 0 R >> /XObject << /X 6 0 R >> >> /Contents 4 0 R", ("", Ascii("/X Do")), Helvetica,
            ("/Type /XObject /Subtype /Form /BBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >>", form)));
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        Assert.Contains("inside the form", doc.Text);
    }

    [Theory]
    [InlineData("/Filter /LZWDecode")]
    [InlineData("/Filter /RunLengthDecode")]
    public void A_compression_whose_output_cannot_be_bounded_is_not_read(string filter)
        => AssertRefusedCheaply(DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", (filter, [0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01]), Helvetica), DocumentStatus.Unsupported);

    [Fact]
    public void A_predictor_asking_for_enormous_rows_is_refused()
        => AssertRefusedCheaply(DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R",
            ("/Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 2000000000 >>", DocumentFixtures.Deflate(Ascii("BT ET"))), Helvetica));

    /// <summary>A page using a composite font whose descendant has these entries, with this Unicode map.</summary>
    private static byte[] CompositeFont(string descendant, string ranges = "<0041> <0041> <0041>", bool packMap = true)
    {
        byte[] map = Ascii($"/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00000000> <FFFFFFFF> endcodespacerange 1 beginbfrange {ranges} endbfrange endcmap end end");
        return DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", ("", Ascii("BT /F1 12 Tf 10 10 Td <0041> Tj ET")),
            ("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] /ToUnicode 6 0 R >>", null),
            (packMap ? "/Filter /FlateDecode" : "", packMap ? DocumentFixtures.Deflate(map) : map),
            ($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R {descendant} >>", null),
            ("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] >>", null));
    }

    [Theory]
    [InlineData("/W [0 2000000000 500]")]
    [InlineData("/W2 [0 2000000000 500 250 800]")]
    [InlineData("/W [1 [500 500] 10 90000000 500]")]
    public void Font_width_ranges_over_billions_of_characters_are_refused(string widths)
        => AssertRefusedCheaply(CompositeFont(widths));

    [Theory]
    [InlineData("<00000000> <00FFFFFF> <0041>", true)]
    [InlineData("<00000000> <7FFFFFFF> <0041>", false)]
    [InlineData("<00000000> % a comment\n <00FFFFFF> <0041>", false)]
    public void A_character_map_with_enormous_ranges_is_refused(string ranges, bool packed)
        => AssertRefusedCheaply(CompositeFont("", ranges.Replace("\\n", "\n"), packed));

    [Fact]
    public void An_ordinary_composite_font_is_not_refused()
        => Assert.NotEqual(DocumentStatus.TooLarge, Read("cid.pdf", CompositeFont("/W [0 [500] 1 65535 600]", "<0000> <FFFF> <0000>")).Status);

    /// <summary>A page with several composite fonts, each listing widths for every 16-bit character, and each with its own Unicode map of the whole range or all sharing one.</summary>
    private static byte[] ManyFonts(int count, bool sharedMap)
    {
        byte[] map = DocumentFixtures.Deflate(Ascii("begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0000> <FFFF> <0000> endbfrange endcmap"));
        // Object 4 is the content, 5 the descriptor, 6 the shared map; then three objects for each font: the font, its descendant and its own map.
        List<(string, byte[])> objects = [("", Ascii("BT /F0 12 Tf 10 10 Td <0041> Tj ET")), ("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] >>", null), ("/Filter /FlateDecode", map)];
        for (int i = 0; i < count; i++)
        {
            int font = 7 + i * 3;
            objects.Add(($"<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [{font + 1} 0 R] /ToUnicode {(sharedMap ? 6 : font + 2)} 0 R >>", null));
            objects.Add(("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /W [0 65535 500] >>", null));
            objects.Add(("/Filter /FlateDecode", map));
        }
        string fonts = string.Join(' ', Enumerable.Range(0, count).Select(i => $"/F{i} {7 + i * 3} 0 R"));
        return DocumentFixtures.Objects($"/Resources << /Font << {fonts} >> >> /Contents 4 0 R", [.. objects]);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(12, false)]
    [InlineData(12, true)]
    public void A_file_with_several_full_range_fonts_is_read(int count, bool sharedMap)
    {
        ExtractedDocument doc = Read("cjk.pdf", ManyFonts(count, sharedMap));
        Assert.True(doc.Status == DocumentStatus.Ok, $"{doc.Status}: {doc.Error}");
    }

    [Theory]
    [InlineData(40, false)]
    [InlineData(200, true)]
    public void Fonts_without_end_are_refused_even_when_they_share_a_map(int count, bool sharedMap)
        => AssertRefusedCheaply(ManyFonts(count, sharedMap));

    // Ways round the limits: each copies how PdfPig itself follows the file.

    /// <summary>Refused, quickly, and without the cost: for files that would otherwise loop or take gigabytes.</summary>
    private static async Task AssertRefusedSoon(byte[] pdf)
    {
        Task<(ExtractedDocument Result, long Allocated)> reading = Task.Run(() => Cost(pdf));
        // What is refused is refused in well under a second; the wait is long for a slow, loaded machine.
        Assert.True(await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(60))) == reading, "still reading after 60 seconds");
        (ExtractedDocument result, long allocated) = await reading;
        Assert.True(result.Status == DocumentStatus.TooLarge, $"{result.Status}: {result.Error}");
        Assert.True(allocated < 200 * 1024 * 1024, $"allocated {allocated / 1_000_000} MB");
    }

    [Fact]
    public Task A_page_listing_a_stream_through_an_array_of_one_is_counted()
    {
        string contents = string.Join(' ', Enumerable.Repeat("4 0 R", 500));
        return AssertRefusedSoon(DocumentFixtures.Objects($"{FontResource} /Contents [{contents}]", ("[6 0 R]", null), Helvetica, ("", Ascii(new string(' ', 1_000_000)))));
    }

    /// <summary>A composite font page: object 6 is its Unicode map and 7 its descendant; further objects are 9 onward.</summary>
    private static byte[] Composite(string descendant, string map, params (string, byte[])[] more)
        => DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", [("", Ascii("BT /F1 12 Tf 10 10 Td <0041> Tj ET")),
            ("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] /ToUnicode 6 0 R >>", null),
            ("/Filter /FlateDecode", DocumentFixtures.Deflate(Ascii(map))),
            ($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R {descendant} >>", null),
            ("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] >>", null), .. more]);

    private static string Map(string body) => $"/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00000000> <FFFFFFFF> endcodespacerange {body} endcmap end end";

    [Theory]
    // The ends of a range of different lengths: the parser compares only as many bytes as the first end has.
    [InlineData("1 beginbfrange <00000000> <FF> <0041> endbfrange")]
    [InlineData("1 beginbfrange <000000000000> <000000FFFFFF> <0041> endbfrange")]
    // A comment that names the operator, ahead of the real one.
    [InlineData("% beginbfrange <00>\n1 beginbfrange <00000000> <00FFFFFF> <0041> endbfrange")]
    // A literal string that names it.
    [InlineData("/Note (beginbfrange <00>) def 1 beginbfrange <00000000> <00FFFFFF> <0041> endbfrange")]
    // No space between the count and the operator, which a character map may leave out.
    [InlineData("1beginbfrange<00000000><00FFFFFF><0041>endbfrange")]
    public Task A_character_map_written_to_be_miscounted_is_refused(string body) => AssertRefusedSoon(Composite("", Map(body.Replace("\\n", "\n"))));

    [Theory]
    // Codes at the end of the integers: the parser's loop never gets past the last one.
    [InlineData("/W [2147483000 2147483647 500]")]
    [InlineData("/W2 [2147483000 2147483647 500 250 800]")]
    [InlineData("/W [-2147483648 -2147483000 500]")]
    public Task Width_codes_outside_what_a_font_has_are_refused(string widths) => AssertRefusedSoon(Composite(widths, Map("")));

    [Theory]
    // The first code through an array of one; the last through one reference after another.
    [InlineData("/W [9 0 R 90000000 500]", "[10 0 R]", "0")]
    [InlineData("/W [0 9 0 R 500]", "10 0 R", "90000000")]
    public Task Width_codes_reached_the_long_way_are_followed(string widths, string ninth, string tenth)
        => AssertRefusedSoon(Composite(widths, Map(""), (ninth, null), (tenth, null)));

    [Fact]
    public Task A_width_list_used_over_and_over_is_counted_each_time()
        => AssertRefusedSoon(Composite($"/W [{string.Join(' ', Enumerable.Range(0, 400).Select(i => $"{i * 10_000} 9 0 R"))}]", Map(""),
            ($"[{string.Join(' ', Enumerable.Repeat("500", 10_000))}]", null)));

    [Fact]
    public Task Widths_behind_a_long_chain_of_references_are_followed()
    {
        // Objects 9 to 28 each point at the next; the last is the list.
        (string, byte[])[] chain = [.. Enumerable.Range(10, 19).Select(next => ($"{next} 0 R", (byte[])null)), ("[0 90000000 500]", null)];
        return AssertRefusedSoon(Composite("/W 9 0 R", Map(""), chain));
    }

    [Fact]
    public Task Fonts_inherited_from_the_page_tree_are_checked_whatever_the_page_says_its_parent_is()
    {
        // The page's own Parent entry points elsewhere; the parser still takes resources from the node that lists the page.
        byte[] pdf = DocumentFixtures.Document("/Parent 9 0 R /Contents 4 0 R", "", "/Resources << /Font << /F1 5 0 R >> >>", ("", Ascii("BT /F1 12 Tf 10 10 Td <0041> Tj ET")),
            ("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [6 0 R] >>", null),
            ("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 7 0 R /W [0 90000000 500] >>", null),
            ("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] >>", null), ("<< >>", null), ("<< /Type /Pages >>", null));
        return AssertRefusedSoon(pdf);
    }

    [Fact]
    public Task A_font_resource_with_an_unusual_name_is_still_checked()
        => AssertRefusedSoon(DocumentFixtures.Objects("/Resources << /Font << /Parent 5 0 R >> >> /Contents 4 0 R", ("", Ascii("BT /Parent 12 Tf 10 10 Td <0041> Tj ET")),
            ("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [6 0 R] >>", null),
            ("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 7 0 R /W [0 90000000 500] >>", null),
            ("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] >>", null)));

    [Theory]
    [InlineData("/DecodeParms << /Predictor 1 >> /DP << /Predictor 12 /Columns 2000000000 >>")]
    [InlineData("/DecodeParms << /Predictor 12 /Columns 2000000000 /Colors 32 >>")]
    public Task The_predictor_checked_is_the_one_the_decoder_uses(string parameters)
        => AssertRefusedSoon(DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", ($"/Filter /FlateDecode {parameters}", DocumentFixtures.Deflate(Ascii("BT ET"))), Helvetica));

    [Theory]
    [InlineData("/DecodeParms null")]
    [InlineData("/DecodeParms 6 0 R")]
    [InlineData("/DP null")]
    public void Decode_parameters_written_unusually_do_not_refuse_a_good_file(string parameters)
    {
        ExtractedDocument doc = Read("ok.pdf", DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R",
            ($"/Filter /FlateDecode {parameters}", DocumentFixtures.Deflate(Ascii("BT /F1 12 Tf 10 700 Td (plain words) Tj ET"))), Helvetica, ("<< /Predictor 1 >>", null)));
        Assert.True(doc.Status == DocumentStatus.Ok, $"{doc.Status}: {doc.Error}");
        Assert.Contains("plain words", doc.Text);
    }

    [Fact]
    public void A_file_with_a_great_many_objects_outside_its_pages_is_read()
    {
        // A large tagged PDF: its structure tree is hundreds of thousands of objects that no page's text needs.
        const int Many = 320_000;
        (string, byte[])[] objects = [("", Ascii("BT /F1 12 Tf 10 700 Td (tagged words) Tj ET")), Helvetica,
            ($"<< /Type /StructTreeRoot /K [{string.Join(' ', Enumerable.Range(7, Many).Select(n => $"{n} 0 R"))}] >>", null),
            .. Enumerable.Repeat(("<< /Type /StructElem /S /P >>", (byte[])null), Many)];
        ExtractedDocument doc = Read("tagged.pdf", DocumentFixtures.Document($"{FontResource} /Contents 4 0 R", "/StructTreeRoot 6 0 R", "", objects));
        Assert.True(doc.Status == DocumentStatus.Ok, $"{doc.Status}: {doc.Error}");
        Assert.Contains("tagged words", doc.Text);
    }

    /// <summary>A page whose fonts each carry a program that unpacks to this many megabytes.</summary>
    private static byte[] BigFontPrograms(int fonts, int megabytes)
    {
        byte[] program = DocumentFixtures.Deflate(new byte[megabytes * 1024 * 1024]);
        // Object 4 is the content; then three objects for each font: the font, its descriptor and its program.
        List<(string, byte[])> objects = [("", Ascii("BT /F0 12 Tf 10 700 Td (some words) Tj ET"))];
        for (int i = 0; i < fonts; i++)
        {
            int font = 5 + i * 3;
            objects.Add(($"<< /Type /Font /Subtype /TrueType /BaseFont /X /FirstChar 32 /LastChar 32 /Widths [500] /FontDescriptor {font + 1} 0 R >>", null));
            objects.Add(($"<< /Type /FontDescriptor /FontName /X /Flags 32 /FontBBox [0 0 1000 1000] /FontFile2 {font + 2} 0 R >>", null));
            objects.Add(("/Filter /FlateDecode", program));
        }
        string names = string.Join(' ', Enumerable.Range(0, fonts).Select(i => $"/F{i} {5 + i * 3} 0 R"));
        return DocumentFixtures.Objects($"/Resources << /Font << {names} >> >> /Contents 4 0 R", [.. objects]);
    }

    [Fact]
    public void A_page_whose_fonts_unpack_past_what_measuring_may_is_left_out()
    {
        // Eight programs of 12 MB: each within the stream limit, together past the 64 MB one page may unpack while it is measured.
        ExtractedDocument doc = Read("fonts.pdf", BigFontPrograms(8, 12));
        Assert.True(doc.Status == DocumentStatus.TooLarge, $"{doc.Status}: {doc.Error}");
        Assert.Contains("pages of that PDF are too large", doc.Error);
        Assert.Equal(DocumentStatus.Ok, Read("font.pdf", BigFontPrograms(2, 12)).Status);
    }

    private static readonly (string, byte[]) Descriptor = ("<< /Type /FontDescriptor /FontName /X /Flags 4 /FontBBox [0 0 1000 1000] >>", null);

    /// <summary>A composite font naming this object as its descendant.</summary>
    private static (string, byte[]) Type0(int descendant) => ($"<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [{descendant} 0 R] >>", null);

    /// <summary>A descendant font with these width entries.</summary>
    private static (string, byte[]) Descendant(int descriptor, string widths)
        => ($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R {widths} >>", null);

    private static byte[] ShowText(string font) => Ascii($"BT /{font} 12 Tf 10 10 Td <0041> Tj ET");

    [Theory]
    [InlineData("W")]
    [InlineData("W2")]
    public Task A_font_resource_named_like_a_width_list_is_still_checked(string name)
        => AssertRefusedSoon(DocumentFixtures.Objects($"/Resources << /Font << /{name} 5 0 R >> >> /Contents 4 0 R", ("", ShowText(name)),
            Type0(6), Descendant(7, "/W [0 3000000 500]"), Descriptor));

    [Theory]
    [InlineData("W")]
    [InlineData("W2")]
    public Task A_form_named_like_a_width_list_does_not_hide_its_fonts(string name)
        => AssertRefusedSoon(DocumentFixtures.Objects($"/Resources << /XObject << /{name} 5 0 R >> >> /Contents 4 0 R", ("", Ascii($"/{name} Do")),
            ("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /F1 6 0 R >> >>", ShowText("F1")),
            Type0(7), Descendant(8, "/W [0 3000000 500]"), Descriptor));

    /// <summary>A page with many font entries that all end at one descendant with widths for every 16-bit character: each entry its own composite font, another name for one font
    /// (an object that only refers to it), or a font written into the resources.</summary>
    private static byte[] OneDescendant(int entries, string how)
    {
        // Object 4 is the content, 5 the descriptor, 6 the descendant, 7 the first composite font; the others follow.
        List<(string, byte[])> objects = [("", ShowText("F0")), Descriptor, Descendant(5, "/W [0 65535 500]"), Type0(6)];
        List<string> names = [];
        for (int i = 0; i < entries; i++)
        {
            if (how == "direct")
            {
                names.Add($"/F{i} {Type0(6).Item1}");
                continue;
            }
            objects.Add(how == "alias" ? ("7 0 R", null) : Type0(6));
            names.Add($"/F{i} {7 + objects.Count - 4} 0 R");
        }
        return DocumentFixtures.Objects($"/Resources << /Font << {string.Join(' ', names)} >> >> /Contents 4 0 R", [.. objects]);
    }

    [Theory]
    [InlineData("fonts")]
    [InlineData("alias")]
    [InlineData("direct")]
    public Task A_descendant_font_is_counted_for_every_font_that_loads_it(string how) => AssertRefusedSoon(OneDescendant(100, how));

    [Fact]
    public void One_font_under_many_names_is_counted_once()
    {
        string names = string.Join(' ', Enumerable.Range(0, 100).Select(i => $"/F{i} 5 0 R"));
        ExtractedDocument doc = Read("names.pdf", DocumentFixtures.Objects($"/Resources << /Font << {names} >> >> /Contents 4 0 R", ("", ShowText("F0")),
            Type0(6), Descendant(7, "/W [0 65535 500]"), Descriptor));
        Assert.NotEqual(DocumentStatus.TooLarge, doc.Status);
    }

    /// <summary>A page that sets a font through a graphics state this many times.</summary>
    private static byte[] StateFont(int times, int widths = 4000)
        => DocumentFixtures.Objects("/Resources << /Font << /F1 5 0 R >> /ExtGState << /G 6 0 R >> >> /Contents 4 0 R",
            ("", Ascii(string.Concat(Enumerable.Repeat("q /G gs Q ", times)) + "BT /F1 12 Tf 10 700 Td (state words) Tj ET")), Helvetica,
            ("<< /Type /ExtGState /Font [7 0 R 12] >>", null), Type0(8), Descendant(9, $"/W [0 {widths - 1} 500]"), Descriptor);

    [Fact]
    public Task A_font_set_through_a_graphics_state_over_and_over_is_refused() => AssertRefusedSoon(StateFont(2000));

    [Fact]
    public Task The_largest_font_a_graphics_state_may_set_is_loaded_only_a_few_times() => AssertRefusedSoon(StateFont(17, 150_000));

    [Fact]
    public void A_font_set_through_a_graphics_state_a_few_times_is_read()
    {
        ExtractedDocument doc = Read("state.pdf", StateFont(5));
        Assert.True(doc.Status == DocumentStatus.Ok, $"{doc.Status}: {doc.Error}");
        Assert.Contains("state words", doc.Text);
    }

    // References that never end. PdfPig follows many of them by calling itself, and an overflowing stack ends the whole process, test run included.

    private static void AssertDamaged(byte[] pdf)
    {
        ExtractedDocument doc = Read("loop.pdf", pdf);
        Assert.True(doc.Status == DocumentStatus.Malformed, $"{doc.Status}: {doc.Error}");
    }

    /// <summary>A page of words with these entries added to it and these objects as 6 onward.</summary>
    private static byte[] Looping(string page, params string[] objects)
        => DocumentFixtures.Objects($"/Contents 4 0 R {page}", [("", Ascii("BT /F1 12 Tf 10 700 Td (looped words) Tj ET")), Helvetica, .. objects.Select(o => (o, (byte[])null))]);

    [Fact]
    public void A_page_rotation_that_refers_to_itself_is_refused() => AssertDamaged(Looping($"{FontResource} /Rotate 6 0 R", "6 0 R"));

    [Fact]
    public void A_page_tree_entry_that_loops_through_two_objects_is_refused()
        => AssertDamaged(DocumentFixtures.Document("/Contents 4 0 R", "", "/Resources 6 0 R", ("", Ascii("BT ET")), Helvetica, ("7 0 R", null), ("6 0 R", null)));

    [Fact]
    public void A_resource_that_refers_to_itself_is_refused()
        => AssertDamaged(Looping("/Resources << /Font << /F1 5 0 R >> /Properties << /P 6 0 R >> >>", "6 0 R"));

    [Fact]
    public void A_content_list_that_holds_only_itself_is_refused() => AssertDamaged(DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", ("[4 0 R]", null), Helvetica));

    [Fact]
    public void A_media_box_number_that_refers_to_itself_is_refused() => AssertDamaged(Looping($"{FontResource} /CropBox [0 0 6 0 R 800]", "6 0 R"));

    /// <summary>A rotation reached through this many objects that each only refer to the next.</summary>
    private static byte[] Chain(int length)
        => Looping($"{FontResource} /Rotate 6 0 R", [.. Enumerable.Range(7, length).Select(next => $"{next} 0 R"), "0"]);

    [Fact]
    public void A_very_long_chain_of_references_is_refused() => AssertDamaged(Chain(100_000));

    [Fact]
    public void A_short_chain_of_references_is_read()
    {
        ExtractedDocument doc = Read("chain.pdf", Chain(30));
        Assert.True(doc.Status == DocumentStatus.Ok, $"{doc.Status}: {doc.Error}");
        Assert.Contains("looped words", doc.Text);
    }

    [Theory]
    // The decoder keeps the sign, and two negative numbers make a long row.
    [InlineData("/Predictor 12 /Colors -400000 /Columns -1000")]
    [InlineData("/Predictor 12 /Colors 32 /Columns -2000000 /BitsPerComponent -8")]
    [InlineData("/Predictor 12 /Columns 1000 /BitsPerComponent -8")]
    public Task Negative_predictor_numbers_are_refused(string parameters)
        => AssertRefusedSoon(DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", ($"/Filter /FlateDecode /DecodeParms << {parameters} >>", DocumentFixtures.Deflate(Ascii("BT ET"))), Helvetica));

    // Pictures are never unpacked to read text, so how one is packed does not matter.

    [Theory]
    [InlineData("/DCTDecode")]
    [InlineData("/LZWDecode")]
    [InlineData("/RunLengthDecode")]
    [InlineData("/CCITTFaxDecode")]
    [InlineData("/JBIG2Decode")]
    [InlineData("/JPXDecode")]
    public void A_page_that_also_draws_a_picture_is_read_however_the_picture_is_packed(string filter)
    {
        byte[] pdf = DocumentFixtures.Objects("/Resources << /Font << /F1 5 0 R >> /XObject << /Im 6 0 R >> >> /Contents 4 0 R",
            ("", Ascii("q 100 0 0 100 40 500 cm /Im Do Q BT /F1 12 Tf 40 700 Td (words beside a picture) Tj ET")), Helvetica,
            ($"/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter {filter}", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0x80, 0x01]));
        ExtractedDocument doc = Read("picture.pdf", pdf);
        Assert.True(doc.Status == DocumentStatus.Ok, $"{doc.Status}: {doc.Error}");
        Assert.Contains("words beside a picture", doc.Text);
    }

    [Theory]
    [InlineData("/LZW")]
    [InlineData("/RL")]
    [InlineData("/DCT")]
    [InlineData("/CCF")]
    public void A_picture_written_into_the_page_itself_does_not_refuse_it(string filter)
    {
        byte[] content = [.. Ascii($"BI /W 4 /H 4 /CS /G /BPC 8 /F {filter} ID "), 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01, .. Ascii(" EI BT /F1 12 Tf 40 700 Td (words after a picture) Tj ET")];
        ExtractedDocument doc = Read("inline.pdf", DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", ("", content), Helvetica));
        Assert.True(doc.Status == DocumentStatus.Ok, $"{doc.Status}: {doc.Error}");
        Assert.Contains("words after a picture", doc.Text);
    }

    [Fact]
    public void The_heaviest_page_that_is_accepted_costs_a_bounded_amount()
    {
        // Close to every per-page limit at once: nearly 200,000 characters in nearly 8 MB of instructions.
        StringBuilder content = new("BT /F1 1 Tf 10 10 Td ");
        content.Append('(').Append('a', 190_000).Append(") Tj ET ");
        while (content.Length < 7_900_000)
        {
            content.Append("q Q ");
        }
        byte[] pdf = DocumentFixtures.Objects($"{FontResource} /Contents 4 0 R", ("/Filter /FlateDecode", DocumentFixtures.Deflate(Ascii(content.ToString()))), Helvetica);
        (ExtractedDocument doc, long allocated) = Cost(pdf);
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        // Allocated in all while reading it (twice over: once to measure), not held at once.
        Assert.True(allocated < 2500L * 1024 * 1024, $"allocated {allocated / 1_000_000} MB");
        Console.WriteLine($"HEAVIEST allocated {allocated / 1_000_000} MB");
    }

    [Fact]
    public void A_page_over_the_limits_is_left_out_and_the_others_are_read()
    {
        byte[] heavy = Ascii($"BT /F1 1 Tf 10 10 Td ({new string('a', 300_000)}) Tj ET");
        byte[] pdf = DocumentFixtures.RawPdf([DocumentFixtures.Lines("first page"), heavy, DocumentFixtures.Lines("third page")], true);
        ExtractedDocument doc = Read("mixed.pdf", pdf);
        Assert.Equal(DocumentStatus.Ok, doc.Status);
        Assert.True(doc.Truncated);
        Assert.Equal(["first page", "", "third page"], doc.Pages);
        Assert.Contains(doc.Warnings, w => w.Contains("too large"));
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
    {
        ExtractedDocument doc = DocumentExtraction.Extract("notes.txt", DocumentFixtures.Pdf("really a pdf"), Limits);
        Assert.Equal("pdf", doc.Kind);
        Assert.Contains("really a pdf", doc.Text);
    }

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
