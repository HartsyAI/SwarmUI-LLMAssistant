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

    // LLMAssistantSaveThread removed: chat is now server-authoritative — the WS chat endpoint
    // and ThreadStorageService.AppendMessage own all writes. Thread metadata edits use the
    // dedicated rename endpoint. To re-add an "import thread" affordance, build a separate
    // ImportThread endpoint that is gated and explicit, not implicit on every send.

    public static async Task<JObject> LLMAssistantDeleteThread(Session session, string threadId)
    {
        bool deleted = ThreadStorageService.DeleteThread(session.User, threadId);
        return new JObject
        {
            ["success"] = deleted,
            ["error"] = deleted ? null : $"Thread '{threadId}' not found."
        };
    }

    /// <summary>Deletes a single message from a thread by message id. Server-authoritative: the
    /// frontend calls this instead of mutating its local <c>messages</c> array and saving the
    /// whole thread (which would let clients spoof history).</summary>
    public static async Task<JObject> LLMAssistantDeleteMessage(Session session, string threadId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return new JObject { ["success"] = false, ["error"] = "messageId is required." };
        }
        JObject thread = ThreadStorageService.DeleteMessage(session.User, threadId, messageId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Thread or message not found." };
        }
        return new JObject { ["success"] = true, ["thread"] = thread };
    }

    /// <summary>Edits the text content of a single message in a thread. Server-authoritative —
    /// see <see cref="LLMAssistantDeleteMessage"/> for the same rationale.</summary>
    public static async Task<JObject> LLMAssistantEditMessage(Session session, JObject rawInput)
    {
        string threadId = rawInput["threadId"]?.ToString();
        string messageId = rawInput["messageId"]?.ToString();
        string content = rawInput["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return new JObject { ["success"] = false, ["error"] = "messageId is required." };
        }
        if (content is null)
        {
            return new JObject { ["success"] = false, ["error"] = "content is required (use empty string to blank a message)." };
        }
        JObject thread = ThreadStorageService.EditMessage(session.User, threadId, messageId, content);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Thread or message not found." };
        }
        return new JObject { ["success"] = true, ["thread"] = thread };
    }

    /// <summary>Renames a thread. The UI calls this instead of re-sending the entire thread on rename.</summary>
    public static async Task<JObject> LLMAssistantRenameThread(Session session, string threadId, string title)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            return new JObject { ["success"] = false, ["error"] = "title is required." };
        }
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Thread '{threadId}' not found." };
        }
        thread["title"] = title.Trim();
        ThreadStorageService.SaveThread(session.User, thread);
        return new JObject
        {
            ["success"] = true,
            ["thread"] = thread
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
