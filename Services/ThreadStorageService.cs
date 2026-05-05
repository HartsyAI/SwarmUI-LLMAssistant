using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Persists chat threads per-user via SwarmUI's GenericData store.</summary>
public static class ThreadStorageService
{
    public const string DataName = "llmassistant";
    public const string IndexKey = "thread_index";
    public const string ThreadPrefix = "thread_";

    /// <summary>Gets the thread index for a user.</summary>
    public static JArray GetThreadIndex(User user)
    {
        string raw = user.GetGenericData(DataName, IndexKey);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }
        try
        {
            return JArray.Parse(raw);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Gets a full thread by ID.</summary>
    public static JObject GetThread(User user, string threadId)
    {
        string raw = user.GetGenericData(DataName, ThreadPrefix + threadId);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        try
        {
            return JObject.Parse(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves a thread and updates the index.</summary>
    public static void SaveThread(User user, JObject thread)
    {
        string threadId = thread["id"]?.ToString();
        if (string.IsNullOrEmpty(threadId))
        {
            threadId = GenerateThreadId();
            thread["id"] = threadId;
        }
        thread["updatedAt"] = DateTime.UtcNow.ToString("o");
        if (thread["createdAt"] is null)
        {
            thread["createdAt"] = DateTime.UtcNow.ToString("o");
        }
        JArray messages = thread["messages"] as JArray ?? [];
        thread["messageCount"] = messages.Count;
        // Auto-title: if the title is missing or is a placeholder, derive one from the first user message.
        string currentTitle = thread["title"]?.ToString();
        if (IsPlaceholderTitle(currentTitle))
        {
            string derived = DeriveTitleFromMessages(messages);
            if (!string.IsNullOrEmpty(derived))
            {
                thread["title"] = derived;
            }
        }
        // Save thread data
        user.SaveGenericData(DataName, ThreadPrefix + threadId, thread.ToString(Formatting.None));
        // Update index
        UpdateIndex(user, thread);
    }

    /// <summary>Returns true if the given title is null, empty, or a known placeholder.</summary>
    private static bool IsPlaceholderTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }
        if (title == "New Thread" || title == "New Chat" || title == "Untitled")
        {
            return true;
        }
        if (title.StartsWith("Chat with ", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    /// <summary>Derives a short title from the first user message in a thread, or null if none.</summary>
    private static string DeriveTitleFromMessages(JArray messages)
    {
        if (messages is null)
        {
            return null;
        }
        foreach (JToken msg in messages)
        {
            string role = msg["role"]?.ToString();
            if (role != Roles.User)
            {
                continue;
            }
            string content = msg["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }
            string trimmed = content.Trim();
            if (trimmed.Length > 50)
            {
                return trimmed[..50] + "\u2026";
            }
            return trimmed;
        }
        return null;
    }

    /// <summary>Creates a new empty thread for the given assistant and saves it. Used by the
    /// chat endpoint when the user sends a first message without an existing thread.
    /// Returns the freshly-created thread blob (caller can read the assigned <c>id</c>).</summary>
    public static JObject CreateThread(User user, string assistantId, string title = null)
    {
        string threadId = GenerateThreadId();
        string now = DateTime.UtcNow.ToString("o");
        JObject thread = new()
        {
            ["id"] = threadId,
            ["title"] = title ?? "New Thread",
            ["assistantId"] = assistantId ?? "",
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["messages"] = new JArray(),
            ["messageCount"] = 0
        };
        SaveThread(user, thread);
        return thread;
    }

    /// <summary>Appends a single message to a thread and persists. The message is expected to
    /// have at minimum <c>role</c> and <c>content</c>; <c>id</c> and <c>timestamp</c> are filled
    /// in if missing. Returns the updated thread, or null if not found.</summary>
    public static JObject AppendMessage(User user, string threadId, JObject message)
    {
        if (message is null)
        {
            return null;
        }
        JObject thread = GetThread(user, threadId);
        if (thread is null)
        {
            return null;
        }
        JArray messages = thread["messages"] as JArray ?? [];
        if (message["id"] is null)
        {
            message["id"] = Guid.NewGuid().ToString("N");
        }
        if (message["timestamp"] is null)
        {
            message["timestamp"] = DateTime.UtcNow.ToString("o");
        }
        messages.Add(message);
        thread["messages"] = messages;
        SaveThread(user, thread);
        return thread;
    }

    /// <summary>Removes a single message from a thread by message id and persists. Returns the
    /// updated thread, or null if the thread or message wasn't found.</summary>
    public static JObject DeleteMessage(User user, string threadId, string messageId)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            return null;
        }
        JObject thread = GetThread(user, threadId);
        if (thread is null || thread["messages"] is not JArray messages)
        {
            return null;
        }
        JToken target = messages.FirstOrDefault(m => m["id"]?.ToString() == messageId);
        if (target is null)
        {
            return null;
        }
        messages.Remove(target);
        thread["messages"] = messages;
        SaveThread(user, thread);
        return thread;
    }

    /// <summary>Replaces the <c>content</c> field of a single message and persists. Other fields
    /// (role, toolCalls, timestamp) are left intact. Returns the updated thread, or null if not found.</summary>
    public static JObject EditMessage(User user, string threadId, string messageId, string newContent)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            return null;
        }
        JObject thread = GetThread(user, threadId);
        if (thread is null || thread["messages"] is not JArray messages)
        {
            return null;
        }
        JObject target = messages.OfType<JObject>().FirstOrDefault(m => m["id"]?.ToString() == messageId);
        if (target is null)
        {
            return null;
        }
        target["content"] = newContent ?? "";
        target["editedAt"] = DateTime.UtcNow.ToString("o");
        SaveThread(user, thread);
        return thread;
    }

    /// <summary>Deletes a thread and removes it from the index.</summary>
    public static bool DeleteThread(User user, string threadId)
    {
        bool deleted = user.DeleteGenericData(DataName, ThreadPrefix + threadId);
        if (deleted)
        {
            RemoveFromIndex(user, threadId);
        }
        return deleted;
    }

    /// <summary>Generates a unique thread ID.</summary>
    public static string GenerateThreadId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static void UpdateIndex(User user, JObject thread)
    {
        JArray index = GetThreadIndex(user);
        string threadId = thread["id"].ToString();
        // Remove existing entry if present
        JToken existing = index.FirstOrDefault(t => t["id"]?.ToString() == threadId);
        if (existing is not null)
        {
            index.Remove(existing);
        }
        // Build summary
        JArray messages = thread["messages"] as JArray;
        string title = thread["title"]?.ToString();
        if (string.IsNullOrEmpty(title) && messages?.Count > 0)
        {
            string firstMsg = messages[0]?["content"]?.ToString() ?? "New Thread";
            title = firstMsg.Length > 50 ? firstMsg[..50] + "..." : firstMsg;
        }
        // Build searchable preview from message content
        string preview = "";
        if (messages is not null && messages.Count > 0)
        {
            preview = string.Join(" ", messages.Select(m => m["content"]?.ToString() ?? ""));
            if (preview.Length > 200)
            {
                preview = preview[..200];
            }
        }
        JObject summary = new()
        {
            ["id"] = threadId,
            ["title"] = title ?? "New Thread",
            ["preview"] = preview,
            ["createdAt"] = thread["createdAt"],
            ["updatedAt"] = thread["updatedAt"],
            ["messageCount"] = thread["messageCount"]
        };
        // Insert at beginning (most recent first)
        index.Insert(0, summary);
        user.SaveGenericData(DataName, IndexKey, index.ToString(Formatting.None));
    }

    private static void RemoveFromIndex(User user, string threadId)
    {
        JArray index = GetThreadIndex(user);
        JToken existing = index.FirstOrDefault(t => t["id"]?.ToString() == threadId);
        if (existing is not null)
        {
            index.Remove(existing);
            user.SaveGenericData(DataName, IndexKey, index.ToString(Formatting.None));
        }
    }
}
