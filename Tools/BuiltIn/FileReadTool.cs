using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: read a text file from the SwarmUI Data directory (sandboxed via
/// <see cref="WebServer.CheckFilePath"/>).</summary>
public class FileReadTool : ToolHandler
{
    public override string HandlerId => ToolConstants.FileRead;

    public override async Task<JObject> Execute(JObject args, Session session, CancellationToken ct)
    {
        string path = args["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            return new JObject { ["success"] = false, ["error"] = "path is required" };
        }
        int maxBytes = args["maxBytes"]?.Value<int>() ?? 65536;
        if (maxBytes <= 0 || maxBytes > 1024 * 1024)
        {
            maxBytes = 65536;
        }
        try
        {
            string dataRoot = Path.GetFullPath("Data");
            // SwarmUI's canonical sandbox check (handles traversal, symlinks, normalization).
            (string fullPath, string consoleError, string userError) = WebServer.CheckFilePath(dataRoot, path);
            if (fullPath is null)
            {
                if (consoleError is not null) Logs.Warning($"[LLMAssistant] file_read rejected path: {consoleError}");
                return new JObject { ["success"] = false, ["error"] = userError ?? "Invalid path." };
            }
            if (!File.Exists(fullPath))
            {
                return new JObject { ["success"] = false, ["error"] = $"File not found: {path}" };
            }
            FileInfo info = new(fullPath);
            int bytesToRead = (int)Math.Min(maxBytes, info.Length);
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
                ["path"] = path,
                ["size"] = info.Length,
                ["bytesRead"] = bytesToRead,
                ["truncated"] = info.Length > bytesToRead,
                ["content"] = content
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
