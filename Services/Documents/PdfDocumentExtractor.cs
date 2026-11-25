using System.IO;
using System.IO.Compression;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>PDF files: the text layer, read with PdfPig page by page. There is no OCR, so a scanned or image-only PDF is reported as having no text layer, and so is text that cannot be
/// decoded (a font with no text mapping gives characters that mean nothing), rather than handing that on.</summary>
public sealed class PdfDocumentExtractor : IDocumentExtractor
{
    /// <summary>The most bytes one packed stream inside a PDF may unpack to.</summary>
    public const long MaxStreamBytes = 96 * 1024 * 1024;

    /// <summary>The most bytes all the streams read from one PDF may unpack to (a stream used by many pages counts each time).</summary>
    public const long MaxUnpackedBytes = 1024L * 1024 * 1024;

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
    public ExtractedDocument Extract(byte[] data, DocumentLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BoundedFilters filters = new();
        try
        {
            using PdfDocument pdf = PdfDocument.Open(data, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true, FilterProvider = filters });
            return Read(pdf, filters, limits, ct);
        }
        catch (PdfDocumentEncryptedException)
        {
            return ExtractedDocument.Failed(DocumentStatus.Encrypted, Kind, "This PDF needs a password. Save a copy without one and attach that.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return filters.Exceeded ? TooLarge() : ExtractedDocument.Failed(DocumentStatus.Malformed, Kind, "That PDF is damaged or is not really a PDF.");
        }
    }

    /// <summary>The result for a PDF that unpacks to more than is allowed.</summary>
    private ExtractedDocument TooLarge() => ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, "That PDF unpacks to more data than can be read.");

    /// <summary>Reads an opened PDF's pages up to the limits.</summary>
    private ExtractedDocument Read(PdfDocument pdf, BoundedFilters filters, DocumentLimits limits, CancellationToken ct)
    {
        int total = pdf.NumberOfPages;
        int wanted = Math.Min(total, limits.MaxPages);
        List<string> pages = [];
        int chars = 0, blank = 0, failed = 0, unreadable = 0;
        bool cut = false;
        for (int number = 1; number <= wanted; number++)
        {
            ct.ThrowIfCancellationRequested();
            string text = "";
            try
            {
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
            if (filters.Exceeded)
            {
                return TooLarge();
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
        if (wanted < total)
        {
            warnings.Add($"Only the first {wanted} of {total} pages were read.");
        }
        return new ExtractedDocument(DocumentStatus.Ok, Kind, pages, total, cut || wanted < total, warnings, "");
    }

    /// <summary>PdfPig's decoders with a cap on what they may unpack to, so a small file cannot expand until the server is out of memory. A flate stream is measured before it is decoded;
    /// the others are measured after.</summary>
    private sealed class BoundedFilters : IFilterProvider
    {
        private readonly IFilterProvider Inner = DefaultFilterProvider.Instance;

        private long Total;

        /// <summary>Whether a limit was passed.</summary>
        public bool Exceeded;

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(Inner.GetFilters(dictionary));

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(Inner.GetNamedFilters(names));

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetAllFilters() => Wrap(Inner.GetAllFilters());

        private IReadOnlyList<IFilter> Wrap(IReadOnlyList<IFilter> filters) => [.. filters.Select(f => (IFilter)new Bounded(f, this))];

        private sealed class Bounded(IFilter inner, BoundedFilters owner) : IFilter
        {
            /// <inheritdoc/>
            public bool IsSupported => inner.IsSupported;

            /// <inheritdoc/>
            public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
            {
                if (owner.Exceeded || (inner is FlateFilter && InflatedSize(input) > MaxStreamBytes))
                {
                    throw owner.Fail();
                }
                Memory<byte> result = inner.Decode(input, streamDictionary, filterProvider, filterIndex);
                owner.Total += result.Length;
                return result.Length > MaxStreamBytes || owner.Total > MaxUnpackedBytes ? throw owner.Fail() : result;
            }

            /// <summary>How many bytes a flate stream unpacks to, counting no further than just past the limit and keeping none of them. Damaged data counts as far as it reads.</summary>
            private static long InflatedSize(Memory<byte> input)
            {
                if (input.Length <= 2)
                {
                    return 0;
                }
                long size = 0;
                try
                {
                    // Past the two bytes of the zlib header, as the decoder reads it.
                    using DeflateStream deflate = new(new MemoryStream(input[2..].ToArray()), CompressionMode.Decompress);
                    byte[] buffer = new byte[81920];
                    int read;
                    while (size <= MaxStreamBytes && (read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        size += read;
                    }
                }
                catch (InvalidDataException)
                {
                }
                return size;
            }
        }

        /// <summary>Records that a limit was passed and makes the error to throw.</summary>
        private DocumentTooLargeException Fail()
        {
            Exceeded = true;
            return new DocumentTooLargeException();
        }
    }
}
