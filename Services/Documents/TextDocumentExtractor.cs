namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>Plain text files: notes, Markdown, CSV, JSON, logs and source code, read as they are.</summary>
public sealed class TextDocumentExtractor : IDocumentExtractor
{
    /// <inheritdoc/>
    public string Kind => "text";

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions { get; } =
    [
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".log", ".xml", ".yaml", ".yml", ".toml", ".ini", ".rst", ".tex",
        ".js", ".ts", ".py", ".cs", ".java", ".go", ".rs", ".c", ".cpp", ".h", ".sh", ".sql",
    ];

    /// <inheritdoc/>
    public bool Recognizes(ReadOnlySpan<byte> data) => false;

    /// <inheritdoc/>
    public ExtractedDocument Extract(byte[] data, DocumentLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string text = DocumentText.Decode(data);
        return text is null
            ? ExtractedDocument.Failed(DocumentStatus.Unsupported, Kind, "That does not look like a text file. Documents must be PDF, Word (.docx), plain text, Markdown, HTML, CSV, JSON or code.")
            // No file name: text is kept as written (nothing is treated as a web page).
            : DocumentText.Single(Kind, text, "", limits);
    }
}

/// <summary>Web pages saved as files: the visible text, without scripts, styles and tags.</summary>
public sealed class HtmlDocumentExtractor : IDocumentExtractor
{
    /// <inheritdoc/>
    public string Kind => "html";

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions { get; } = [".html", ".htm"];

    /// <inheritdoc/>
    public bool Recognizes(ReadOnlySpan<byte> data) => false;

    /// <inheritdoc/>
    public ExtractedDocument Extract(byte[] data, DocumentLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string text = DocumentText.Decode(data);
        if (text is null)
        {
            return ExtractedDocument.Failed(DocumentStatus.Unsupported, Kind, "That does not look like a web page: the file is not text.");
        }
        // A page is larger than its text, but not by a factor that makes a very large file worth parsing (the knowledge library refuses the same).
        if (text.Length > Math.Max(limits.MaxChars, 100_000) * 8L)
        {
            return ExtractedDocument.Failed(DocumentStatus.TooLarge, Kind, $"That web page is too long to read ({text.Length:N0} characters).");
        }
        return DocumentText.Single(Kind, text, "page.html", limits);
    }
}
