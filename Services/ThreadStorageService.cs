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

    /// <summary>Gets a full thread by ID. The returned blob is guaranteed branch-shaped (every message
    /// carries <c>parentId</c> and the thread carries <c>activeLeafId</c>) via lazy migration.</summary>
    public static JObject GetThread(User user, string threadId)
    {
        string raw = user.GetGenericData(DataName, ThreadPrefix + threadId);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        try
        {
            JObject thread = JObject.Parse(raw);
            EnsureTreeShape(thread);
            return thread;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Upgrades a thread blob in-memory to the branch (tree) shape: every message gets an
    /// <c>id</c> and a <c>parentId</c>, and the thread gets an <c>activeLeafId</c>. Legacy (pre-branching)
    /// threads are linear — each message's parent is the message before it and the active leaf is the last
    /// message. Idempotent: a thread that already carries branch metadata is left untouched. The upgrade is
    /// persisted on the next <see cref="SaveThread"/>; a read-only load just recomputes it (cheap).</summary>
    public static void EnsureTreeShape(JObject thread)
    {
        if (thread?["messages"] is not JArray messages || messages.Count == 0)
        {
            return;
        }
        string prevId = null;
        foreach (JToken tok in messages)
        {
            if (tok is not JObject m)
            {
                continue;
            }
            if (m["id"] is null || string.IsNullOrEmpty(m["id"].ToString()))
            {
                m["id"] = Guid.NewGuid().ToString("N");
            }
            if (!m.ContainsKey("parentId"))
            {
                m["parentId"] = prevId is null ? JValue.CreateNull() : new JValue(prevId);
            }
            prevId = m["id"].ToString();
        }
        if (thread["activeLeafId"] is null || string.IsNullOrEmpty(thread["activeLeafId"].ToString()))
        {
            thread["activeLeafId"] = prevId; // last message id
        }
    }

    /// <summary>Returns the linear "active path" of a thread — the chain from the root down to
    /// <c>activeLeafId</c>, oldest first. This is the conversation the user currently sees and the
    /// history handed to the LLM; off-path branches are excluded. Falls back to the last message when
    /// no valid active leaf is set. Cycle-safe.</summary>
    public static List<JObject> GetActivePath(JObject thread)
    {
        List<JObject> path = [];
        if (thread?["messages"] is not JArray messages || messages.Count == 0)
        {
            return path;
        }
        Dictionary<string, JObject> byId = new();
        foreach (JToken tok in messages)
        {
            if (tok is JObject m && m["id"]?.ToString() is string id && !string.IsNullOrEmpty(id))
            {
                byId[id] = m;
            }
        }
        string leaf = thread["activeLeafId"]?.ToString();
        if (string.IsNullOrEmpty(leaf) || !byId.ContainsKey(leaf))
        {
            leaf = (messages[^1] as JObject)?["id"]?.ToString();
        }
        HashSet<string> visited = [];
        string cur = leaf;
        while (!string.IsNullOrEmpty(cur) && byId.TryGetValue(cur, out JObject node) && visited.Add(cur))
        {
            path.Add(node);
            cur = node["parentId"] is null || node["parentId"].Type == JTokenType.Null ? null : node["parentId"].ToString();
        }
        path.Reverse();
        return path;
    }

    /// <summary>Resolves a message's parentId as a string (null for a root message).</summary>
    private static string ParentIdOf(JObject message)
    {
        JToken pid = message?["parentId"];
        return pid is null || pid.Type == JTokenType.Null ? null : pid.ToString();
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
        EnsureTreeShape(thread);
        // messageCount reflects the active branch (what the user sees), not every node across all branches.
        thread["messageCount"] = GetActivePath(thread).Count;
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
        // Branch model: a new message hangs off the current active leaf and becomes the new leaf. For a
        // linear thread the active leaf is the last message, so this is identical to a plain append.
        if (!message.ContainsKey("parentId"))
        {
            string leaf = thread["activeLeafId"]?.ToString();
            message["parentId"] = string.IsNullOrEmpty(leaf) ? JValue.CreateNull() : new JValue(leaf);
        }
        messages.Add(message);
        thread["messages"] = messages;
        thread["activeLeafId"] = message["id"];
        SaveThread(user, thread);
        return thread;
    }

    /// <summary>Points the thread's active path at <paramref name="messageId"/> — i.e. the rendered
    /// conversation becomes root→that message, and the next appended message branches from it. Backs the
    /// Fork action and the branch-switcher pager. Returns the updated thread, or null if not found.</summary>
    public static JObject SetActiveLeaf(User user, string threadId, string messageId)
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
        if (!messages.OfType<JObject>().Any(m => m["id"]?.ToString() == messageId))
        {
            return null;
        }
        thread["activeLeafId"] = messageId;
        SaveThread(user, thread);
        return thread;
    }

    /// <summary>Adds <paramref name="newMessage"/> as a sibling of <paramref name="refMessageId"/> (same
    /// parent) and makes it the active leaf — creating a new branch alongside the existing one. Backs the
    /// "edit a message into a new branch" flow. Returns the updated thread, or null if the ref isn't found.</summary>
    public static JObject AddBranch(User user, string threadId, string refMessageId, JObject newMessage)
    {
        if (newMessage is null || string.IsNullOrEmpty(refMessageId))
        {
            return null;
        }
        JObject thread = GetThread(user, threadId);
        if (thread is null || thread["messages"] is not JArray messages)
        {
            return null;
        }
        JObject refNode = messages.OfType<JObject>().FirstOrDefault(m => m["id"]?.ToString() == refMessageId);
        if (refNode is null)
        {
            return null;
        }
        if (newMessage["id"] is null)
        {
            newMessage["id"] = Guid.NewGuid().ToString("N");
        }
        if (newMessage["timestamp"] is null)
        {
            newMessage["timestamp"] = DateTime.UtcNow.ToString("o");
        }
        newMessage["parentId"] = refNode["parentId"]?.DeepClone() ?? JValue.CreateNull();
        messages.Add(newMessage);
        thread["messages"] = messages;
        thread["activeLeafId"] = newMessage["id"];
        SaveThread(user, thread);
        return thread;
    }

    /// <summary>Removes a message and its entire subtree (all descendant branches) by id and persists.
    /// In the branch model a message can have children; deleting it must drop them too or they'd orphan.
    /// If the active leaf was inside the removed subtree, it falls back to the deleted message's parent.
    /// Returns the updated thread, or null if the thread or message wasn't found.</summary>
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
        JObject target = messages.OfType<JObject>().FirstOrDefault(m => m["id"]?.ToString() == messageId);
        if (target is null)
        {
            return null;
        }
        // Collect the target plus every transitive descendant.
        HashSet<string> toRemove = [messageId];
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (JObject m in messages.OfType<JObject>())
            {
                string id = m["id"]?.ToString();
                string pid = ParentIdOf(m);
                if (id is not null && pid is not null && toRemove.Contains(pid) && !toRemove.Contains(id))
                {
                    toRemove.Add(id);
                    grew = true;
                }
            }
        }
        string parentOfDeleted = ParentIdOf(target);
        JArray survivors = [];
        foreach (JObject m in messages.OfType<JObject>())
        {
            if (!toRemove.Contains(m["id"]?.ToString()))
            {
                survivors.Add(m);
            }
        }
        thread["messages"] = survivors;
        string leaf = thread["activeLeafId"]?.ToString();
        if (string.IsNullOrEmpty(leaf) || toRemove.Contains(leaf))
        {
            thread["activeLeafId"] = parentOfDeleted is null ? JValue.CreateNull() : new JValue(parentOfDeleted);
        }
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
        if (thread is null)
        {
            return null;
        }
        if (thread["messages"] is not JArray messages)
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
        // Build summary from the active branch — the conversation the user actually sees.
        List<JObject> active = GetActivePath(thread);
        string title = thread["title"]?.ToString();
        if (string.IsNullOrEmpty(title) && active.Count > 0)
        {
            string firstMsg = active[0]?["content"]?.ToString() ?? "New Thread";
            title = firstMsg.Length > 50 ? firstMsg[..50] + "..." : firstMsg;
        }
        // Build searchable preview from active-branch message content
        string preview = "";
        if (active.Count > 0)
        {
            preview = string.Join(" ", active.Select(m => m["content"]?.ToString() ?? ""));
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
