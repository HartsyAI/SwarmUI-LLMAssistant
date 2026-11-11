using System.IO;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.LLMs;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Resolves <see cref="LLMMediaAttachment"/>s for delivery to LLM backends.
/// <para>Inbound shape (from saved threads): always <c>{ Type: "url", Data: "View/{userId}/..." }</c>
/// (or the legacy <c>Output/</c> prefix on older saved threads — see <see cref="OutputUrls"/>).</para>
/// <para>Outbound shape needed by backends:</para>
/// <list type="bullet">
/// <item><b>HTTPS URL</b> — pass through as-is (the model can fetch it directly).</item>
/// <item><b>Local <c>Output/</c> path</b> — read the file from <see cref="Program.ServerSettings.Paths.OutputPath"/>
///   and replace with <c>{ Type: "base64", Data: &lt;b64&gt;, MediaType: ... }</c> so the backend
///   can inline it (the model can't reach our local server).</item>
/// </list>
/// <para><b>Video contract.</b> A local <c>View/</c> (or legacy <c>Output/</c>) attachment whose <c>MediaType</c> starts with
/// <c>video/</c> (or, with no <c>MediaType</c>, whose extension is a video one) is NOT base64'd, because a clip can be tens of MB.
/// It is returned as <c>{ Type: "file", Data: &lt;absolute local path&gt;, MediaType: "video/mp4|webm|quicktime" }</c>; the
/// provider reads the file itself (the path is already traversal-checked and size-checked against
/// <see cref="MediaStorageService.MaxVideoBytes"/>; a missing, oversized or escaping file is skipped with a warning).
/// A video that arrives as <c>"base64"</c> or an <c>http(s)</c> <c>"url"</c> passes through unchanged.
/// Providers that only understand images must skip attachments where <see cref="LLMMediaAttachment.IsVideo"/> is true.</para>
/// <para>Resolution happens at LLM-input-build time in <c>ChatEndpoints</c>, so the resolved
/// list lives only as long as the request — no caching needed and no risk of stale content.</para></summary>
public static class MediaResolver
{
    /// <summary>Returns a new list with each attachment resolved for the wire. Null/empty input
    /// yields null. File-read failures degrade to skipping that attachment with a warning log —
    /// better than failing the whole chat request because one image is gone.</summary>
    public static List<LLMMediaAttachment> ResolveForLLM(List<LLMMediaAttachment> attachments)
        => ResolveForLLM(attachments, OutputUrls.ToFullPath);

    /// <summary>The testable core: <paramref name="toFullPath"/> maps a local output URL to a full path (null when it escapes the
    /// output root), so tests do not need the server's output settings.</summary>
    internal static List<LLMMediaAttachment> ResolveForLLM(List<LLMMediaAttachment> attachments, Func<string, string> toFullPath)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return null;
        }
        List<LLMMediaAttachment> resolved = [];
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
            // Local output-relative URL — read off disk, base64.
            if (att.Type == "url" && OutputUrls.IsLocal(att.Data))
            {
                // Defense in depth: ToFullPath returns null on traversal outside the output root.
                // (URLs come from our own upload endpoint so they're trusted, but we shouldn't
                // assume that for all callers.)
                string fullPath = toFullPath(att.Data);
                if (fullPath is null)
                {
                    Logs.Warning($"[LLMAssistant] MediaResolver refused path traversal: {att.Data}");
                    continue;
                }
                if (!File.Exists(fullPath))
                {
                    Logs.Warning($"[LLMAssistant] MediaResolver: referenced media file missing: {fullPath}");
                    continue;
                }
                string mediaType = string.IsNullOrEmpty(att.MediaType) ? ImageInputResolver.GuessMimeFromExt(fullPath) : att.MediaType;
                if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                {
                    // Videos stay on disk: hand the provider the path instead of inflating a clip into a base64 string.
                    long length;
                    try
                    {
                        length = new FileInfo(fullPath).Length;
                    }
                    catch (Exception ex)
                    {
                        Logs.Warning($"[LLMAssistant] MediaResolver: could not stat {fullPath}: {ex.Message}");
                        continue;
                    }
                    if (length > MediaStorageService.MaxVideoBytes)
                    {
                        Logs.Warning($"[LLMAssistant] MediaResolver: skipping video over the {MediaStorageService.MaxVideoBytes / (1024 * 1024)} MB cap: {fullPath}");
                        continue;
                    }
                    resolved.Add(new LLMMediaAttachment { Type = "file", Data = fullPath, MediaType = mediaType });
                    continue;
                }
                try
                {
                    byte[] bytes = File.ReadAllBytes(fullPath);
                    resolved.Add(new LLMMediaAttachment
                    {
                        Type = "base64",
                        Data = Convert.ToBase64String(bytes),
                        MediaType = mediaType
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
}
