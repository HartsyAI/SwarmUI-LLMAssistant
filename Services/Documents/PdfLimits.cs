using System.IO;
using System.IO.Compression;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Geometry;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.PdfFonts;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>PdfPig builds every character of a page in memory before any text can be looked at, and a small file can be made to unpack, repeat or draw the same bytes millions of times. These
/// keep what it is given to work on small: its decoders are bounded, and a page is measured, and its characters counted without being kept, before it is read.</summary>
internal static class PdfLimits
{
    /// <summary>The most bytes one packed stream may unpack to.</summary>
    public const long MaxStreamBytes = 16 * 1024 * 1024;

    /// <summary>The most bytes the streams read from one file may unpack to in all.</summary>
    public const long MaxUnpackedBytes = 128 * 1024 * 1024;

    /// <summary>The most bytes of drawing instructions one page may have: its content streams, each as often as the page lists it, and each form it draws, as often as it draws it.</summary>
    public const long MaxPageContentBytes = 8 * 1024 * 1024;

    /// <summary>The most characters one page may draw.</summary>
    public const int MaxPageGlyphs = 200_000;

    /// <summary>The most forms one page may draw, and the most content streams it may list.</summary>
    public const int MaxPageForms = 2000, MaxPageContentStreams = 1000;

    /// <summary>The most characters one width list or character map may describe by ranges, and all of them in a file, a map counting again for each font that uses it. The parser makes an
    /// entry for every character in a range, so a few bytes can ask for billions. Ordinary files give whole 16-bit ranges (65,536) font after font, so about thirty such lists or maps fit.</summary>
    public const long MaxFontMapEntries = 150_000, MaxFileFontMapEntries = 2_000_000;

    /// <summary>The most objects looked at when checking a file's font maps.</summary>
    private const int MaxObjects = 300_000;

    /// <summary>The most bytes one row of a predicted (PNG or TIFF filtered) stream may be.</summary>
    private const long MaxPredictorRowBytes = 1024 * 1024;

    /// <summary>PdfPig's decoders, only the ones whose output can be bounded before they run: flate (measured first), ASCII85 and ASCII hex. LZW, run-length and the picture decoders are
    /// refused when text reading needs one (pictures are never unpacked for text).</summary>
    public sealed class Filters : IFilterProvider
    {
        private readonly IFilterProvider Inner = DefaultFilterProvider.Instance;

        private long Total;

        /// <summary>Whether what is unpacked now counts toward <see cref="MaxUnpackedBytes"/>. Off while a page is only being measured, since reading it unpacks the same again.</summary>
        public bool Metering = true;

        /// <summary>Whether a size limit was passed.</summary>
        public bool Exceeded;

        /// <summary>The compression that was needed and is not read, or null.</summary>
        public string Refused;

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(Inner.GetFilters(dictionary));

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(Inner.GetNamedFilters(names));

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetAllFilters() => Wrap(Inner.GetAllFilters());

        private IReadOnlyList<IFilter> Wrap(IReadOnlyList<IFilter> filters) => [.. filters.Select(f => (IFilter)new Bounded(f, this))];

        /// <summary>Records that a limit was passed and makes the error to throw.</summary>
        private DocumentTooLargeException Fail()
        {
            Exceeded = true;
            return new DocumentTooLargeException();
        }

        private sealed class Bounded(IFilter inner, Filters owner) : IFilter
        {
            /// <inheritdoc/>
            public bool IsSupported => inner.IsSupported;

            /// <inheritdoc/>
            public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
            {
                if (owner.Exceeded)
                {
                    throw owner.Fail();
                }
                switch (inner)
                {
                    case FlateFilter:
                        if (PredictorRowBytes(streamDictionary, filterIndex) > MaxPredictorRowBytes || InflatedSize(input) > MaxStreamBytes)
                        {
                            throw owner.Fail();
                        }
                        break;
                    case Ascii85Filter:
                        // A "z" stands for four bytes; everything else shrinks.
                        if (input.Length + 3L * input.Span.Count((byte)'z') > MaxStreamBytes)
                        {
                            throw owner.Fail();
                        }
                        break;
                    case AsciiHexDecodeFilter:
                        break;
                    default:
                        owner.Refused = inner.GetType().Name.Replace("DecodeFilter", "").Replace("Filter", "");
                        throw new NotSupportedException($"The {owner.Refused} compression is not read.");
                }
                Memory<byte> result = inner.Decode(input, streamDictionary, filterProvider, filterIndex);
                if (result.Length > MaxStreamBytes)
                {
                    throw owner.Fail();
                }
                if (owner.Metering && (owner.Total += result.Length) > MaxUnpackedBytes)
                {
                    throw owner.Fail();
                }
                return result;
            }

