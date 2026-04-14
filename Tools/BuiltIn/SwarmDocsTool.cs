using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: read SwarmUI's bundled documentation (the markdown files under
/// the repo's <c>docs/</c> folder). Sandboxed strictly to that directory — the LLM cannot
/// reach anywhere else on disk through this tool.
///
/// <para>Two modes:</para>
/// <list type="bullet">
/// <item><c>action="list"</c> — returns a flat list of every <c>.md</c> file under <c>docs/</c>
/// (recursive). Use this first to discover what's available.</item>
/// <item><c>action="read"</c> — returns the full contents of one specific doc by relative path
/// (e.g. <c>"Basic Usage.md"</c>, <c>"Features/Prompt Syntax.md"</c>).</item>
/// </list>
///
/// <para>This tool intentionally does <b>not</b> share <see cref="FileReadTool"/>'s sandbox —
/// docs are public reference material, not user data, and live outside the SwarmUI Data folder.</para>
/// </summary>
public class SwarmDocsTool : ToolHandler
{
    public override string HandlerId => ToolConstants.SwarmDocs;

    /// <summary>Hard cap on bytes returned for a single read, to keep the LLM context bounded.</summary>
    private const int MaxReadBytes = 256 * 1024;

    public override async Task<JObject> Execute(JObject args, Session session, CancellationToken ct)
    {
        string action = args["action"]?.ToString()?.Trim().ToLowerInvariant() ?? "list";
        try
        {
            string docsRoot = Path.GetFullPath("docs");
            if (!Directory.Exists(docsRoot))
            {
                return new JObject { ["success"] = false, ["error"] = "SwarmUI docs folder not found on this install." };
            }
            if (action == "list")
            {
                return ListDocs(docsRoot);
            }
            if (action == "read")
            {
                string path = args["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new JObject { ["success"] = false, ["error"] = "path is required when action='read'" };
                }
                return await ReadDoc(docsRoot, path, ct);
            }
            return new JObject { ["success"] = false, ["error"] = $"Unknown action '{action}'. Use 'list' or 'read'." };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Recursively lists every <c>.md</c> file under <c>docs/</c> as a relative path.
    /// Output is sorted for stable LLM consumption.</summary>
    private static JObject ListDocs(string docsRoot)
    {
        string[] files = Directory.GetFiles(docsRoot, "*.md", SearchOption.AllDirectories);
        JArray entries = [];
        foreach (string full in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(docsRoot, full).Replace('\\', '/');
            entries.Add(rel);
        }
        return new JObject
        {
            ["success"] = true,
            ["root"] = "docs/",
            ["count"] = entries.Count,
            ["files"] = entries
        };
    }

    /// <summary>Reads a single doc, validating the resolved path stays inside <c>docs/</c>
    /// (defends against <c>../</c> traversal). Truncates the result at <see cref="MaxReadBytes"/>.</summary>
    private static async Task<JObject> ReadDoc(string docsRoot, string path, CancellationToken ct)
    {
        string fullPath = Path.GetFullPath(Path.Combine(docsRoot, path));
        if (!fullPath.StartsWith(docsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(docsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "Path is outside the SwarmUI docs directory (sandbox violation)."
            };
        }
        if (!File.Exists(fullPath))
        {
            return new JObject { ["success"] = false, ["error"] = $"Doc not found: {path}" };
        }
        FileInfo info = new(fullPath);
        int bytesToRead = (int)Math.Min(MaxReadBytes, info.Length);
        byte[] buffer = new byte[bytesToRead];
        await using (FileStream fs = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int totalRead = 0;
            while (totalRead < bytesToRead)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(totalRead, bytesToRead - totalRead), ct);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }
        }
        string content = System.Text.Encoding.UTF8.GetString(buffer);
        return new JObject
        {
            ["success"] = true,
            ["path"] = path.Replace('\\', '/'),
            ["size"] = info.Length,
            ["bytesRead"] = bytesToRead,
            ["truncated"] = info.Length > bytesToRead,
            ["content"] = content
        };
    }
}
