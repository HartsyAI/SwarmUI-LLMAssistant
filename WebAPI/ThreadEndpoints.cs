using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Thread CRUD endpoints.</summary>
public static class ThreadEndpoints
{
    public static async Task<JObject> LLMAssistantGetThreads(Session session)
    {
        JArray threads = ThreadStorageService.GetThreadIndex(session.User);
        return new JObject
        {
            ["success"] = true,
            ["threads"] = threads
        };
    }

    public static async Task<JObject> LLMAssistantGetThread(Session session, string threadId)
    {
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"Thread '{threadId}' not found."
            };
        }
        return new JObject
        {
            ["success"] = true,
            ["thread"] = thread
        };
    }

    public static async Task<JObject> LLMAssistantSaveThread(Session session, JObject rawInput)
    {
        JObject thread = rawInput["thread"] as JObject;
        if (thread is null)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "No thread data provided."
            };
        }
        ThreadStorageService.SaveThread(session.User, thread);
        return new JObject
        {
            ["success"] = true,
            ["thread"] = thread
        };
    }

    public static async Task<JObject> LLMAssistantDeleteThread(Session session, string threadId)
    {
        bool deleted = ThreadStorageService.DeleteThread(session.User, threadId);
        return new JObject
        {
            ["success"] = deleted,
            ["error"] = deleted ? null : $"Thread '{threadId}' not found."
        };
    }

    /// <summary>Exports a thread in JSON or Markdown format.</summary>
    public static async Task<JObject> LLMAssistantExportThread(Session session, string threadId, string format = "json")
    {
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Thread '{threadId}' not found." };
        }
        string title = thread["title"]?.ToString() ?? "thread";
        string safeTitle = new string(title.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ').ToArray()).Replace(' ', '_');
        string content;
        string filename;
        if (format == "markdown")
        {
            StringBuilder sb = new();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            JArray messages = thread["messages"] as JArray ?? [];
            foreach (JToken msg in messages)
            {
                string role = msg["role"]?.ToString() == Roles.User ? "You" : "Assistant";
                sb.AppendLine($"## {role}");
                sb.AppendLine();
                sb.AppendLine(msg["content"]?.ToString() ?? "");
                sb.AppendLine();
            }
            content = sb.ToString();
            filename = $"{safeTitle}.md";
        }
        else
        {
            JObject export = new()
            {
                ["id"] = thread["id"],
                ["title"] = title,
                ["assistantId"] = thread["assistantId"],
                ["messages"] = thread["messages"],
                ["parameters"] = thread["parameters"],
                ["exportedAt"] = DateTime.UtcNow.ToString("o")
            };
            content = export.ToString(Formatting.Indented);
            filename = $"{safeTitle}.json";
        }
        return new JObject
        {
            ["success"] = true,
            ["content"] = content,
            ["filename"] = filename
        };
    }
}
