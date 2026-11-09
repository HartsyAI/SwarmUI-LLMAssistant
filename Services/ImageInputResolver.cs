using System.IO;
using System.Net.Http;
using ImageMagick;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Resolves an image referenced by an LLM tool call (or any code path that takes a
/// "user-supplied image input") into raw bytes + MIME. Accepts a data URI, a local
/// <c>Output/{userId}/...</c> path (traversal-checked), an HTTPS URL (gated by
/// <see cref="NetworkSafety"/> and a size cap), or raw base64. Returns a single shape
/// (<see cref="ResolvedImage"/>) and throws <see cref="InvalidOperationException"/> with a
/// user-facing message on failure.</summary>
public static class ImageInputResolver
{
    private static readonly HttpClient Http = NetworkBackendUtils.MakeHttpClient();

    /// <summary>Hard cap on fetched/decoded image size. Matches the chat-upload cap so all
    /// image-ingest paths have the same ceiling.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>Default request timeout for HTTPS fetches.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Raster formats accepted from any image input. ImageMagick also renders SVG, and an SVG can reference
    /// local files, so every other format is rejected before its pixels are decoded.</summary>
    private static readonly HashSet<MagickFormat> AllowedFormats = [MagickFormat.Png, MagickFormat.Jpeg, MagickFormat.WebP, MagickFormat.Gif, MagickFormat.Bmp, MagickFormat.Bmp2, MagickFormat.Bmp3, MagickFormat.Tiff];

    private const string UnsupportedFormatMessage = "Unsupported image format. Allowed: PNG, JPEG, WebP, GIF, BMP, TIFF.";

    public class ResolvedImage
    {
        public byte[] Bytes;
        public string MimeType;
        /// <summary>Convenience: a data URI form, useful for handing straight to T2IAPI which
        /// accepts both raw base64 and full data URIs.</summary>
        public string DataUri => $"data:{MimeType};base64,{Convert.ToBase64String(Bytes)}";
    }

    /// <summary>Resolves the input string into bytes, then rejects anything that is not an allowed raster format.
    /// See class doc for accepted shapes.</summary>
    public static async Task<ResolvedImage> ResolveAsync(string input, CancellationToken ct = default)
    {
        ResolvedImage resolved = await ResolveSourceAsync(input, ct);
        EnsureRasterImage(resolved.Bytes);
        return resolved;
    }

    private static async Task<ResolvedImage> ResolveSourceAsync(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("Image input is empty.");
        }
        // Data URI
        if (input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = input.IndexOf(',');
            if (commaIdx <= 0)
            {
                throw new InvalidOperationException("Malformed data URI.");
            }
            string header = input[5..commaIdx];
            string mime = header.Split(';')[0];
            byte[] bytes;
            try { bytes = Convert.FromBase64String(input[(commaIdx + 1)..]); }
            catch { throw new InvalidOperationException("Invalid base64 in data URI."); }
            EnsureSize(bytes.Length);
            return new ResolvedImage { Bytes = bytes, MimeType = string.IsNullOrEmpty(mime) ? "image/png" : mime };
        }
        // Local output URL ("View/…", or the legacy "Output/…" still stored in older threads).
        if (OutputUrls.IsLocal(input))
        {
            string fullPath = OutputUrls.ToFullPath(input);
            if (fullPath is null)
            {
                throw new InvalidOperationException("Image path is outside the Output directory.");
            }
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Image not found: {input}");
            }
            byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct);
            EnsureSize(bytes.Length);
            return new ResolvedImage { Bytes = bytes, MimeType = GuessMimeFromExt(fullPath) };
        }
        // HTTPS URL
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out Uri uri))
            {
                throw new InvalidOperationException("Invalid URL.");
            }
            if (NetworkSafety.IsPrivateOrLoopback(uri))
            {
                throw new InvalidOperationException("Refusing to fetch from loopback/private/link-local addresses.");
            }
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);
            using HttpResponseMessage resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Fetch failed with HTTP {(int)resp.StatusCode}.");
            }
            string mime = resp.Content?.Headers?.ContentType?.MediaType ?? "";
            if (!string.IsNullOrEmpty(mime) && !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"URL did not return an image (got {mime}).");
            }
            // Read with size cap. Don't trust Content-Length blindly; cap during the actual read.
            byte[] bytes;
            using (MemoryStream ms = new())
            {
                await using Stream src = await resp.Content.ReadAsStreamAsync(timeout.Token);
                byte[] buffer = new byte[64 * 1024];
                int totalRead = 0;
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                {
                    totalRead += read;
                    if (totalRead > MaxBytes)
                    {
                        throw new InvalidOperationException($"Fetched image exceeds size cap ({MaxBytes} bytes).");
                    }
                    ms.Write(buffer, 0, read);
                }
                bytes = ms.ToArray();
            }
            return new ResolvedImage { Bytes = bytes, MimeType = string.IsNullOrEmpty(mime) ? GuessMimeFromExt(uri.AbsolutePath) : mime };
        }
        // Raw base64? Tolerate as a last resort — assume PNG if we can decode it.
        try
        {
            byte[] bytes = Convert.FromBase64String(input);
            EnsureSize(bytes.Length);
            return new ResolvedImage { Bytes = bytes, MimeType = "image/png" };
        }
        catch
        {
            throw new InvalidOperationException("Unrecognized image input. Expected a data URI, an Output/... path, an https:// URL, or raw base64.");
        }
    }

    /// <summary>Rejects data that is not an allowed raster image. The file signature is checked first so ImageMagick never
    /// inspects any other format: identifying a PDF, for example, launches Ghostscript. ImageMagick then confirms the
    /// format from the header without decoding pixels. Throws <see cref="InvalidOperationException"/> on rejection.</summary>
    private static void EnsureRasterImage(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("Image data is empty.");
        }
        if (!HasRasterSignature(bytes))
        {
            throw new InvalidOperationException(UnsupportedFormatMessage);
        }
        MagickFormat format;
        try
        {
            format = new MagickImageInfo(bytes).Format;
        }
        catch (MagickException ex)
        {
            throw new InvalidOperationException("Image could not be read.", ex);
        }
        if (!AllowedFormats.Contains(format))
        {
            throw new InvalidOperationException(UnsupportedFormatMessage);
        }
    }

    /// <summary>True when the data starts with the signature of PNG, JPEG, GIF, BMP, WebP, or TIFF.</summary>
    private static bool HasRasterSignature(byte[] b)
    {
        if (b.Length < 12)
        {
            return false;
        }
        bool png = b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
        bool jpeg = b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
        bool gif = b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8';
        bool bmp = b[0] == (byte)'B' && b[1] == (byte)'M';
        bool webp = b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';
        bool tiff = (b[0] == (byte)'I' && b[1] == (byte)'I' && b[2] == 0x2A && b[3] == 0) || (b[0] == (byte)'M' && b[1] == (byte)'M' && b[2] == 0 && b[3] == 0x2A);
        return png || jpeg || gif || bmp || webp || tiff;
    }

    private static void EnsureSize(int byteCount)
    {
        if (byteCount > MaxBytes)
        {
            throw new InvalidOperationException($"Image exceeds size cap ({MaxBytes} bytes).");
        }
    }

    /// <summary>Guesses a MIME type from a file extension; defaults to PNG. Shared with
    /// <see cref="MediaResolver"/> so there's one mapping to maintain.</summary>
    public static string GuessMimeFromExt(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
    }
}
