using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Adds a workspace's own instructions file to the system prompt of a chat that can use the code tools, so the model follows the project's
/// conventions without being told each time. The file is <c>AGENTS.md</c>, or <c>CLAUDE.md</c> when there is none. The text is cut to a fixed size and
/// has its line endings normalized, so the prompt is the same text every turn until the file changes, which keeps a local model's prompt cache valid.
/// <para>Only workspaces a user may use are read (their own, or a folder an admin allowed), and only for a chat that has a code tool switched on, so an
/// ordinary chat never takes in a workspace file. Whether to trust an instructions file from a folder someone else controls is a decision for the skills
/// work that follows; until then, the files that can speak here are the ones in the user's own workspaces and the admin's allowed folders.</para></summary>
public static class ProjectInstructionsService
{
    /// <summary>The file names tried, in order.</summary>
    public static readonly string[] FileNames = ["AGENTS.md", "CLAUDE.md"];

    /// <summary>The most characters taken from the file.</summary>
    public const int MaxChars = 16_000;

    /// <summary>The largest file looked at.</summary>
    public const long MaxFileBytes = 256 * 1024;

    /// <summary>The text of the workspace's instructions file, ready to add to a prompt, or null when it has none.</summary>
    public static string Read(Workspace workspace)
    {
        foreach (string name in FileNames)
        {
            if (!PathSandbox.TryResolve(workspace.Root, name, out string full, out _) || !File.Exists(full))
            {
                continue;
            }
            FileInfo info = new(full);
            if (info.Length is 0 or > MaxFileBytes)
            {
                continue;
            }
            string text = File.ReadAllText(full, Encoding.UTF8).Replace("\r\n", "\n").Trim();
            if (text.Length == 0)
            {
                continue;
            }
            if (text.Length > MaxChars)
            {
                text = text[..MaxChars] + "\n[The rest of the file was left out.]";
            }
            return $"# Project instructions ({name} in the workspace)\n\n{text}";
        }
        return null;
    }

    /// <summary>Whether any of the tools a chat is given is a code tool.</summary>
    public static bool HasCodeTool(IEnumerable<JObject> enabledTools)
        => enabledTools.Any(tool => ToolConstants.CodeIds.Contains(tool["handlerId"]?.ToString() ?? ""));

    /// <summary>The system prompt with the workspace's instructions added after it, when this chat has a code tool and the user holds <c>llm_code_mode</c>.
    /// Otherwise the prompt is returned as it is.</summary>
    public static string Append(string systemPrompt, User user, JObject thread, IEnumerable<JObject> enabledTools)
    {
        if (user is null || !user.HasPermission(WebAPI.LLMAssistantAPI.PermCodeMode) || !HasCodeTool(enabledTools))
        {
            return systemPrompt;
        }
        Workspace workspace = WorkspaceService.Resolve(user, thread?["workspace"]?.ToString());
        string instructions = workspace is null ? null : Read(workspace);
        return instructions is null ? systemPrompt : $"{systemPrompt}\n\n{instructions}";
    }
}
