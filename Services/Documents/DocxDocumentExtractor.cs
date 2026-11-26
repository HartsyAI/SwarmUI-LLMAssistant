using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>Word documents (.docx): a zip of XML, read with the runtime's own readers. The body's paragraphs, headings (as Markdown headings) and tables (a line per row) are kept. Headers,
/// footers, footnotes, comments, text boxes' fallback copies and tracked deletions are not. The XML is read as a stream, with document type definitions refused and a cap on how much it may
/// unpack to, so a small file cannot expand without end.</summary>
public sealed class DocxDocumentExtractor : IDocumentExtractor
{
    /// <summary>The most bytes the document's XML may unpack to.</summary>
    public const long MaxXmlBytes = 40 * 1024 * 1024;

    /// <summary>The most entries a package may hold.</summary>
    private const int MaxEntries = 5000;

    /// <summary>The namespaces of the word processing markup (the usual one and the "strict" one).</summary>
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main", StrictNs = "http://purl.oclc.org/ooxml/wordprocessingml/main";

    /// <inheritdoc/>
    public string Kind => "docx";

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions { get; } = [".docx"];

    /// <inheritdoc/>
    public bool Recognizes(ReadOnlySpan<byte> data) => false;

    /// <inheritdoc/>
    public ExtractedDocument Extract(byte[] data, DocumentLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using MemoryStream file = new(data, false);
            using ZipArchive zip = new(file, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries)
            {
                return ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, "That Word file holds too many parts to read.");
            }
            ZipArchiveEntry body = zip.GetEntry("word/document.xml");
            if (body is null)
            {
                return ExtractedDocument.Failed(DocumentStatus.Malformed, Kind, "That is not a Word document (it has no document body).");
            }
            using Stream unpacked = new BoundedReadStream(body.Open(), MaxXmlBytes);
            (string text, bool cut) = Read(unpacked, limits.MaxChars, ct);
            return DocumentText.Single(Kind, text, "", limits, cut);
        }
        catch (DocumentTooLargeException)
        {
            return ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, "That Word file unpacks to more text than can be read.");
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return ExtractedDocument.Failed(DocumentStatus.Malformed, Kind, "That Word file is damaged or is not really a .docx file.");
        }
    }

    /// <summary>The text of a document body, and whether reading stopped at the character limit.</summary>
    private static (string Text, bool Cut) Read(Stream xml, int maxChars, CancellationToken ct)
    {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
        using XmlReader reader = XmlReader.Create(xml, settings);
        StringBuilder output = new();
        StringBuilder paragraph = new();
        // The open tables, innermost last: the cells of the row being read and the text of the cell being read.
        Stack<(List<string> Row, StringBuilder Cell)> tables = new();
        int heading = 0;
        int tabStops = 0;
        int nodes = 0;
        bool inText = false;
        void EndParagraph()
        {
            string line = paragraph.ToString().Trim();
            paragraph.Clear();
            int level = heading;
            heading = 0;
            if (line.Length == 0)
            {
                return;
            }
            if (tables.Count > 0)
            {
                StringBuilder cell = tables.Peek().Cell;
                cell.Append(cell.Length > 0 ? " " : "").Append(line);
                return;
            }
            output.Append(level > 0 ? new string('#', level) + " " : "").Append(line).Append("\n\n");
        }
        bool more = reader.Read();
        while (more)
        {
            if ((++nodes & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (output.Length + paragraph.Length > maxChars)
            {
                EndParagraph();
                return (output.ToString(), true);
            }
            bool word = reader.NamespaceURI is WordNs or StrictNs;
            if (reader.NodeType == XmlNodeType.Element)
            {
                // The copy kept for older programs of something already read (a text box, say), and text moved elsewhere in tracked changes.
                if (reader.LocalName == "Fallback" || (word && reader.LocalName is "moveFrom" or "del"))
                {
                    reader.Skip();
                    more = !reader.EOF;
                    continue;
                }
                if (word)
                {
                    bool empty = reader.IsEmptyElement;
                    switch (reader.LocalName)
                    {
                        case "t":
                            inText = !empty;
                            break;
                        case "tabs":
                            tabStops += empty ? 0 : 1;
                            break;
                        case "tab" when tabStops == 0:
                            paragraph.Append('\t');
                            break;
                        case "br" or "cr":
                            paragraph.Append('\n');
                            break;
                        case "noBreakHyphen":
                            paragraph.Append('-');
                            break;
                        case "pStyle":
                            heading = Math.Max(heading, HeadingLevel(reader.GetAttribute("val", reader.NamespaceURI)));
                            break;
                        case "outlineLvl" when int.TryParse(reader.GetAttribute("val", reader.NamespaceURI), out int outline) && outline is >= 0 and < 6:
                            heading = Math.Max(heading, outline + 1);
                            break;
                        case "tbl" when !empty:
                            EndParagraph();
                            tables.Push(([], new StringBuilder()));
                            break;
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && word)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        inText = false;
                        break;
                    case "tabs":
                        tabStops = Math.Max(0, tabStops - 1);
                        break;
                    case "p":
                        EndParagraph();
                        break;
                    case "tc" when tables.Count > 0:
                        tables.Peek().Row.Add(tables.Peek().Cell.ToString());
                        tables.Peek().Cell.Clear();
                        break;
                    case "tr" when tables.Count > 0:
                        List<string> row = tables.Peek().Row;
                        if (row.Any(c => c.Length > 0))
                        {
                            string line = string.Join(" | ", row);
                            if (tables.Count == 1)
                            {
                                output.Append(line).Append('\n');
                            }
                            else
                            {
                                // A table inside a cell goes into that cell.
                                (List<string> Row, StringBuilder Cell) inner = tables.Pop();
                                tables.Peek().Cell.Append(' ').Append(line);
                                tables.Push(inner);
                            }
                        }
                        row.Clear();
                        break;
                    case "tbl" when tables.Count > 0:
                        tables.Pop();
                        if (tables.Count == 0)
                        {
                            output.Append('\n');
                        }
                        break;
                }
            }
            else if (inText && reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA)
            {
                paragraph.Append(reader.Value);
            }
            more = reader.Read();
        }
        EndParagraph();
        return (output.ToString(), false);
    }

    /// <summary>The Markdown heading level a paragraph style stands for (Word's own "Title" and "Heading1" to "Heading6"), or 0.</summary>
    private static int HeadingLevel(string style)
    {
        if (string.IsNullOrEmpty(style))
        {
            return 0;
        }
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        return style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style.AsSpan(7), out int level) && level is >= 1 and <= 6 ? level : 0;
    }
}

/// <summary>Thrown when a packed part unpacks to more than is allowed.</summary>
public sealed class DocumentTooLargeException() : IOException("The unpacked data is over the limit.");

/// <summary>A read-only stream that fails once more than a number of bytes have been read from the one it wraps. The size a zip entry declares can be false, so what is counted is what
/// actually comes out.</summary>
public sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
{
    private long Total;

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Total += read;
        return Total > maxBytes ? throw new DocumentTooLargeException() : read;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => Total;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
