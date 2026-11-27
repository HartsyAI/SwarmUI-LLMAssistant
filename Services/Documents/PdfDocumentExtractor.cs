using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>PDF files: the text layer, read with PdfPig page by page. There is no OCR, so a scanned or image-only PDF is reported as having no text layer, and so is text that cannot be
/// decoded (a font with no text mapping gives characters that mean nothing), rather than handing that on. The parser runs in a process of its own (<see cref="PdfWorkerHost"/>), which is
/// what keeps a hostile file from harming the server. There it is also held to <see cref="PdfLimits"/>; a page over them is left out.</summary>
public sealed class PdfDocumentExtractor : IDocumentExtractor
{
    /// <summary>The share of a page's characters that may be unreadable before the page's text is not used.</summary>
    private const double MaxUnreadableShare = 0.1;

    /// <summary>What a PDF with no text is told.</summary>
    public const string NoTextMessage = "This PDF has no text layer (it looks like scanned pages or pictures). Text in pictures cannot be read, because there is no OCR. Attach it as images to a model that can see, or use a copy that has real text.";

    /// <inheritdoc/>
    public string Kind => "pdf";

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions { get; } = [".pdf"];

    /// <inheritdoc/>
    public bool Recognizes(ReadOnlySpan<byte> data) => data.StartsWith("%PDF-"u8);

    /// <inheritdoc/>
    public ExtractedDocument Extract(byte[] data, DocumentLimits limits, CancellationToken ct) => PdfWorkerHost.Shared.Read(data, limits, ct);

