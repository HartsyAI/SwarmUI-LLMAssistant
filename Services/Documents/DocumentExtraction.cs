using System.IO;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>The one way a file's text is read: picks the extractor by how the bytes start and then by the file's extension (never by a type the sender claims), runs it away from the
/// request thread with a time limit, and turns every failure into a result with words for the user. Nothing here writes a file or uses the file's name as a path.</summary>
public static class DocumentExtraction
{
    /// <summary>The extractors, in the order they are asked. The last also takes any other file whose content is text.</summary>
    public static readonly IReadOnlyList<IDocumentExtractor> Extractors = [new PdfDocumentExtractor(), new DocxDocumentExtractor(), new HtmlDocumentExtractor(), new TextDocumentExtractor()];

    /// <summary>How long one extraction may take, and how long a request waits for its turn.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>How much longer than its time limit a request waits for an extraction to answer. One that ends itself at the limit, as the PDF worker's host does, so gets to say why,
    /// and to have ended its process, before the request gives up.</summary>
    private static readonly TimeSpan StopMargin = TimeSpan.FromSeconds(15);

    /// <summary>Whose turn it is. A turn ends only when its extraction really does, so one that runs on after its time is up cannot be joined by ever more.</summary>
    private static readonly DocumentGate Gate = new(3);

    /// <summary>How an old binary Office file (and a password-protected newer one) starts.</summary>
    private static readonly byte[] OleStart = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>The extension of a file name, lowercase with the dot, or empty.</summary>
    public static string ExtensionOf(string fileName) => Path.GetExtension((fileName ?? "").Replace('\\', '/')).ToLowerInvariant();

    /// <summary>Reads a file's text here and now. Never throws except when cancelled.</summary>
    public static ExtractedDocument Extract(string fileName, byte[] data, DocumentLimits limits, CancellationToken ct = default)
    {
        if (data is null || data.Length == 0)
        {
            return ExtractedDocument.Failed(DocumentStatus.NoTextLayer, "", "That file is empty.");
        }
        string extension = ExtensionOf(fileName);
        if (data.AsSpan().StartsWith(OleStart) || extension == ".doc")
        {
            return ExtractedDocument.Failed(DocumentStatus.Unsupported, "", "Old Word .doc files and password-protected Office files cannot be read. Save it as .docx without a password, or as PDF.");
        }
        IDocumentExtractor extractor = Extractors.FirstOrDefault(e => e.Recognizes(data))
            ?? Extractors.FirstOrDefault(e => e.Extensions.Contains(extension))
            ?? Extractors[^1];
        try
        {
            return extractor.Extract(data, limits, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Warning($"[LLMAssistant] Reading a {extractor.Kind} file failed: {ex.GetType().Name}: {ex.Message}");
            return ExtractedDocument.Failed(DocumentStatus.Malformed, extractor.Kind, "That file could not be read; it is probably damaged.");
        }
    }

    /// <summary><see cref="Extract"/> on another thread, within <see cref="Timeout"/>. <paramref name="owner"/> (a user's id) has one file read at a time.</summary>
    public static Task<ExtractedDocument> ExtractAsync(string fileName, byte[] data, DocumentLimits limits, CancellationToken ct, string owner = null)
        => ExtractAsync(fileName, data, limits, ct, owner, Timeout);

    /// <summary>The same with another time limit than <see cref="Timeout"/>, so that tests need not wait that long.</summary>
    internal static async Task<ExtractedDocument> ExtractAsync(string fileName, byte[] data, DocumentLimits limits, CancellationToken ct, string owner, TimeSpan timeLimit)
    {
        IDisposable turn = await Gate.EnterAsync(owner, Timeout, ct);
        if (turn is null)
        {
            return ExtractedDocument.Failed(DocumentStatus.TooLarge, "", "The server is busy reading other documents. Try again in a moment.");
        }
        CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ExtractedDocument> work = Task.Run(() => Extract(fileName, data, limits, limit.Token), CancellationToken.None);
        _ = work.ContinueWith(done =>
        {
            _ = done.Exception;
            turn.Dispose();
            limit.Dispose();
        }, TaskScheduler.Default);
        try
        {
            return await work.WaitAsync(timeLimit + StopMargin, ct);
        }
        catch (TimeoutException)
        {
            try
            {
                limit.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            return ExtractedDocument.Failed(DocumentStatus.TooLarge, "", "Reading that file took too long. Split it into smaller parts.");
        }
    }
}
