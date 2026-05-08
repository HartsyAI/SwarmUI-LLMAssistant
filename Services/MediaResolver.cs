using System.IO;
using SwarmUI.Core;
using SwarmUI.LLMs;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Resolves <see cref="LLMMediaAttachment"/>s for delivery to LLM backends.
/// <para>Inbound shape (from saved threads): always <c>{ Type: "url", Data: "Output/{userId}/..." }</c>.</para>
/// <para>Outbound shape needed by backends:</para>
/// <list type="bullet">
/// <item><b>HTTPS URL</b> — pass through as-is (the model can fetch it directly).</item>
/// <item><b>Local <c>Output/</c> path</b> — read the file from <see cref="Program.ServerSettings.Paths.OutputPath"/>
///   and replace with <c>{ Type: "base64", Data: &lt;b64&gt;, MediaType: ... }</c> so the backend
///   can inline it (the model can't reach our local server).</item>
/// </list>
/// <para>Resolution happens at LLM-input-build time in <c>ChatEndpoints</c>, so the resolved
/// list lives only as long as the request — no caching needed and no risk of stale content.</para></summary>
public static class MediaResolver
{
    /// <summary>Returns a new list with each attachment resolved for the wire. Null/empty input
    /// yields null. File-read failures degrade to skipping that attachment with a warning log —
    /// better than failing the whole chat request because one image is gone.</summary>
    public static List<LLMMediaAttachment> ResolveForLLM(List<LLMMediaAttachment> attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return null;
        }
        List<LLMMediaAttachment> resolved = [];
        string outputRoot = Path.GetFullPath(Program.ServerSettings.Paths.OutputPath);
        foreach (LLMMediaAttachment att in attachments)
        {
            if (att is null || string.IsNullOrEmpty(att.Data))
            {
                continue;
            }
            // Already base64 or external HTTPS URL — backend can use as-is.
            if (att.Type == "base64"
                || (att.Type == "url" && (att.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                          || att.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))))
            {
                resolved.Add(att);
                continue;
            }
            // Local Output-relative URL — read off disk, base64.
            if (att.Type == "url" && att.Data.StartsWith("Output/", StringComparison.OrdinalIgnoreCase))
            {
                string rel = att.Data["Output/".Length..];
                string fullPath = Path.GetFullPath(Path.Combine(outputRoot, rel));
                // Defense in depth: stay inside outputRoot. (URLs come from our own upload
                // endpoint so they're trusted, but we shouldn't assume that for all callers.)
                if (!fullPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Logs.Warning($"[LLMAssistant] MediaResolver refused path traversal: {att.Data}");
                    continue;
                }
                if (!File.Exists(fullPath))
                {
                    Logs.Warning($"[LLMAssistant] MediaResolver: referenced media file missing: {fullPath}");
                    continue;
                }
                try
                {
                    byte[] bytes = File.ReadAllBytes(fullPath);
                    resolved.Add(new LLMMediaAttachment
                    {
                        Type = "base64",
                        Data = Convert.ToBase64String(bytes),
                        MediaType = string.IsNullOrEmpty(att.MediaType) ? GuessMimeFromExt(fullPath) : att.MediaType
                    });
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[LLMAssistant] MediaResolver: could not read {fullPath}: {ex.Message}");
                }
            }
            else
            {
                Logs.Warning($"[LLMAssistant] MediaResolver: unrecognized attachment shape Type='{att.Type}' Data='{att.Data?[..Math.Min(40, att.Data.Length)]}...'");
            }
        }
        return resolved.Count == 0 ? null : resolved;
    }

    private static string GuessMimeFromExt(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/png"
        };
    }
}