            /// <summary>How long a row is for a stream filtered through a predictor (0 for none): the decoder makes buffers of that size from numbers in the file.</summary>
            private static double PredictorRowBytes(DictionaryToken dictionary, int filterIndex)
            {
                foreach (NameToken key in new[] { NameToken.DecodeParms, NameToken.Dp })
                {
                    if (!dictionary.TryGet(key, out IToken token))
                    {
                        continue;
                    }
                    DictionaryToken parameters = token as DictionaryToken ?? (token is ArrayToken list && filterIndex < list.Length ? list.Data[filterIndex] as DictionaryToken : null);
                    // Parameters that cannot be looked at here (a reference to them) are taken as too large rather than trusted.
                    if (parameters is null)
                    {
                        return token is ArrayToken ? 0 : double.MaxValue;
                    }
                    double Number(string name, double fallback)
                        => !parameters.Data.TryGetValue(name, out IToken value) ? fallback : value is NumericToken number ? Math.Abs(number.Double) : double.MaxValue;
                    return Number("Predictor", 1) <= 1 ? 0 : Number("Columns", 1) * Number("Colors", 1) * Number("BitsPerComponent", 8) / 8;
                }
                return 0;
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
    }

    /// <summary>Whether the ranges in every font map the document can reach (the width lists of composite fonts, and the Unicode and character maps) stay within
    /// <see cref="MaxFontMapEntries"/>. Looked at before any page is, since loading a page's fonts is what expands them.</summary>
    public static bool FontMapsFit(DictionaryToken catalog, IPdfTokenScanner scanner, ILookupFilterProvider decoders, CancellationToken ct)
    {
        Stack<IToken> waiting = new();
        HashSet<IndirectReference> seen = [];
        // What each character map covers, so one that many fonts use is read once.
        Dictionary<object, long> maps = [];
        long entries = 0;
        int looked = 0;
        waiting.Push(catalog);
        while (waiting.Count > 0)
        {
            if ((++looked & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (looked > MaxObjects * 4)
            {
                return false;
            }
            IToken token = waiting.Pop();
            switch (token)
            {
                case IndirectReferenceToken reference when seen.Add(reference.Data):
                    if (seen.Count > MaxObjects)
                    {
                        return false;
                    }
                    try
                    {
                        if (scanner.Get(reference.Data)?.Data is IToken found)
                        {
                            waiting.Push(found);
                        }
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or DocumentTooLargeException))
                    {
                        // A broken object is the reader's to report, if a page needs it.
                    }
                    break;
                case ArrayToken array:
                    foreach (IToken item in array.Data)
                    {
                        waiting.Push(item);
                    }
                    break;
                case StreamToken stream:
                    waiting.Push(stream.StreamDictionary);
                    break;
                case DictionaryToken dictionary:
                    foreach (KeyValuePair<string, IToken> entry in dictionary.Data)
                    {
                        long covered = 0;
                        if (entry.Key is "W" or "W2" && Resolve(entry.Value, scanner) is ArrayToken widths)
                        {
                            covered = WidthEntries(widths, entry.Key == "W" ? 3 : 5, scanner);
                        }
                        else if (entry.Key is "ToUnicode" or "Encoding" && Resolve(entry.Value, scanner) is StreamToken map)
                        {
                            object key = entry.Value is IndirectReferenceToken named ? named.Data : map;
                            if (!maps.TryGetValue(key, out covered))
                            {
                                maps[key] = covered = MapEntries(map.Decode(decoders, scanner).Span);
                            }
                        }
                        if (covered > MaxFontMapEntries)
                        {
                            return false;
                        }
                        entries += covered;
                        waiting.Push(entry.Value);
                    }
                    if (entries > MaxFileFontMapEntries)
                    {
                        return false;
                    }
                    break;
            }
        }
        return true;
    }

    /// <summary>The characters the ranges of a composite font's width list cover. The list holds "first [widths]", which is as long as what it describes, or "first last values..."
    /// (<paramref name="rangeLength"/> items), which is not.</summary>
    private static long WidthEntries(ArrayToken widths, int rangeLength, IPdfTokenScanner scanner)
    {
        long entries = 0;
        for (int i = 0; i + 1 < widths.Length;)
        {
            IToken second = Resolve(widths.Data[i + 1], scanner);
            if (second is ArrayToken)
            {
                i += 2;
                continue;
            }
            if (Resolve(widths.Data[i], scanner) is NumericToken first && second is NumericToken last)
            {
                entries += (long)Math.Clamp(last.Double - first.Double + 1, 0, MaxFontMapEntries + 1);
                i += rangeLength;
            }
            else
            {
                i++;
            }
            if (entries > MaxFontMapEntries)
            {
                break;
            }
        }
        return entries;
    }

    /// <summary>The characters the ranges of a character map (<c>bfrange</c> and <c>cidrange</c> blocks) cover.</summary>
    internal static long MapEntries(ReadOnlySpan<byte> map)
    {
        string text = System.Text.Encoding.Latin1.GetString(map);
        long entries = 0;
        foreach (string kind in new[] { "bfrange", "cidrange" })
        {
            int at = 0;
            while ((at = text.IndexOf("begin" + kind, at, StringComparison.Ordinal)) >= 0)
            {
                at += 5 + kind.Length;
                int end = text.IndexOf("end" + kind, at, StringComparison.Ordinal);
                // The tokens of the block in threes: the first and last code of a range, and what they stand for.
                int position = 0;
                long first = -1;
                foreach (string token in MapTokens(text, at, end < 0 ? text.Length : end))
                {
                    if (position == 0)
                    {
                        first = HexValue(token);
                    }
                    else if (position == 1 && first >= 0 && HexValue(token) is long last && last >= first)
                    {
                        entries += Math.Min(last - first + 1, MaxFontMapEntries + 1);
                        if (entries > MaxFontMapEntries)
                        {
                            return entries;
                        }
                    }
                    position = (position + 1) % 3;
                }
                at = end < 0 ? text.Length : end;
            }
        }
        return entries;
    }

    /// <summary>The tokens of part of a character map: hex strings, whole arrays and words, without comments.</summary>
    private static IEnumerable<string> MapTokens(string text, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            char c = text[i];
            if (c == '%')
            {
                while (i < end && text[i] is not ('\n' or '\r'))
                {
                    i++;
                }
                continue;
            }
            if (char.IsWhiteSpace(c) || c == '\0')
            {
                i++;
                continue;
            }
            int from = i;
            if (c is '<' or '[')
            {
                int close = text.IndexOf(c == '<' ? '>' : ']', i + 1, end - i - 1);
                i = close < 0 ? end : close + 1;
            }
            else
            {
                i++;
                while (i < end && !char.IsWhiteSpace(text[i]) && text[i] is not ('<' or '[' or '%' or '\0'))
                {
                    i++;
                }
            }
            yield return text[from..i];
        }
    }

