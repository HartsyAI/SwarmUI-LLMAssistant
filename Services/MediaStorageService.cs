using System.IO;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Persists chat-attached images to the calling user's per-user output directory and
/// returns a served URL (rather than storing base64 in the thread blob, which would bloat every
/// settings/thread read-write and can't be browser-cached like a URL). Writes go to
/// <c>{user.OutputDirectory}/llm_assistant/uploads/{threadId}/...</c> so SwarmUI's
/// <c>/Output/{*Path}</c> route auth-gates retrieval automatically. Reuses <see cref="ImageFile"/>
/// for parsing/resize/format conversion.</summary>
public static class MediaStorageService
{
    /// <summary>Subdir under user's OutputDirectory where chat uploads live, partitioned by thread.</summary>
    public const string SubdirRoot = "llm_assistant/uploads";

    /// <summary>Hard cap on raw upload size (decoded). Larger uploads are rejected.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>Long-edge cap. Images larger than this are downscaled to keep the LLM payload
    /// reasonable and avoid blowing past provider per-request limits. Aspect ratio preserved.</summary>
    public const int MaxDimension = 1536;

    private static readonly Dictionary<string, string> MimeToExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"]  = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"]  = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"]  = ".gif"
    };

    /// <summary>Returned shape from a successful save.</summary>
    public class StoredImage
    {
        /// <summary>The Output-relative URL the browser should use (eg <c>Output/{userId}/llm_assistant/uploads/{threadId}/{messageId}.png</c>).</summary>
        public string Url;
        /// <summary>Absolute filesystem path. Used by orphan GC and never sent to the client.</summary>
        public string FullPath;
        public string MimeType;
        public int BytesWritten;
        public int Width;
        public int Height;
    }

    /// <summary>Returns the absolute root of all uploads for this user (every thread).</summary>
    public static string GetAllUploadsRoot(User user)
    {
        return Path.GetFullPath(Path.Combine(user.OutputDirectory, SubdirRoot));
    }

    /// <summary>Decodes a data URI, optionally resizes if larger than <see cref="MaxDimension"/>
    /// on the long edge, and writes to disk. Returns null if the input is malformed; throws on
    /// IO errors so the caller can surface a clean error.</summary>
    public static async Task<StoredImage> SaveDataUriAsync(User user, string threadId, string messageId, string dataUri, CancellationToken ct = default, int maxDimension = MaxDimension)
    {
        // Clamp the caller-supplied cap into a sane range; 0/negative → the default.
        int cap = maxDimension <= 0 ? MaxDimension : Math.Clamp(maxDimension, 256, 2048);
        if (user is null || string.IsNullOrEmpty(dataUri))
        {
            return null;
        }
        if (!dataUri.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }
        // Parse data URI for MIME first — quick reject before decode.
        int commaIdx = dataUri.IndexOf(',');
        if (commaIdx <= 0)
        {
            return null;
        }
        string header = dataUri[5..commaIdx];
        string mime = header.Split(';')[0];
        if (!MimeToExt.TryGetValue(mime, out string ext))
        {
            throw new InvalidOperationException($"Unsupported image MIME: {mime}. Allowed: png, jpg, webp, gif.");
        }
        // SwarmUI's ImageFile handles the data URI decode + ImageSharp wrapping.
        ImageFile img;
        try
        {
            img = ImageFile.FromDataString(dataUri);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not decode image: {ex.Message}");
        }
        if (img.RawData.Length > MaxBytes)
        {
            throw new InvalidOperationException($"Image too large ({img.RawData.Length} bytes; max {MaxBytes}).");
        }
        // Cap to the configured long edge, preserving aspect ratio. No-op if already small.
        (int origW, int origH) = img.GetResolution();
        if (origW > cap || origH > cap)
        {
            double scale = (double)cap / Math.Max(origW, origH);
            int newW = Math.Max(1, (int)Math.Round(origW * scale));
            int newH = Math.Max(1, (int)Math.Round(origH * scale));
            img = img.Resize(newW, newH);
            // Resize() always returns PNG output; reflect that in the saved extension/MIME.
            ext = ".png";
            mime = "image/png";
        }
        // Sanitize id components (UUIDs are safe, but be defensive against caller-supplied values).
        string safeThread = SanitizeIdComponent(threadId, fallback: "_thread");
        string safeMessage = SanitizeIdComponent(messageId, fallback: $"img-{Guid.NewGuid():N}");
        string dir = Path.Combine(user.OutputDirectory, SubdirRoot, safeThread);
        Directory.CreateDirectory(dir);
        string fullPath = Path.GetFullPath(Path.Combine(dir, safeMessage + ext));
        await File.WriteAllBytesAsync(fullPath, img.RawData, ct);
        (int finalW, int finalH) = img.GetResolution();
        return new StoredImage
        {
            Url = OutputUrls.ForFullPath(fullPath),
            FullPath = fullPath,
            MimeType = mime,
            BytesWritten = img.RawData.Length,
            Width = finalW,
            Height = finalH
        };
    }

    /// <summary>Strict sanitizer: keep alnum, dash, underscore. Falls back to a synthetic id if
    /// nothing usable remains.</summary>
    private static string SanitizeIdComponent(string raw, string fallback)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }
        string cleaned = new([.. raw.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')]);
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
    }
}
