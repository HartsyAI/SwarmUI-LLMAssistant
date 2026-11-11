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
        // SwarmUI's ImageFile handles the data URI decode.
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

    /// <summary>Hard cap on a chat-attached video (decoded bytes). Videos are stored untouched (no decode or resize), so this
    /// bounds disk use and the memory the local engine reads when it decodes the clip.</summary>
    public const int MaxVideoBytes = 64 * 1024 * 1024;

    private static readonly Dictionary<string, string> VideoMimeToExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov"
    };

    /// <summary>Returned shape from a successful video save.</summary>
    public class StoredVideo
    {
        /// <summary>The served URL the browser should use (eg <c>View/{userId}/llm_assistant/uploads/{threadId}/{messageId}.mp4</c>).</summary>
        public string Url;
        /// <summary>Absolute filesystem path. Used by orphan GC and never sent to the client.</summary>
        public string FullPath;
        public string MimeType;
        public int BytesWritten;
    }

    /// <summary>Decodes a video data URI (mp4, webm or mov), validates its container signature and size, and writes it
    /// unchanged to the same per-thread uploads folder images use (so the orphan sweep protects it through
    /// <c>media[].url</c>). Returns null if the input is not a data URI; throws <see cref="InvalidOperationException"/>
    /// with a user-readable message for an unsupported type, an oversized clip or a bad signature.</summary>
    public static async Task<StoredVideo> SaveVideoAsync(User user, string threadId, string messageId, string dataUri, CancellationToken ct = default)
    {
        if (user is null)
        {
            return null;
        }
        StoredVideo stored = await SaveVideoToAsync(Path.Combine(user.OutputDirectory, SubdirRoot), threadId, messageId, dataUri, ct);
        if (stored is not null)
        {
            stored.Url = OutputUrls.ForFullPath(stored.FullPath);
        }
        return stored;
    }

    /// <summary>The host-independent core of <see cref="SaveVideoAsync"/>: writes under
    /// <paramref name="uploadsRoot"/>/{thread}/{message}.{ext} and leaves <see cref="StoredVideo.Url"/> null (building it needs
    /// the server's output root). <paramref name="maxBytes"/> is the decoded-size cap (tests pass a small one).</summary>
    internal static async Task<StoredVideo> SaveVideoToAsync(string uploadsRoot, string threadId, string messageId, string dataUri, CancellationToken ct = default, int maxBytes = MaxVideoBytes)
    {
        if (string.IsNullOrEmpty(dataUri) || !dataUri.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }
        int commaIdx = dataUri.IndexOf(',');
        if (commaIdx <= 0)
        {
            return null;
        }
        string mime = dataUri[5..commaIdx].Split(';')[0].Trim().ToLowerInvariant();
        if (!VideoMimeToExt.TryGetValue(mime, out string ext))
        {
            throw new InvalidOperationException($"Unsupported video type: {(mime.Length == 0 ? "unknown" : mime)}. Allowed: mp4, webm, mov.");
        }
        // Reject on the encoded length before allocating the decoded buffer (base64 is ~4/3 of the raw size).
        long approxBytes = (long)(dataUri.Length - commaIdx - 1) / 4 * 3;
        if (approxBytes > (long)maxBytes + 3)
        {
            throw new InvalidOperationException($"Video too large (over {maxBytes / (1024 * 1024)} MB).");
        }
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dataUri[(commaIdx + 1)..]);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Could not decode video: invalid base64 data.");
        }
        if (bytes.Length > maxBytes)
        {
            throw new InvalidOperationException($"Video too large ({bytes.Length} bytes; max {maxBytes}).");
        }
        if (!HasVideoSignature(bytes, mime))
        {
            throw new InvalidOperationException($"The file is not a valid {ext.TrimStart('.')} video (bad file signature).");
        }
        string safeThread = SanitizeIdComponent(threadId, fallback: "_thread");
        string safeMessage = SanitizeIdComponent(messageId, fallback: $"vid-{Guid.NewGuid():N}");
        string dir = Path.Combine(uploadsRoot, safeThread);
        Directory.CreateDirectory(dir);
        string fullPath = Path.GetFullPath(Path.Combine(dir, safeMessage + ext));
        await File.WriteAllBytesAsync(fullPath, bytes, ct);
        return new StoredVideo { FullPath = fullPath, MimeType = mime, BytesWritten = bytes.Length };
    }

    /// <summary>True when <paramref name="bytes"/> starts like the container <paramref name="mime"/> names: an ISO base media
    /// <c>ftyp</c> box at offset 4 for mp4 and mov, the EBML header <c>1A 45 DF A3</c> for webm.</summary>
    internal static bool HasVideoSignature(byte[] bytes, string mime)
    {
        if (bytes is null || bytes.Length < 12)
        {
            return false;
        }
        if (string.Equals(mime, "video/webm", StringComparison.OrdinalIgnoreCase))
        {
            return bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3;
        }
        if (string.Equals(mime, "video/mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(mime, "video/quicktime", StringComparison.OrdinalIgnoreCase))
        {
            return bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p';
        }
        return false;
    }

    /// <summary>Strict sanitizer: keep alnum, dash, underscore. Falls back to a synthetic id if
    /// nothing usable remains.</summary>
    internal static string SanitizeIdComponent(string raw, string fallback)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }
        string cleaned = new([.. raw.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')]);
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
    }
}