    /// <summary>The number a hex string stands for (-1 when it is not one; a very long one counts as very large).</summary>
    private static long HexValue(string token)
    {
        if (token.Length < 2 || token[0] != '<' || token[^1] != '>')
        {
            return -1;
        }
        long value = 0;
        foreach (char c in token.AsSpan(1, token.Length - 2))
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            if (!char.IsAsciiHexDigit(c))
            {
                return -1;
            }
            value = Math.Min(value * 16 + (c <= '9' ? c - '0' : (c | 32) - 'a' + 10), long.MaxValue / 32);
        }
        return value;
    }

    /// <summary>A page's dictionary, taken without reading the page.</summary>
    public sealed record PageEntry(DictionaryToken Dictionary);

    /// <summary>Gives a page's dictionary without reading its content.</summary>
    public sealed class EntryFactory : IPageFactory<PageEntry>
    {
        /// <inheritdoc/>
        public PageEntry Create(int number, DictionaryToken dictionary, PageTreeMembers pageTreeMembers, NamedDestinations namedDestinations) => new(dictionary);
    }

    /// <summary>The size of the content streams a page lists, each counted every time it is listed. Stops, returning more than the limit, as soon as the limit is passed or the list is
    /// too long.</summary>
    public static long ContentBytes(DictionaryToken page, IPdfTokenScanner scanner, ILookupFilterProvider decoders, CancellationToken ct)
    {
        if (!page.TryGet(NameToken.Contents, out IToken contents))
        {
            return 0;
        }
        contents = Resolve(contents, scanner);
        IReadOnlyList<IToken> parts = contents is ArrayToken array ? array.Data : [contents];
        if (parts.Count > MaxPageContentStreams)
        {
            return long.MaxValue;
        }
        long total = 0;
        foreach (IToken part in parts)
        {
            ct.ThrowIfCancellationRequested();
            if (Resolve(part, scanner) is StreamToken stream)
            {
                total += stream.Decode(decoders, scanner).Length + 1;
                if (total > MaxPageContentBytes)
                {
                    break;
                }
            }
        }
        return total;
    }

    /// <summary>What a reference points at (followed a few steps), or the token itself.</summary>
    private static IToken Resolve(IToken token, IPdfTokenScanner scanner)
    {
        for (int depth = 0; token is IndirectReferenceToken reference && depth < 8; depth++)
        {
            token = scanner.Get(reference.Data)?.Data;
        }
        return token;
    }

