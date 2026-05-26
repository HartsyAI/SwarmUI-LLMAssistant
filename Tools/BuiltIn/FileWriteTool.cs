using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: write a text file into the calling user's per-user sandbox under
/// <c>{user.OutputDirectory}/llm_assistant/</c>. The path lives inside SwarmUI's <c>/Output/</c>
/// route, which already enforces per-user authorization via <c>WebServer.ViewOutput</c>.
/// <para>The extension whitelist is the safe default set unioned with any extras the user has
/// added via the tool's per-user config (<c>extraExtensions</c>).</para></summary>
public class FileWriteTool : ToolHandler
{
    public override string HandlerId => ToolConstants.FileWrite;

    public const string ConfigExtraExtensions = "extraExtensions";

    /// <summary>Subdirectory of the user's output folder where this tool writes.</summary>
    public const string SandboxSubdir = "llm_assistant";

    /// <summary>Always-allowed extensions. Tuned to safe text/data formats; the user can extend
    /// this set per-account via tool config but cannot remove these.</summary>
    private static readonly HashSet<string> DefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".json", ".txt", ".yaml", ".yml", ".csv", ".log"
    };

    /// <summary>Returns the absolute filesystem root of the calling user's sandbox.
    /// Used by both this tool's writes and the orphan-file GC sweep.</summary>
    public static string GetSandboxRoot(User user)
    {
        return Path.GetFullPath(Path.Combine(user.OutputDirectory, SandboxSubdir));
    }

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        Session session = ctx.Session;
        CancellationToken ct = ctx.Ct;
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "file_write requires an authenticated user." };
        }
        string path = args["path"]?.ToString();
        string content = args["content"]?.ToString();
        bool overwrite = args["overwrite"]?.Value<bool>() ?? false;

        if (string.IsNullOrWhiteSpace(path))
        {
            return new JObject { ["success"] = false, ["error"] = "path is required" };
        }
        if (content is null)
        {
            return new JObject { ["success"] = false, ["error"] = "content is required" };
        }
        // Reject generic placeholder names so the LLM is forced to pick a meaningful one.
        // The tool result loops back as a user message, so the LLM sees this and retries.
        if (IsPlaceholderName(path))
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "Filename looks like a placeholder. Pick a short, descriptive name based on the file's actual content (eg 'pizza-recipe.md' instead of 'untitled.md'). The user sees this name in their asset list."
            };
        }
        if (content.Length > 1024 * 1024)
        {
            return new JObject { ["success"] = false, ["error"] = "content is too large (max 1MB)" };
        }
        HashSet<string> allowed = ResolveAllowedExtensions(session, ctx.AssistantId);
        string ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && !allowed.Contains(ext))
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"Disallowed file extension: {ext}. Allowed: {string.Join(", ", allowed.OrderBy(e => e))}. To enable additional extensions, edit the file_write tool's config in LLM Assistant settings."
            };
        }
        try
        {
            string sandboxRoot = GetSandboxRoot(session.User);
            Directory.CreateDirectory(sandboxRoot);
            // SwarmUI's canonical sandbox check (handles traversal, symlinks, normalization).
            (string fullPath, string consoleError, string userError) = WebServer.CheckFilePath(sandboxRoot, path);
            if (fullPath is null)
            {
                if (consoleError is not null) Utils.Logs.Warning($"[LLMAssistant] file_write rejected path: {consoleError}");
                return new JObject { ["success"] = false, ["error"] = userError ?? "Invalid path." };
            }
            // Defense-in-depth: re-verify the resolved path lives inside the sandbox. WebServer.CheckFilePath
            // is already the primary line of defense; this second check guards against a regression in that
            // helper or an unusual symlink/junction case it didn't catch.
            string sandboxFull = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string targetFull = Path.GetFullPath(fullPath);
            bool isInside = targetFull.Equals(sandboxFull, StringComparison.Ordinal)
                || targetFull.StartsWith(sandboxFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || targetFull.StartsWith(sandboxFull + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
            if (!isInside)
            {
                Utils.Logs.Warning($"[LLMAssistant] file_write defense-in-depth check rejected '{targetFull}' (sandbox '{sandboxFull}').");
                return new JObject { ["success"] = false, ["error"] = "Path escaped sandbox (defense-in-depth check failed)." };
            }
            string parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }
            if (File.Exists(fullPath) && !overwrite)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "File already exists. Set overwrite=true to replace it.",
                    ["path"] = path
                };
            }
            await File.WriteAllTextAsync(fullPath, content, System.Text.Encoding.UTF8, ct);
            // The /Output/{*Path} route serves files from the OutputPath root with per-user
            // auth, so the URL we return must be relative to the OutputPath root.
            string outputRoot = Path.GetFullPath(Program.ServerSettings.Paths.OutputPath);
            string relPath = Path.GetRelativePath(outputRoot, fullPath).Replace('\\', '/');
            string url = $"Output/{relPath}";
            return new JObject
            {
                ["success"] = true,
                ["path"] = path,
                ["fullPath"] = fullPath,
                ["url"] = url,
                ["bytesWritten"] = System.Text.Encoding.UTF8.GetByteCount(content)
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Common placeholder filenames the LLM tends to fall back to when it doesn't bother
    /// thinking of a real name. Match the bare stem (no extension, no folder, optional trailing
    /// digits/separators like "untitled-1" or "file_2"). Reject so the LLM retries with a real name.</summary>
    private static readonly HashSet<string> PlaceholderStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "untitled", "noname", "newfile", "file", "output", "result", "data", "document", "doc", "test", "temp", "tmp"
    };

    private static bool IsPlaceholderName(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path ?? "") ?? "";
        // Strip a trailing "-N" / "_N" / " N" counter so "untitled-1" / "file_2" are caught too.
        string normalized = System.Text.RegularExpressions.Regex.Replace(stem, @"[\s_\-]\d+$", "").Trim();
        return PlaceholderStems.Contains(normalized);
    }

    /// <summary>Returns the effective allowed-extension set: defaults ∪ the user's configured extras
    /// ∪ the assistant's configured extras. Each user-supplied entry is normalized to a leading
    /// dot and lowercased; empty entries are skipped.</summary>
    private static HashSet<string> ResolveAllowedExtensions(Session session, string assistantId)
    {
        HashSet<string> allowed = new(DefaultExtensions, StringComparer.OrdinalIgnoreCase);
        if (session?.User is null)
        {
            return allowed;
        }
        if (ToolConfigService.GetConfig(ToolConstants.FileWrite, session.User, assistantId)[ConfigExtraExtensions] is JArray extras)
        {
            foreach (JToken t in extras)
            {
                string raw = t?.ToString()?.Trim();
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }
                allowed.Add(raw.StartsWith('.') ? raw : "." + raw);
            }
        }
        return allowed;
    }
}
