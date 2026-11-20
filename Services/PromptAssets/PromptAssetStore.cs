using System.Collections.Concurrent;
using System.IO;
using System.Text;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptAssets;

/// <summary>Reads the assets under one folder: skills as <c>skills/&lt;name&gt;/SKILL.md</c> and commands as <c>commands/&lt;name&gt;.md</c>. What was read is
/// kept until a file is added, removed or changed (found by comparing each file's size and modified time on the next look, so no watcher is needed), and a
/// folder that does not exist offers nothing. A link (a symlink or junction) is never followed, and an asset over <see cref="PromptAsset.MaxFileBytes"/> is skipped.</summary>
public static class PromptAssetStore
{
    /// <summary>The most assets read from one folder (skills and commands together); the rest are ignored.</summary>
    public const int MaxPerRoot = 100;

    /// <summary>The file a skill's text is in.</summary>
    public const string SkillFile = "SKILL.md";

    /// <summary>The most other files of a skill listed to the model.</summary>
    public const int MaxListedFiles = 50;

    /// <summary>What was read from a folder, and what the folder looked like then.</summary>
    private sealed record Cached(string Fingerprint, List<PromptAsset> Assets);

    /// <summary>The folders read so far.</summary>
    private static readonly ConcurrentDictionary<string, Cached> Cache = new();

    /// <summary>The assets under <paramref name="baseDir"/>, tagged with <paramref name="scope"/>, skills then commands, each in name order.</summary>
    public static List<PromptAsset> Load(string baseDir, string scope)
    {
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir) || IsLink(baseDir))
        {
            return [];
        }
        string key = scope + "\n" + Path.GetFullPath(baseDir);
        List<(string Kind, string Name, string File)> found = Discover(baseDir);
        string fingerprint = string.Join("|", found.Select(f =>
        {
            FileInfo info = new(f.File);
            return $"{f.Kind}/{f.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));
        if (Cache.TryGetValue(key, out Cached cached) && cached.Fingerprint == fingerprint)
        {
            return [.. cached.Assets];
        }
        List<PromptAsset> assets = [];
        foreach ((string kind, string name, string file) in found)
        {
            PromptAsset asset = Read(kind, name, file, scope);
            if (asset is not null)
            {
                assets.Add(asset);
            }
        }
        Cache[key] = new Cached(fingerprint, assets);
        return [.. assets];
    }

    /// <summary>Whether <paramref name="path"/> is a symlink or junction.</summary>
    internal static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The asset files under <paramref name="baseDir"/>, as (kind, name, full path).</summary>
    private static List<(string Kind, string Name, string File)> Discover(string baseDir)
    {
        List<(string, string, string)> found = [];
        try
        {
            string skills = Path.Combine(baseDir, "skills");
            if (Directory.Exists(skills) && !IsLink(skills))
            {
                foreach (string dir in Directory.GetDirectories(skills).OrderBy(d => d, StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(dir);
                    string file = Path.Combine(dir, SkillFile);
                    if (PromptAsset.IsValidName(name) && !IsLink(dir) && File.Exists(file) && !IsLink(file))
                    {
                        found.Add((PromptAsset.KindSkill, name, file));
                    }
                }
            }
            string commands = Path.Combine(baseDir, "commands");
            if (Directory.Exists(commands) && !IsLink(commands))
            {
                foreach (string file in Directory.GetFiles(commands, "*.md").OrderBy(f => f, StringComparer.Ordinal))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (PromptAsset.IsValidName(name) && !IsLink(file))
                    {
                        found.Add((PromptAsset.KindCommand, name, file));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logs.Debug($"[LLMAssistant] Could not list prompt assets in {baseDir}: {ex.Message}");
        }
        return found.Count > MaxPerRoot ? found[..MaxPerRoot] : found;
    }

    /// <summary>Reads one asset file, or null when it is too large or cannot be read.</summary>
    private static PromptAsset Read(string kind, string name, string file, string scope)
    {
        try
        {
            if (new FileInfo(file).Length > PromptAsset.MaxFileBytes)
            {
                Logs.Debug($"[LLMAssistant] Skipped {file}: over {PromptAsset.MaxFileBytes} bytes.");
                return null;
            }
            (Dictionary<string, string> meta, string body) = PromptAssetFile.Parse(File.ReadAllText(file, Encoding.UTF8));
            string description = meta.GetValueOrDefault("description") ?? "";
            if (description.Length == 0)
            {
                description = body.Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            }
            return new PromptAsset(kind, name, description, meta.GetValueOrDefault("argument-hint") ?? "", body, scope,
                kind == PromptAsset.KindSkill ? Path.GetDirectoryName(file) : null, PromptAssetFile.IsTrue(meta, "disable-model-invocation"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logs.Debug($"[LLMAssistant] Could not read {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The other files in a skill's folder (relative paths with <c>/</c>, up to three levels down), so the model knows what it may ask for.</summary>
    public static List<string> ListSkillFiles(PromptAsset skill)
    {
        List<string> files = [];
        if (skill?.Directory is null || !Directory.Exists(skill.Directory))
        {
            return files;
        }
        try
        {
            CollectFiles(skill.Directory, skill.Directory, 0, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logs.Debug($"[LLMAssistant] Could not list the files of skill {skill.Name}: {ex.Message}");
        }
        return files;
    }

    /// <summary>Adds the files under <paramref name="dir"/> to <paramref name="files"/>, skipping links and <see cref="SkillFile"/>, down to three levels and <see cref="MaxListedFiles"/> files.</summary>
    private static void CollectFiles(string root, string dir, int depth, List<string> files)
    {
        foreach (string file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (files.Count >= MaxListedFiles)
            {
                return;
            }
            if (relative != SkillFile && !IsLink(file))
            {
                files.Add(relative);
            }
        }
        if (depth >= 2)
        {
            return;
        }
        foreach (string sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!IsLink(sub) && files.Count < MaxListedFiles)
            {
                CollectFiles(root, sub, depth + 1, files);
            }
        }
    }
}
