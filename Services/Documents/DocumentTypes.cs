using System.Text.Json.Serialization;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>How reading a document ended.</summary>
public enum DocumentStatus
{
    /// <summary>Text was read.</summary>
    Ok,
    /// <summary>The file holds no text that can be read (a scanned or image-only PDF, fonts with no text mapping, or an empty document). There is no OCR.</summary>
    NoTextLayer,
    /// <summary>The file needs a password.</summary>
    Encrypted,
    /// <summary>The kind of file is not one that can be read.</summary>
    Unsupported,
    /// <summary>The file is damaged, or is not what its name says.</summary>
    Malformed,
    /// <summary>The file is larger than the limit, or unpacks to more than is allowed.</summary>
    TooLarge,
}

/// <summary>What bounds one extraction.</summary>
/// <param name="MaxPages">The most pages read from a document that has pages.</param>
/// <param name="MaxChars">The most characters of text kept; reading stops there and the result says it was cut.</param>
public sealed record DocumentLimits(int MaxPages, int MaxChars);

/// <summary>What was read from a document. A document with pages (a PDF) has one entry in <see cref="Pages"/> for each page read, empty for a page with no text, so a page keeps its number;
/// any other kind has its whole text as the only entry and no page count.</summary>
/// <param name="Status">How reading ended; text is only there for <see cref="DocumentStatus.Ok"/>.</param>
/// <param name="Kind">What kind of document it was read as (<c>pdf</c>, <c>docx</c>, <c>html</c>, <c>text</c>), or empty when none.</param>
/// <param name="Pages">The text, by page.</param>
/// <param name="TotalPages">How many pages the document has, or 0 for a kind without pages.</param>
/// <param name="Truncated">Whether a limit cut the document short, so the text is not all of it.</param>
/// <param name="Warnings">What the user should know about the result (pages with no text, pages left out).</param>
/// <param name="Error">Why reading failed, in words for the user; empty on success.</param>
public sealed record ExtractedDocument(DocumentStatus Status, string Kind, IReadOnlyList<string> Pages, int TotalPages, bool Truncated, IReadOnlyList<string> Warnings, string Error)
{
    /// <summary>The whole text: the pages that have any, a blank line between them.</summary>
    [JsonIgnore]
    public string Text => string.Join("\n\n", Pages.Where(p => p.Length > 0));

    /// <summary>Whether the document was read.</summary>
    [JsonIgnore]
    public bool Success => Status == DocumentStatus.Ok;

    /// <summary>A result that says why there is no text.</summary>
    public static ExtractedDocument Failed(DocumentStatus status, string kind, string error, int totalPages = 0)
        => new(status, kind, [], totalPages, false, [], error);

    /// <summary>The name a status has in API replies.</summary>
    public static string StatusName(DocumentStatus status) => status switch
    {
        DocumentStatus.Ok => "ok",
        DocumentStatus.NoTextLayer => "no_text_layer",
        DocumentStatus.Encrypted => "encrypted",
        DocumentStatus.Unsupported => "unsupported",
        DocumentStatus.TooLarge => "too_large",
        _ => "malformed",
    };
}

/// <summary>Reads the text of one kind of document from its bytes. An implementation holds no state, keeps to the limits it is given, looks at the cancellation token while it works, and reports
/// a file it cannot read as a result rather than by throwing. The text it returns is untrusted: anything can be written in a file.</summary>
public interface IDocumentExtractor
{
    /// <summary>The kind it reads, as results and lists name it.</summary>
    string Kind { get; }

    /// <summary>The file extensions it reads, lowercase, with the dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Whether the bytes start the way this kind of file does, whatever it is called. False for a kind that has no such mark.</summary>
    bool Recognizes(ReadOnlySpan<byte> data);

    /// <summary>The text of a document.</summary>
    ExtractedDocument Extract(byte[] data, DocumentLimits limits, CancellationToken ct);
}
