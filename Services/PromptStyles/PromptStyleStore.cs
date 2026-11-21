using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptStyles;

/// <summary>Reads the styles under one folder: <c>styles/&lt;id&gt;.md</c>. What was read is kept until a file is added, removed or changed (found by comparing each file's size and modified time
/// on the next look, so no watcher is needed), and a folder that does not exist offers nothing. A link (a symlink or junction) is never followed, and a style over
/// <see cref="PromptAsset.MaxFileBytes"/> is skipped. The folder is the same one skills and commands live in, so a scope's styles sit beside its other prompt assets without being read as them.</summary>
public static class PromptStyleStore
{
    /// <summary>The folder (inside a scope's root) the style files are in.</summary>
    public const string FolderName = "styles";

    /// <summary>The most styles read from one folder; the rest are ignored.</summary>
    public const int MaxPerRoot = 100;

    /// <summary>What was read from a folder, and what the folder looked like then.</summary>
    private sealed record Cached(string Fingerprint, List<PromptStyle> Styles);

    /// <summary>The folders read so far.</summary>
    private static readonly ConcurrentDictionary<string, Cached> Cache = new();

    /// <summary>The styles under <paramref name="baseDir"/>, tagged with <paramref name="scope"/>, in id order.</summary>
    public static List<PromptStyle> Load(string baseDir, string scope)
    {
        string folder = string.IsNullOrEmpty(baseDir) ? null : Path.Combine(baseDir, FolderName);
        if (folder is null || !Directory.Exists(baseDir) || PromptAssetStore.IsLink(baseDir) || !Directory.Exists(folder) || PromptAssetStore.IsLink(folder))
        {
            return [];
        }
        string key = scope + "\n" + Path.GetFullPath(folder);
        List<(string Id, string File)> found = Discover(folder);
        string fingerprint = string.Join("|", found.Select(f =>
        {
            FileInfo info = new(f.File);
            return $"{f.Id}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));
        if (Cache.TryGetValue(key, out Cached cached) && cached.Fingerprint == fingerprint)
        {
            return [.. cached.Styles];
        }
        List<PromptStyle> styles = [];
        foreach ((string id, string file) in found)
        {
            PromptStyle style = Read(id, file, scope);
            if (style is not null)
            {
                styles.Add(style);
            }
        }
        Cache[key] = new Cached(fingerprint, styles);
        return [.. styles];
    }

    /// <summary>The style files in <paramref name="folder"/>, as (id, full path).</summary>
    private static List<(string Id, string File)> Discover(string folder)
    {
        List<(string, string)> found = [];
        try
        {
            foreach (string file in Directory.GetFiles(folder, "*.md").OrderBy(f => f, StringComparer.Ordinal))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (PromptStyle.IsValidId(id) && !PromptAssetStore.IsLink(file))
                {
                    found.Add((id, file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logs.Debug($"[LLMAssistant] Could not list prompt styles in {folder}: {ex.Message}");
        }
        return found.Count > MaxPerRoot ? found[..MaxPerRoot] : found;
    }

    /// <summary>Reads one style file, or null when it is too large, cannot be read, or is not a style.</summary>
    private static PromptStyle Read(string id, string file, string scope)
    {
        try
        {
            if (new FileInfo(file).Length > PromptAsset.MaxFileBytes)
            {
                Logs.Debug($"[LLMAssistant] Skipped {file}: over {PromptAsset.MaxFileBytes} bytes.");
                return null;
            }
            return PromptStyleFile.Parse(id, scope, File.ReadAllText(file, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logs.Debug($"[LLMAssistant] Could not read {file}: {ex.Message}");
            return null;
        }
    }
}
