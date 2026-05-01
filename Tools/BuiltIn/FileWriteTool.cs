using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: write a text file into a sandboxed output directory (sandboxed).</summary>
public class FileWriteTool : ToolHandler
{
    public override string HandlerId => ToolConstants.FileWrite;

    public override async Task<JObject> Execute(JObject args, Session session, CancellationToken ct)
    {
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

        if (content.Length > 1024 * 1024)
        {
            return new JObject { ["success"] = false, ["error"] = "content is too large (max 1MB)" };
        }

        string ext = Path.GetExtension(path ?? "");
        if (!string.IsNullOrEmpty(ext))
        {
            string lowerExt = ext.ToLowerInvariant();
            if (lowerExt != ".md" && lowerExt != ".json" && lowerExt != ".txt" && lowerExt != ".yaml" && lowerExt != ".yml" && lowerExt != ".csv" && lowerExt != ".log")
            {
                return new JObject { ["success"] = false, ["error"] = $"Disallowed file extension: {ext}" };
            }
        }

        try
        {
            string sandboxRoot = Path.GetFullPath(Path.Combine("Output", "LLMAssistantFiles"));
            Directory.CreateDirectory(sandboxRoot);

            string fullPath = Path.GetFullPath(Path.Combine(sandboxRoot, path));
            if (!fullPath.StartsWith(sandboxRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !fullPath.Equals(sandboxRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "Path is outside the file_write sandbox (sandbox violation)."
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

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

            string relPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), fullPath)
                .Replace('\\', '/');

            return new JObject
            {
                ["success"] = true,
                ["path"] = path,
                ["fullPath"] = fullPath,
                ["url"] = relPath,
                ["bytesWritten"] = System.Text.Encoding.UTF8.GetByteCount(content)
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