    /// <summary><see cref="Extract"/> in this process, with nothing to contain a file that crashes or exhausts the parser. For <see cref="PdfWorker"/> and tests only: the server must
    /// never call it.</summary>
    internal ExtractedDocument ReadInThisProcess(byte[] data, DocumentLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PdfLimits.Filters filters = new();
        try
        {
            using PdfDocument pdf = PdfDocument.Open(data, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true, FilterProvider = filters });
            pdf.AddPageFactory(new PdfLimits.EntryFactory());
            pdf.AddPageFactory<PdfLimits.PageMeasure, PdfLimits.MeasureFactory>();
            return Read(pdf, filters, limits, ct);
        }
        catch (PdfDocumentEncryptedException)
        {
            return ExtractedDocument.Failed(DocumentStatus.Encrypted, Kind, "This PDF needs a password. Save a copy without one and attach that.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Stopped(filters) ?? (ex is DocumentTooLargeException
                ? ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, "That PDF is built in a way that would take too much to read.")
                : ExtractedDocument.Failed(DocumentStatus.Malformed, Kind, "That PDF is damaged or is not really a PDF."));
        }
    }

    /// <summary>The result for a PDF whose reading cannot go on (it unpacks to too much, or needs a compression that is not read), or null.</summary>
    private ExtractedDocument Stopped(PdfLimits.Filters filters)
        => filters.Exceeded ? ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, "That PDF unpacks to more data than can be read.")
        : filters.Refused is string refused ? ExtractedDocument.Failed(DocumentStatus.Unsupported, Kind, $"That PDF uses the {refused} compression, which is not read. Save it again with another program (printing it to a new PDF usually works).")
        : null;

    /// <summary>Whether a page may be read, found out without building it: first the font maps it leads to (a refusal there ends the file), then its content.</summary>
    private (bool Fits, ExtractedDocument Refusal) Check(PdfDocument pdf, PdfLimits.Filters filters, PdfLimits.FontMaps fonts, int number, CancellationToken ct)
    {
        try
        {
            filters.StartChecking();
            PdfLimits.PageEntry page = pdf.GetPage<PdfLimits.PageEntry>(number);
            PdfLimits.FontCheck maps;
            try
            {
                maps = fonts.Check(page, ct);
            }
            catch (DocumentTooLargeException)
            {
                maps = PdfLimits.FontCheck.TooManyCharacters;
            }
            if (Stopped(filters) is ExtractedDocument stopped)
            {
                return (false, stopped);
            }
            if (maps != PdfLimits.FontCheck.Fit)
            {
                string reason = maps == PdfLimits.FontCheck.TooManyObjects ? "The pages of that PDF use too many objects to check."
                    : "The fonts in that PDF describe more characters than can be read.";
                return (false, ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, reason));
            }
            filters.StartPage();
            bool fits;
            try
            {
                fits = PdfLimits.ContentBytes(page.Dictionary, pdf.Structure.TokenScanner, pdf.Structure.FilterProvider, ct) <= PdfLimits.MaxPageContentBytes
                    && pdf.GetPage<PdfLimits.PageMeasure>(number).Within && !filters.PageExceeded;
            }
            catch (DocumentTooLargeException)
            {
                fits = false;
            }
            return (fits, Stopped(filters));
        }
        finally
        {
            filters.StartReading();
        }
    }

    /// <summary>Reads an opened PDF's pages up to the limits.</summary>
    private ExtractedDocument Read(PdfDocument pdf, PdfLimits.Filters filters, DocumentLimits limits, CancellationToken ct)
    {
        int total = pdf.NumberOfPages;
        int wanted = Math.Min(total, limits.MaxPages);
        List<string> pages = [];
        int chars = 0, blank = 0, failed = 0, unreadable = 0, large = 0;
        bool cut = false;
        // First of all, before a page is asked for: a reference that never ends would take this process down with it.
        filters.StartChecking();
        PdfLimits.ReferenceCheck references = PdfLimits.CheckReferences(pdf.Structure.Catalog.CatalogDictionary, pdf.Structure.TokenScanner, ct);
        filters.StartReading();
        if (Stopped(filters) is ExtractedDocument unreadableObjects)
        {
            return unreadableObjects;
        }
        if (references == PdfLimits.ReferenceCheck.Endless)
        {
            return ExtractedDocument.Failed(DocumentStatus.Malformed, Kind, "That PDF is damaged: it has references that lead round in circles or too far to follow.", total);
        }
        if (references == PdfLimits.ReferenceCheck.TooManyObjects)
        {
            return ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, "The pages of that PDF use too many objects to check.", total);
        }
        PdfLimits.FontMaps fonts = new(pdf.Structure.TokenScanner, pdf.Structure.FilterProvider);
        for (int number = 1; number <= wanted; number++)
        {
            ct.ThrowIfCancellationRequested();
            string text = "";
            try
            {
                (bool fits, ExtractedDocument refusal) = Check(pdf, filters, fonts, number, ct);
                if (refusal is not null)
                {
                    return refusal;
                }
                if (!fits)
                {
                    large++;
                    pages.Add("");
                    continue;
                }
                text = ContentOrderTextExtractor.GetText(pdf.GetPage(number), true);
                text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                if (DocumentText.IsUnreadable(text, MaxUnreadableShare))
                {
                    unreadable++;
                    text = "";
                }
                else if (!text.Any(char.IsLetterOrDigit))
                {
                    blank++;
                    text = "";
                }
                else
                {
                    // Stray codes in otherwise readable text are dropped.
                    text = new string([.. text.Where(c => c is '\n' or '\t' || (!char.IsControl(c) && c != '\uFFFD' && c is not (>= '\uE000' and <= '\uF8FF')))]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                failed++;
            }
            if (Stopped(filters) is ExtractedDocument over)
            {
                return over;
            }
            if (chars + text.Length > limits.MaxChars)
            {
                text = DocumentText.Cut(text, limits.MaxChars - chars);
                cut = true;
            }
            chars += text.Length;
            pages.Add(text);
            if (cut)
            {
                break;
            }
        }
        if (chars == 0)
        {
            if (large > 0)
            {
                return ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, "The pages of that PDF are too large to read (too many characters or drawing instructions).", total);
            }
            if (wanted == 0 || failed == pages.Count)
            {
                return ExtractedDocument.Failed(DocumentStatus.Malformed, Kind, "No page of that PDF could be read; it is probably damaged.", total);
            }
            string reason = unreadable > 0
                ? "The text in this PDF cannot be decoded (its fonts do not say which characters they draw), and there is no OCR."
                : NoTextMessage;
            return ExtractedDocument.Failed(DocumentStatus.NoTextLayer, Kind, wanted < total ? $"{reason} Only the first {wanted} of {total} pages were looked at." : reason, total);
        }
        List<string> warnings = [];
        if (blank + unreadable > 0)
        {
            warnings.Add($"{blank + unreadable} of {pages.Count} pages have no readable text (scanned or picture pages are not read).");
        }
        if (failed > 0)
        {
            warnings.Add($"{failed} page(s) could not be read.");
        }
        if (large > 0)
        {
            warnings.Add($"{large} page(s) were too large to read and were left out.");
        }
        if (wanted < total)
        {
            warnings.Add($"Only the first {wanted} of {total} pages were read.");
        }
        return new ExtractedDocument(DocumentStatus.Ok, Kind, pages, total, cut || wanted < total || large > 0, warnings, "");
    }
}
