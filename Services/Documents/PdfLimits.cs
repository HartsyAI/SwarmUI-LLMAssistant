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
/// keep what it is given to work on small: its decoders are bounded, a page's font maps are looked over, and the page is measured, its characters counted without being kept, before it is
/// read. Where they follow the file they do it as PdfPig 0.1.16 does (references, arrays of one, its own tokenizer for character maps), because anything read differently is a way round.
/// <para>These are <b>best effort</b>, against the ways found to make a small file expensive. They are not a guarantee against a hostile PDF: that needs the reading done in a separate
/// process whose memory and time the system limits.</para></summary>
internal static class PdfLimits
{
    /// <summary>The most bytes one packed stream may unpack to.</summary>
    public const long MaxStreamBytes = 16 * 1024 * 1024;

    /// <summary>The most bytes the streams unpacked to read one file's text may come to.</summary>
    public const long MaxUnpackedBytes = 128 * 1024 * 1024;

    /// <summary>The most bytes the checks made before reading may unpack: for one page, and for a file (fonts loaded then are kept and used when the page is read).</summary>
    public const long MaxPageMeasuredBytes = 64 * 1024 * 1024, MaxMeasuredBytes = 512 * 1024 * 1024;

    /// <summary>The most bytes of drawing instructions one page may have: its content streams, each as often as the page lists it, and each form it draws, as often as it draws it.</summary>
    public const long MaxPageContentBytes = 8 * 1024 * 1024;

    /// <summary>The most characters one page may draw.</summary>
    public const int MaxPageGlyphs = 200_000;

    /// <summary>The most forms one page may draw, and the most content streams it may list.</summary>
    public const int MaxPageForms = 2000, MaxPageContentStreams = 1000;

    /// <summary>The most characters one width list or character map may describe by ranges, and all of them in a file, a map counting again for each font that uses it. The parser makes an
    /// entry for every character in a range, so a few bytes can ask for billions. Ordinary files give whole 16-bit ranges (65,536) font after font, so about thirty such lists or maps fit.</summary>
    public const long MaxFontMapEntries = 150_000, MaxFileFontMapEntries = 2_000_000;

    /// <summary>The highest character code a font's width list may name. The parser counts from the first code to the last, and never gets past a last code at the end of the integers.</summary>
    private const double MaxCharacterCode = 0xFFFFFF;

    /// <summary>The most objects the resources of a file's pages may lead to.</summary>
    private const int MaxResourceObjects = 300_000;

    /// <summary>The most references (and arrays of one) followed to reach one thing.</summary>
    private const int MaxReferenceSteps = 1000;

    /// <summary>The most bytes one row of a predicted (PNG or TIFF filtered) stream may be.</summary>
    private const long MaxPredictorRowBytes = 1024 * 1024;

    /// <summary>What a token stands for, found as PdfPig finds it: references are followed however many there are, and where something other than an array is wanted an array of one
    /// stands for what it holds (<paramref name="unwrap"/>). Null when it is not there; throws when the way there is too long to be honest.</summary>
    private static T Find<T>(IToken token, IPdfTokenScanner scanner, bool unwrap = true) where T : class, IToken
    {
        for (int step = 0; step < MaxReferenceSteps; step++)
        {
            if (token is T found)
            {
                return found;
            }
            if (token is IndirectReferenceToken reference)
            {
                token = scanner.Get(reference.Data)?.Data;
            }
            else if (unwrap && token is ArrayToken { Length: 1 } single)
            {
                token = single.Data[0];
            }
            else
            {
                return null;
            }
        }
        throw new DocumentTooLargeException();
    }

    /// <summary>PdfPig's decoders, only the ones whose output can be bounded before they run: flate (measured first), ASCII85 and ASCII hex. LZW, run-length and the picture decoders are
    /// refused when text reading needs one (pictures are never unpacked for text). What they unpack is added up, apart for reading and for the checks before it.</summary>
    public sealed class Filters : IFilterProvider
    {
        private readonly IFilterProvider Inner = DefaultFilterProvider.Instance;

        private long Read, Measured, PageMeasured;

        private bool Measuring, MeasuringPage;

        /// <summary>Whether a limit for the whole file was passed.</summary>
        public bool Exceeded;

