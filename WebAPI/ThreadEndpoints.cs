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
                ["error"] = $"Chat '{threadId}' not found."
            };
        }
        return new JObject
        {
            ["success"] = true,
            ["thread"] = thread
        };
    }

    public static async Task<JObject> LLMAssistantDeleteThread(Session session, string threadId)
    {
        bool deleted = ThreadStorageService.DeleteThread(session.User, threadId);
        // Only include "error" on failure: the API layer treats the mere PRESENCE of an "error" key as a
        // failure and logs it, so `error = null` on success produces a spurious empty-message error log.
        if (!deleted)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        return new JObject { ["success"] = true };
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
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        JArray messages = thread["messages"] as JArray;
        if (messages is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Thread has no messages array." };
        }
        JObject targetMessage = messages.OfType<JObject>().FirstOrDefault(m => m["id"]?.ToString() == messageId);
        if (targetMessage is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Message '{messageId}' not found in thread." };
        }
        JObject updatedThread = ThreadStorageService.EditMessage(session.User, threadId, messageId, content);
        if (updatedThread is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Failed to save message edit." };
        }
        return new JObject { ["success"] = true, ["thread"] = updatedThread };
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
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        thread["title"] = title.Trim();
        // A manual rename claims the title permanently — the auto-title generator must never overwrite it.
        ThreadStorageService.MarkTitleClaimed(thread);
        ThreadStorageService.SaveThread(session.User, thread);
        return new JObject
        {
            ["success"] = true,
            ["thread"] = thread
        };
    }

    /// <summary>Points a thread's active branch at a specific message (Fork / branch-switcher pager).
    /// In the in-thread tree model "forking" doesn't copy the thread — it just moves the active leaf so the
    /// rendered conversation becomes root→that message and the next sent message branches from there.
    /// Request: <c>{ threadId, messageId }</c>. Returns the updated thread.</summary>
    public static async Task<JObject> LLMAssistantSetActiveLeaf(Session session, JObject rawInput)
    {
        string threadId = rawInput["threadId"]?.ToString();
        string messageId = rawInput["messageId"]?.ToString();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return new JObject { ["success"] = false, ["error"] = "messageId is required." };
        }
        JObject thread = ThreadStorageService.SetActiveLeaf(session.User, threadId, messageId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Thread or message not found." };
        }
        return new JObject { ["success"] = true, ["thread"] = thread };
    }

    /// <summary>Exports a thread in JSON or Markdown format.</summary>
    public static async Task<JObject> LLMAssistantExportThread(Session session, string threadId, string format = "json")
    {
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
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
