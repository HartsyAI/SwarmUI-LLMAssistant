using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>What code mode means for a chat. A chat in code mode brings its own tools: the code tools that are switched on for everyone are offered
/// to it whether or not its assistant lists them, and it may run them. That is the point of the switch, so a user does not have to edit an assistant to
/// use code mode. It still takes <c>llm_code_mode</c>, a code tool enabled on the Tools tab, and tool calling not pinned off for the chat; a chat in
/// chat mode gets code tools only the old way, from its assistant's list.</summary>
public static class CodeModeService
{
    /// <summary>Whether a thread is in code mode.</summary>
    public static bool IsCodeThread(JObject thread) => thread?["mode"]?.ToString() == ThreadModes.Code;

    /// <summary>Whether this chat is one that brings the code tools: it is in code mode, the user holds <c>llm_code_mode</c>, and tool calling has not been
    /// pinned off for the chat.</summary>
    public static bool BringsCodeTools(JObject thread, User user)
    {
        bool pinnedOff = thread?["toolsEnabled"]?.Type == JTokenType.Boolean && !thread["toolsEnabled"].Value<bool>();
        return IsCodeThread(thread) && !pinnedOff && user is not null && user.HasPermission(WebAPI.LLMAssistantAPI.PermCodeMode);
    }

    /// <summary>The tools already chosen for a chat, plus the code tools a code-mode chat brings (each once, and only if it is enabled in the tool list).</summary>
    public static List<JObject> WithCodeTools(List<JObject> enabled, JObject thread, JObject settings, User user)
    {
        if (!BringsCodeTools(thread, user))
        {
            return enabled;
        }
        List<JObject> result = [.. enabled];
        JObject all = settings["tools"] as JObject;
        foreach (string id in ToolConstants.CodeIds)
        {
            if (all?[id] is JObject tool && tool["enabled"]?.Value<bool>() != false && !result.Any(t => t["id"]?.ToString() == id))
            {
                result.Add(tool);
            }
        }
        return result;
    }

    /// <summary>Whether a chat in code mode may run <paramref name="toolId"/> even though its assistant does not list it. Only the code tools, and only for a chat
    /// that <see cref="BringsCodeTools"/>.</summary>
    public static bool GrantsTool(string toolId, User user, string threadId)
    {
        if (user is null || string.IsNullOrEmpty(threadId) || !ToolConstants.CodeIds.Contains(toolId))
        {
            return false;
        }
        return BringsCodeTools(ThreadStorageService.GetThread(user, threadId), user);
    }
}
