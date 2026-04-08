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
        // Save thread data
        user.SaveGenericData(DataName, ThreadPrefix + threadId, thread.ToString(Formatting.None));
        // Update index
        UpdateIndex(user, thread);
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
        return $"{DateTime.UtcNow.Ticks}_{Guid.NewGuid():N}"[..24];
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