    /// <summary>Whether drawing a page stays within the limits.</summary>
    public sealed record PageMeasure(bool Within);

    /// <summary>Goes through a page's instructions as reading it would, forms included, counting and keeping nothing.</summary>
    public sealed class MeasureFactory(IPdfTokenScanner pdfScanner, IResourceStore resourceStore, ILookupFilterProvider filterProvider, IPageContentParser pageContentParser, ParsingOptions parsingOptions)
        : BasePageFactory<PageMeasure>(pdfScanner, resourceStore, filterProvider, pageContentParser, parsingOptions)
    {
        /// <inheritdoc/>
        protected override PageMeasure ProcessPage(int pageNumber, DictionaryToken dictionary, NamedDestinations namedDestinations, MediaBox mediaBox, CropBox cropBox,
            UserSpaceUnit userSpaceUnit, PageRotationDegrees rotation, TransformationMatrix initialMatrix, IReadOnlyList<IGraphicsStateOperation> operations)
        {
            Meter meter = new(pageNumber, ResourceStore, PdfScanner, PageContentParser, FilterProvider, cropBox, userSpaceUnit, rotation, initialMatrix, ParsingOptions);
            return meter.Process(pageNumber, operations);
        }
    }

    /// <summary>The processor behind <see cref="MeasureFactory"/>.</summary>
    private sealed class Meter(int pageNumber, IResourceStore resourceStore, IPdfTokenScanner pdfScanner, IPageContentParser pageContentParser, ILookupFilterProvider filterProvider,
        CropBox cropBox, UserSpaceUnit userSpaceUnit, PageRotationDegrees rotation, TransformationMatrix initialMatrix, ParsingOptions parsingOptions)
        : BaseStreamProcessor<PageMeasure>(pageNumber, resourceStore, pdfScanner, pageContentParser, filterProvider, cropBox, userSpaceUnit, rotation, in initialMatrix, parsingOptions)
    {
        private long FormBytes;

        private int Glyphs, Forms;

        private bool Over;

        public override PageMeasure Process(int pageNumberCurrent, IReadOnlyList<IGraphicsStateOperation> operations)
        {
            CloneAllStates();
            ProcessOperations(operations);
            return new PageMeasure(!Over);
        }

        /// <summary>Notes that the page is over a limit and stops what is being processed.</summary>
        private DocumentTooLargeException Stop()
        {
            Over = true;
            return new DocumentTooLargeException();
        }

        public override void RenderGlyph(IFont font, CurrentGraphicsState currentState, double fontSize, double pointSize, int code, string unicode, long currentOffset,
            in TransformationMatrix renderingMatrix, in TransformationMatrix textMatrix, in TransformationMatrix transformationMatrix, CharacterBoundingBox characterBoundingBox)
        {
            if (Over || ++Glyphs > MaxPageGlyphs)
            {
                throw Stop();
            }
        }

        protected override void ProcessFormXObject(StreamToken formStream, NameToken xObjectName)
        {
            if (Over || ++Forms > MaxPageForms || (FormBytes += formStream.Decode(FilterProvider, PdfScanner).Length) > MaxPageContentBytes)
            {
                throw Stop();
            }
            base.ProcessFormXObject(formStream, xObjectName);
        }

        protected override void RenderXObjectImage(XObjectContentRecord xObjectContentRecord)
        {
        }

        protected override void RenderInlineImage(InlineImage inlineImage)
        {
        }

        protected override void ClipToRectangle(PdfRectangle rectangle, FillingRule clippingRule)
        {
        }

        public override void BeginSubpath()
        {
        }

        public override PdfPoint? CloseSubpath() => null;

        public override void StrokePath(bool close)
        {
        }

        public override void FillPath(FillingRule fillingRule, bool close)
        {
        }

        public override void FillStrokePath(FillingRule fillingRule, bool close)
        {
        }

        public override void MoveTo(double x, double y)
        {
        }

        public override void BezierCurveTo(double x2, double y2, double x3, double y3)
        {
        }

        public override void BezierCurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
        }

        public override void LineTo(double x, double y)
        {
        }

        public override void Rectangle(double x, double y, double width, double height)
        {
        }

        public override void EndPath()
        {
        }

        public override void ClosePath()
        {
        }

        public override void ModifyClippingIntersect(FillingRule clippingRule)
        {
        }

        public override void BeginMarkedContent(NameToken name, NameToken propertyDictionaryName, DictionaryToken properties)
        {
        }

        public override void EndMarkedContent()
        {
        }

        public override void PaintShading(NameToken shadingName)
        {
        }
    }
}
