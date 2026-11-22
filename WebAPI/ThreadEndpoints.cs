using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Compaction;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Thread CRUD endpoints.</summary>
public static class ThreadEndpoints
{
    public static async Task<JObject> LLMAssistantGetThreads(Session session)
    {
        // A session that says it is working while nothing is (the server restarted) is marked interrupted before the list goes out.
        string userId = session.User.UserID;
        SessionManager.Reconcile(session.User, id => BackgroundTurnService.IsLive(userId, id) || GenerationCancellationRegistry.IsActive(id));
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
        JObject result = new() { ["success"] = true, ["thread"] = thread };
        // The summary that stands in for the start of the conversation on the branch now showing, so the client can mark where it ends.
        if (CompactionPlanner.FindActive(thread, ThreadStorageService.GetActivePath(thread)) is { } active)
        {
            result["activeCompaction"] = ChatEndpoints.CompactionJson(active);
        }
        return result;
    }

    public static async Task<JObject> LLMAssistantDeleteThread(Session session, string threadId)
    {
        // Deleting a chat deletes the sessions it started, deepest first, and stops any that are working: they have nobody to report to.
        foreach (string id in SessionManager.DescendantIds(session.User, threadId))
        {
            await BackgroundTurnService.StopAsync(session.User, id);
            ToolApprovals.Pending.CancelScope(session.User.UserID, id);
            ToolApprovals.Grants.Clear(session.User.UserID, id);
            ThreadStorageService.DeleteThread(session.User, id);
        }
        await BackgroundTurnService.StopAsync(session.User, threadId);
        bool deleted = ThreadStorageService.DeleteThread(session.User, threadId);
        // A chat that is gone has nobody to answer for it: end any call still waiting for approval and forget what was allowed.
        ToolApprovals.Pending.CancelScope(session.User.UserID, threadId);
        ToolApprovals.Grants.Clear(session.User.UserID, threadId);
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

    /// <summary>Switches a chat between an ordinary chat and a coding session. Request: <c>{ threadId, mode }</c>, <c>mode</c> being <c>chat</c> or <c>code</c>. Code
    /// mode needs <c>llm_code_mode</c>. A chat in code mode brings the code tools and shows the workspace panel; nothing already in the chat changes.</summary>
    public static async Task<JObject> LLMAssistantSetThreadMode(Session session, JObject rawInput)
    {
        string threadId = rawInput["threadId"]?.ToString();
        string mode = (rawInput["mode"]?.ToString() ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (!ThreadModes.IsValid(mode))
        {
            return new JObject { ["success"] = false, ["error"] = "mode must be chat or code." };
        }
        if (mode == ThreadModes.Code && (session.User is null || !session.User.HasPermission(LLMAssistantAPI.PermCodeMode)))
        {
            return new JObject { ["success"] = false, ["error"] = "Code mode needs the LLM Code Mode permission." };
        }
        JObject thread = ThreadStorageService.SetMode(session.User, threadId, mode);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        return new JObject { ["success"] = true, ["mode"] = mode };
    }

    /// <summary>Saves how this chat approves tool calls: <c>auto</c> (everything runs, the default), <c>ask</c> (anything that changes something
    /// asks first) or <c>plan</c> (read-only). Request: <c>{ threadId, mode }</c>. A reply streamed after this uses it unless the request names
    /// its own <c>approvalMode</c>.</summary>
    public static async Task<JObject> LLMAssistantSetThreadApprovalMode(Session session, JObject rawInput)
    {
        string threadId = rawInput["threadId"]?.ToString();
        string mode = (rawInput["mode"]?.ToString() ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (!ApprovalPolicy.IsValid(mode))
        {
            return new JObject { ["success"] = false, ["error"] = "mode must be auto, ask or plan." };
        }
        JObject thread = ThreadStorageService.SetApprovalMode(session.User, threadId, mode);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        return new JObject { ["success"] = true, ["mode"] = mode };
    }

    /// <summary>Sets (or clears) this thread's tool-calling override. <c>enabled</c> present and boolean →
    /// pins the thread to that state regardless of the assistant's default; <c>enabled</c> absent/null →
    /// clears the override so the thread goes back to inheriting the assistant's master switch. Request:
    /// <c>{ threadId, enabled? }</c>. This is the per-conversation "Tools" toggle in the chat composer — the
    /// assistant-level default lives on the assistant itself (<c>toolsEnabled</c>, edited in the assistant
    /// editor) and is what a new thread starts from until this override is set.</summary>
    public static async Task<JObject> LLMAssistantSetThreadToolsEnabled(Session session, JObject rawInput)
    {
        string threadId = rawInput["threadId"]?.ToString();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        JToken enabledToken = rawInput["enabled"];
        if (enabledToken?.Type == JTokenType.Boolean)
        {
            thread["toolsEnabled"] = enabledToken.Value<bool>();
        }
        else
        {
            thread.Remove("toolsEnabled");
        }
        ThreadStorageService.SaveThread(session.User, thread);
        return new JObject
        {
            ["success"] = true,
            ["threadId"] = threadId,
            ["toolsEnabled"] = thread["toolsEnabled"] ?? JValue.CreateNull()
        };
    }

    /// <summary>Sets (or clears) this chat's memory opt-out ("don't remember this chat"). <c>enabled</c> false → the automatic memory extractor never reads this chat; <c>enabled</c> true or absent
    /// → clears the override, so the chat follows the assistant and the master switch. Request: <c>{ threadId, enabled? }</c>. A chat that is a session ignores it (sessions are never extracted).</summary>
    public static async Task<JObject> LLMAssistantSetThreadMemory(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        string threadId = rawInput["threadId"]?.ToString();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        bool optOut = rawInput["enabled"]?.Type == JTokenType.Boolean && !rawInput["enabled"].Value<bool>();
        // Read, change and saved under the thread write lock, so a message appended at the same moment is not lost and the flag is not.
        JObject thread = ThreadStorageService.Mutate(session.User, threadId, t =>
        {
            if (optOut)
            {
                t["memoryExtraction"] = false;
            }
            else
            {
                t.Remove("memoryExtraction");
            }
            return true;
        });
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        return new JObject { ["success"] = true, ["threadId"] = threadId, ["memoryExtraction"] = thread["memoryExtraction"] ?? JValue.CreateNull() };
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