        /// <summary>Whether the page being measured passed its limit.</summary>
        public bool PageExceeded;

        /// <summary>The compression that was needed and is not read, or null.</summary>
        public string Refused;

        /// <summary>What is unpacked from now is for a check made before reading.</summary>
        public void StartChecking() => (Measuring, MeasuringPage, PageExceeded) = (true, false, false);

        /// <summary>What is unpacked from now is for measuring one page.</summary>
        public void StartPage() => (Measuring, MeasuringPage, PageMeasured, PageExceeded) = (true, true, 0, false);

        /// <summary>What is unpacked from now is for reading the text.</summary>
        public void StartReading() => (Measuring, MeasuringPage, PageExceeded) = (false, false, false);

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(Inner.GetFilters(dictionary));

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(Inner.GetNamedFilters(names));

        /// <inheritdoc/>
        public IReadOnlyList<IFilter> GetAllFilters() => Wrap(Inner.GetAllFilters());

        private IReadOnlyList<IFilter> Wrap(IReadOnlyList<IFilter> filters) => [.. filters.Select(f => (IFilter)new Bounded(f, this))];

        /// <summary>Records that a limit for the file was passed and makes the error to throw.</summary>
        private DocumentTooLargeException Fail()
        {
            Exceeded = true;
            return new DocumentTooLargeException();
        }

        /// <summary>Adds what a decoder made to the sum it belongs to.</summary>
        private void Count(long bytes)
        {
            if (MeasuringPage && (PageMeasured += bytes) > MaxPageMeasuredBytes)
            {
                PageExceeded = true;
                throw new DocumentTooLargeException();
            }
            if (Measuring ? (Measured += bytes) > MaxMeasuredBytes : (Read += bytes) > MaxUnpackedBytes)
            {
                throw Fail();
            }
        }

        private sealed class Bounded(IFilter inner, Filters owner) : IFilter
        {
            /// <inheritdoc/>
            public bool IsSupported => inner.IsSupported;

