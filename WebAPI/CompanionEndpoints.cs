using System.IO;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Endpoints supporting the floating Companion overlay (the in-page Clippy-style helper).
/// Kept separate from <see cref="ChatEndpoints"/> so this surface stays small and easy to gate
/// behind <see cref="LLMAssistantAPI.PermCompanion"/>. Companion chat itself reuses the existing
/// streaming WS endpoint with <c>instructionId="companion"</c> — only metadata helpers live here.</summary>
public static class CompanionEndpoints
{
    /// <summary>Real image extensions the Companion will surface as "your last image". Excludes
    /// the broader set <see cref="T2IAPI.HistoryExtensions"/> covers (audio, video, html, animated
    /// formats) — the critique flow only makes sense for still images. Animated webp/gif could
    /// arguably belong here later, but vision models reliably handle still frames; leaving them
    /// out keeps the first-frame ambiguity out of v1.</summary>
    private static readonly HashSet<string> CompanionImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "webp" };

    /// <summary>Returns the user's most-recently-generated image (URL + sidecar metadata) for
    /// the Companion's "Critique my last image" button. Reuses the standard image-history scan
    /// (date sort, depth=4 to catch dated/model subfolders) and picks the first still image.
    /// <para>Returns <c>{ success: true, lastImage: null }</c> when the user has no generations
    /// yet — the Companion uses that as a friendly empty state, not an error.</para></summary>
    public static async Task<JObject> LLMAssistantGetCompanionContext(Session session)
    {
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Authentication required." };
        }
        try
        {
            // Date sort, ascending=false yields newest first (see T2IAPI sort comparator).
            // Depth=4 covers typical {model}/{date}/img.png and {date}/{batch}/img.png layouts
            // without scanning forever on installs with deep custom output trees.
            JObject listResult = await SwarmUI.WebAPI.T2IAPI.ListImages(session, "", depth: 4, sortBy: "Date", sortReverse: false);
            if (listResult["error"] is JToken err)
            {
                return new JObject { ["success"] = false, ["error"] = err.ToString() };
            }
            if (listResult["files"] is not JArray files || files.Count == 0)
            {
                return new JObject { ["success"] = true, ["lastImage"] = null };
            }
            // Skip non-image entries (audio/video/html the standard scan also returns).
            JObject firstImage = null;
            foreach (JToken token in files)
            {
                if (token is not JObject obj) { continue; }
                string srcCheck = obj["src"]?.ToString();
                if (string.IsNullOrEmpty(srcCheck)) { continue; }
                string ext = srcCheck.AfterLast('.');
                if (!CompanionImageExtensions.Contains(ext)) { continue; }
                firstImage = obj;
                break;
            }
            if (firstImage is null)
            {
                return new JObject { ["success"] = true, ["lastImage"] = null };
            }
            string src = firstImage["src"]?.ToString();
            string metadata = firstImage["metadata"]?.ToString();
            // Build the canonical served URL so MediaResolver can resolve it the same way it
            // resolves URLs from LLMAssistantUploadChatImage. The user's OutputDirectory is the
            // per-user subdir; combining with the listing's `src` gives the on-disk path, which
            // OutputUrls re-relativizes against the global OutputPath.
            string userOutAbs = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, session.User.OutputDirectory);
            string fullDiskPath = Path.GetFullPath(Path.Combine(userOutAbs, src));
            string url;
            if (fullDiskPath.StartsWith(Services.OutputUrls.Root, StringComparison.OrdinalIgnoreCase))
            {
                url = Services.OutputUrls.ForFullPath(fullDiskPath);
            }
            else
            {
                // Output dir is configured outside the standard root — MediaResolver only handles
                // paths under OutputPath, so we can't surface a working URL. Returning the src
                // alone lets the UI still show metadata, and the JS can surface a "couldn't load
                // image" hint instead of failing the whole flow.
                Logs.Debug($"[LLMAssistant] Companion context: user output dir is outside server output path; image URL not available.");
                url = null;
            }
            string mediaType = GuessImageMime(src);
            return new JObject
            {
                ["success"] = true,
                ["lastImage"] = new JObject
                {
                    ["src"] = src,
                    ["url"] = url,
                    ["mediaType"] = mediaType,
                    ["metadata"] = metadata
                }
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] GetCompanionContext failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = "Could not read image history." };
        }
    }

    /// <summary>Guesses a MIME type from a file's extension, defaulting to <c>image/png</c>.</summary>
    private static string GuessImageMime(string path)
    {
        if (string.IsNullOrEmpty(path)) { return "image/png"; }
        string ext = path.AfterLast('.').ToLowerInvariant();
        return ext switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "gif" => "image/gif",
            _ => "image/png"
        };
    }
}