            /// <inheritdoc/>
            public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
            {
                if (owner.Exceeded || owner.PageExceeded)
                {
                    throw new DocumentTooLargeException();
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
                owner.Count(result.Length);
                return result;
            }

            /// <summary>How long a row is for a stream filtered through a predictor (0 for none): the decoder makes buffers of that size from numbers in the file. The parameters are the
            /// ones the decoder itself will take.</summary>
            private static double PredictorRowBytes(DictionaryToken dictionary, int filterIndex)
            {
                DictionaryToken parameters = DecodeParameterResolver.GetFilterParameters(dictionary, filterIndex);
                double Number(NameToken name, double fallback) => parameters.TryGet(name, out IToken value) && value is NumericToken number ? Math.Abs(number.Double) : fallback;
                return Number(NameToken.Predictor, 1) <= 1 ? 0
                    : Math.Min(Number(NameToken.Colors, 1), 32) * Number(NameToken.BitsPerComponent, 8) * Number(NameToken.Columns, 1) / 8;
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

    /// <summary>A page's dictionary and the resources the page tree hands down to it, taken without reading the page.</summary>
    public sealed record PageEntry(DictionaryToken Dictionary, IReadOnlyList<DictionaryToken> Inherited);

    /// <summary>Gives a <see cref="PageEntry"/>.</summary>
    public sealed class EntryFactory : IPageFactory<PageEntry>
    {
        /// <inheritdoc/>
        public PageEntry Create(int number, DictionaryToken dictionary, PageTreeMembers pageTreeMembers, NamedDestinations namedDestinations)
            => new(dictionary, [.. pageTreeMembers.ParentResources]);
    }

    /// <summary>The size of the content streams a page lists, each counted every time it is listed. Returns more than the limit as soon as it is passed, when the list is too long, or when
    /// it names something that is not a stream.</summary>
    public static long ContentBytes(DictionaryToken page, IPdfTokenScanner scanner, ILookupFilterProvider decoders, CancellationToken ct)
    {
        if (!page.TryGet(NameToken.Contents, out IToken contents))
        {
            return 0;
        }
        ArrayToken array = Find<ArrayToken>(contents, scanner, false);
        IReadOnlyList<IToken> parts = array?.Data ?? [contents];
        if (parts.Count > MaxPageContentStreams)
        {
            return long.MaxValue;
        }
        long total = 0;
        foreach (IToken part in parts)
        {
            ct.ThrowIfCancellationRequested();
            // In a list the parser takes references only, and passes over anything else.
            if (array is not null && part is not IndirectReferenceToken)
            {
                continue;
            }
            if (Find<StreamToken>(part, scanner) is not StreamToken stream)
            {
                return long.MaxValue;
            }
            total += stream.Decode(decoders, scanner).Length + 1;
            if (total > MaxPageContentBytes)
            {
                break;
            }
        }
        return total;
    }

    /// <summary>What looking over a page's font maps found.</summary>
    public enum FontCheck
    {
        /// <summary>Within the limits.</summary>
        Fit,
        /// <summary>The fonts describe too many characters.</summary>
        TooManyCharacters,
        /// <summary>The resources lead to too many objects to look at.</summary>
        TooManyObjects,
    }

    /// <summary>Looks over the font maps a page's resources lead to (the width lists of composite fonts, and Unicode and character maps) before the page is loaded, since loading its fonts is
    /// what expands them. It is kept for a whole file: what one page shares with another is counted once, as the parser loads it once. Anything but <see cref="FontCheck.Fit"/> ends the
    /// file, because what was passed over then is not looked at again.</summary>
    public sealed class FontMaps(IPdfTokenScanner scanner, ILookupFilterProvider decoders)
    {
        private readonly HashSet<IndirectReference> Seen = [];

        /// <summary>What each character map covers, so one that many fonts use is read once.</summary>
        private readonly Dictionary<object, long> Maps = [];

        private long Entries;

        /// <summary>Looks over what a page's own resources, and those handed down to it, lead to. The structure tree and whatever else no resource refers to are not walked.</summary>
        public FontCheck Check(PageEntry page, CancellationToken ct)
        {
            Stack<IToken> waiting = new(page.Inherited);
            if (page.Dictionary.TryGet(NameToken.Resources, out IToken own))
            {
                waiting.Push(own);
            }
            int looked = 0;
            while (waiting.Count > 0)
            {
                if ((++looked & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
                switch (waiting.Pop())
                {
                    case IndirectReferenceToken reference when Seen.Add(reference.Data):
                        if (Seen.Count > MaxResourceObjects)
                        {
                            return FontCheck.TooManyObjects;
                        }
                        Try(() => Push(waiting, scanner.Get(reference.Data)?.Data));
                        break;
                    case ArrayToken array:
                        foreach (IToken item in array.Data)
                        {
                            Push(waiting, item);
                        }
                        break;
                    case StreamToken stream:
                        waiting.Push(stream.StreamDictionary);
                        break;
                    case DictionaryToken dictionary:
                        foreach (KeyValuePair<string, IToken> entry in dictionary.Data)
                        {
                            long covered = 0;
                            // Every other entry is followed, whatever its name: a resource may be called anything.
                            if (entry.Key is "W" or "W2")
                            {
                                Try(() => covered = Find<ArrayToken>(entry.Value, scanner, false) is ArrayToken widths ? WidthEntries(widths, entry.Key == "W2") : 0);
                            }
                            else if (entry.Key is "ToUnicode" or "Encoding")
                            {
                                Try(() => covered = MapEntries(entry.Value));
                            }
                            if (covered > MaxFontMapEntries || (Entries += covered) > MaxFileFontMapEntries)
                            {
                                return FontCheck.TooManyCharacters;
                            }
                            // A width list holds numbers (and lists of them), with no font further in.
                            if (entry.Key is not ("W" or "W2"))
                            {
                                Push(waiting, entry.Value);
                            }
                        }
                        break;
                }
            }
            return FontCheck.Fit;
        }

        /// <summary>Adds what may lead further to what is waiting to be looked at.</summary>
        private static void Push(Stack<IToken> waiting, IToken token)
        {
            if (token is IndirectReferenceToken or ArrayToken or DictionaryToken or StreamToken)
            {
                waiting.Push(token);
            }
        }

        /// <summary>Runs one look at one thing: a broken object is the reader's to report, if the page needs it, and must not stop the rest being looked at.</summary>
        private static void Try(Action look)
        {
            try
            {
                look();
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or DocumentTooLargeException))
            {
            }
        }

        /// <summary>The characters a composite font's width list covers: "first [widths]" as many as the list holds, "first last values" all from the first code to the last (three items for
        /// <c>W</c>, five for <c>W2</c>). A code outside what a font can have counts as too many.</summary>
        private long WidthEntries(ArrayToken widths, bool vertical)
        {
            long entries = 0;
            for (int i = 0; i + 1 < widths.Length && entries <= MaxFontMapEntries;)
            {
                if (Find<NumericToken>(widths.Data[i], scanner) is not NumericToken first)
                {
                    break;
                }
                IToken second = widths.Data[i + 1];
                if (Find<ArrayToken>(second, scanner, false) is ArrayToken list)
                {
                    if (!IsCode(first.Double))
                    {
                        return MaxFontMapEntries + 1;
                    }
                    entries += vertical ? list.Length / 3 + 1 : list.Length;
                    i += 2;
                    continue;
                }
                if (Find<NumericToken>(second, scanner) is not NumericToken last)
                {
                    break;
                }
                if (!IsCode(first.Double) || !IsCode(last.Double))
                {
                    return MaxFontMapEntries + 1;
                }
                entries += (long)Math.Max(0, last.Double - first.Double + 1);
                i += vertical ? 5 : 3;
            }
            return entries;
        }

        private static bool IsCode(double value) => value is >= 0 and <= MaxCharacterCode;

        /// <summary>The characters the character map a font entry names covers (0 when it names no stream, such as <c>Identity-H</c>).</summary>
        private long MapEntries(IToken value)
        {
            if (Find<StreamToken>(value, scanner) is not StreamToken map)
            {
                return 0;
            }
            object key = value is IndirectReferenceToken named ? named.Data : map;
            if (!Maps.TryGetValue(key, out long covered))
            {
                Maps[key] = covered = PdfLimits.MapEntries(map.Decode(decoders, scanner));
            }
            return covered;
        }
    }

    /// <summary>The characters the <c>bfrange</c> blocks of a character map cover, read with the tokenizer and in the mode the parser reads it with. The parser steps from the first code of a
    /// range until it reaches the last, comparing as many bytes as the first has, so ends of different lengths (or over four bytes) count as too many. Ranges in <c>cidrange</c> blocks are
    /// kept as two numbers by the parser and cost nothing.</summary>
    internal static long MapEntries(Memory<byte> map)
    {
        long entries = 0;
        try
        {
            CoreTokenScanner tokens = new(new MemoryInputBytes(map), false, new StackDepthGuard(256),
                namedDictionaryRequiredKeys: new Dictionary<NameToken, IReadOnlyList<NameToken>> { [NameToken.CidSystemInfo] = [NameToken.Registry, NameToken.Ordering, NameToken.Supplement] },
                isCMapParser: true);
            bool inRanges = false;
            HexToken first = null, last = null;
            while (tokens.MoveNext())
            {
                IToken token = tokens.CurrentToken;
                if (token is OperatorToken word && word.Data is "beginbfrange" or "endbfrange")
                {
                    inRanges = word.Data == "beginbfrange";
                    first = last = null;
                }
                else if (!inRanges || token is CommentToken)
                {
                    continue;
                }
                else if (first is null)
                {
                    first = token as HexToken;
                }
                else if (last is null)
                {
                    last = token as HexToken;
                    first = last is null ? null : first;
                }
                else
                {
                    // The third item says what the range stands for; with it the range is complete.
                    if (first.Bytes.Length != last.Bytes.Length || first.Bytes.Length is 0 or > 4)
                    {
                        return MaxFontMapEntries + 1;
                    }
                    long from = 0, to = 0;
                    for (int i = 0; i < first.Bytes.Length; i++)
                    {
                        from = from * 256 + first.Bytes[i];
                        to = to * 256 + last.Bytes[i];
                    }
                    entries += to >= from ? to - from + 1 : 1;
                    if (entries > MaxFontMapEntries)
                    {
                        break;
                    }
                    first = last = null;
                }
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DocumentTooLargeException))
        {
            // The parser reads it with the same tokenizer, so it gets no further than this either.
        }
        return entries;
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
