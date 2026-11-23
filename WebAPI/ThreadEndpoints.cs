using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;
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
        // Pinned chats come first, the rest in the order they were saved (newest first).
        JArray threads = ThreadOrdering.PinnedFirst(ThreadStorageService.GetThreadIndex(session.User));
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
        // Changed under the thread write lock, so a message or a pin saved at the same moment is not lost.
        JObject thread = ThreadStorageService.Mutate(session.User, threadId, t =>
        {
            t["title"] = title.Trim();
            // A manual rename claims the title permanently — the auto-title generator must never overwrite it.
            ThreadStorageService.MarkTitleClaimed(t);
            return true;
        });
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
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
        JToken enabledToken = rawInput["enabled"];
        JObject thread = ThreadStorageService.Mutate(session.User, threadId, t =>
        {
            if (enabledToken?.Type == JTokenType.Boolean)
            {
                t["toolsEnabled"] = enabledToken.Value<bool>();
            }
            else
            {
                t.Remove("toolsEnabled");
            }
            return true;
        });
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
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

    /// <summary>Exports a thread as <c>json</c> (every message on every branch, and which branch was showing; <see cref="LLMAssistantImportThread"/> reads it) or <c>markdown</c> (the conversation
    /// on screen, for reading; <c>md</c> also works). Request: <c>{ threadId, format? }</c>. Response: <c>{ success, content, filename }</c>.</summary>
    public static async Task<JObject> LLMAssistantExportThread(Session session, string threadId, string format = "json")
    {
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (ThreadExport.NormalizeFormat(format) is null)
        {
            return new JObject { ["success"] = false, ["error"] = "format must be json or markdown." };
        }
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        (string content, string filename) = ThreadExport.Build(thread, format, DateTime.UtcNow);
        return new JObject
        {
            ["success"] = true,
            ["content"] = content,
            ["filename"] = filename
        };
    }

    /// <summary>Searches this user's chats: titles and the text of user and assistant messages (every branch; never tool results or attachments). Request: <c>{ query, limit?, offset?, mode? }</c>,
    /// <c>mode</c> being <c>chat</c> or <c>code</c> to list only that kind. Every word of the query must be somewhere in the chat. Response: <c>{ success, results: [{ threadId, title, updatedAt, titleMatch,
    /// indexTruncated, matchedMessages, snippets: [{ messageId, role, text, ranges: [[start, length]] }] }], total, offset, limit, hasMore, partial }</c>, best match first. A chat's <c>indexTruncated</c> is true
    /// when part of its text is not searched (a message over 20,000 characters, or a chat over 400,000, is cut; see <see cref="ThreadSearch"/>). <c>partial</c> is true when the user has so much text that some
    /// chats are searched by title only.</summary>
    public static async Task<JObject> LLMAssistantSearchThreads(Session session, JObject rawInput)
    {
        string query = rawInput["query"]?.Type == JTokenType.String ? rawInput["query"].ToString() : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new JObject { ["success"] = false, ["error"] = "query is required." };
        }
        if (ThreadSearch.ParseQuery(query) is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"query must be at least {ThreadSearch.MinQueryChars} characters." };
        }
        if (!TryReadInt(rawInput, "limit", ThreadSearch.DefaultLimit, 1, ThreadSearch.MaxLimit, out int limit, out string error)
            || !TryReadInt(rawInput, "offset", 0, 0, ThreadSearch.MaxOffset, out int offset, out error))
        {
            return new JObject { ["success"] = false, ["error"] = error };
        }
        string mode = null;
        if (rawInput["mode"] is { Type: not JTokenType.Null } modeToken)
        {
            mode = modeToken.ToString().Trim().ToLowerInvariant();
            if (mode.Length > 0 && !ThreadModes.IsValid(mode))
            {
                return new JObject { ["success"] = false, ["error"] = "mode must be chat or code." };
            }
        }
        JArray threadIndex = ThreadStorageService.GetThreadIndex(session.User);
        ThreadSearchIndex cache = ThreadSearchIndex.For(session.User);
        // Sessions that were archived are not listed in the sidebar, so they are not found either.
        // Reading the chats the first time (or after they changed) is storage work: off the request thread.
        List<SearchDoc> docs = await Task.Run(() => cache.Refresh(threadIndex, id => ThreadStorageService.GetThread(session.User, id), row =>
            row["sessionStatus"]?.ToString() != SessionStatuses.Archived
            && (string.IsNullOrEmpty(mode) || (row["mode"]?.ToString() ?? ThreadModes.Chat) == mode)));
        SearchPage page = ThreadSearch.Search(docs, query, limit, offset);
        JArray results = [];
        foreach (SearchHit hit in page.Hits)
        {
            JArray snippets = [];
            foreach (SearchSnippet snippet in hit.Snippets)
            {
                snippets.Add(new JObject
                {
                    ["messageId"] = snippet.MessageId,
                    ["role"] = snippet.Role,
                    ["text"] = snippet.Text,
                    ["ranges"] = new JArray(snippet.Ranges.Select(r => new JArray(r.Start, r.Length)))
                });
            }
            results.Add(new JObject
            {
                ["threadId"] = hit.Id,
                ["title"] = hit.Title,
                ["updatedAt"] = hit.UpdatedAt,
                ["titleMatch"] = hit.TitleMatch,
                ["indexTruncated"] = hit.IndexTruncated,
                ["matchedMessages"] = hit.MatchedMessages,
                ["snippets"] = snippets
            });
        }
        return new JObject
        {
            ["success"] = true,
            ["results"] = results,
            ["total"] = page.Total,
            ["offset"] = page.Offset,
            ["limit"] = limit,
            ["hasMore"] = page.HasMore,
            ["partial"] = cache.Partial
        };
    }

    /// <summary>Pins or unpins a chat; pinned chats are listed first (in <c>LLMAssistantGetThreads</c> and the sidebar). Request: <c>{ threadId, pinned }</c>, <c>pinned</c> a boolean. Pinning is not
    /// activity: the chat keeps its date. A session cannot be pinned (it is listed under the chat that started it).</summary>
    public static async Task<JObject> LLMAssistantPinThread(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        string threadId = rawInput["threadId"]?.Type == JTokenType.String ? rawInput["threadId"].ToString() : null;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (rawInput["pinned"]?.Type != JTokenType.Boolean)
        {
            return new JObject { ["success"] = false, ["error"] = "pinned must be true or false." };
        }
        bool pinned = rawInput["pinned"].Value<bool>();
        JObject thread = ThreadStorageService.SetPinned(session.User, threadId, pinned, out bool refused);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        if (refused)
        {
            return new JObject { ["success"] = false, ["error"] = "A session cannot be pinned: it is listed under the chat that started it." };
        }
        return new JObject { ["success"] = true, ["threadId"] = threadId, ["pinned"] = pinned };
    }

    /// <summary>Makes a new chat from the JSON <see cref="LLMAssistantExportThread"/> produces. Request: <c>{ content, preview?, assistantId? }</c>, <c>content</c> being the file's text. With
    /// <c>preview</c> true nothing is saved and the response is <c>{ success, preview: true, summary }</c>; otherwise the chat is saved as a new one (new ids, whatever the file said) and the response is
    /// <c>{ success, threadId, thread (the sidebar row), summary }</c>. The summary is <c>{ title, assistantId, messageCount, hasBranches, skipped, warnings, sample: [{ role, text }] }</c>.
    /// Only the text of user and assistant messages is imported. Limits: <see cref="ThreadImport.MaxFileChars"/> characters, <see cref="ThreadImport.MaxMessages"/> messages,
    /// <see cref="ThreadImport.MaxMessageChars"/> characters each.</summary>
    public static async Task<JObject> LLMAssistantImportThread(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        string content = rawInput["content"]?.Type == JTokenType.String ? rawInput["content"].ToString() : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return new JObject { ["success"] = false, ["error"] = "content is required: the exported JSON of a chat, as text." };
        }
        if (rawInput["preview"] is { Type: not JTokenType.Null } previewToken && previewToken.Type != JTokenType.Boolean)
        {
            return new JObject { ["success"] = false, ["error"] = "preview must be true or false." };
        }
        bool preview = rawInput["preview"]?.Type == JTokenType.Boolean && rawInput["preview"].Value<bool>();
        JObject settings = SettingsService.GetMergedSettings(session.User);
        Func<string, bool> assistantExists = id => settings["assistants"] is JObject assistants && assistants[id] is JObject;
        string fallback = AssistantService.GetActiveAssistantId(settings, session.User);
        string chosen = rawInput["assistantId"]?.Type == JTokenType.String ? rawInput["assistantId"].ToString().Trim() : "";
        if (chosen.Length > 0 && !assistantExists(chosen))
        {
            return new JObject { ["success"] = false, ["error"] = $"Assistant '{chosen}' not found." };
        }
        ThreadImportResult parsed = ThreadImport.Parse(content, assistantExists, chosen.Length > 0 ? chosen : fallback, DateTime.UtcNow);
        if (!parsed.Ok)
        {
            return new JObject { ["success"] = false, ["error"] = parsed.Error };
        }
        if (chosen.Length > 0)
        {
            // The caller named the assistant: it wins over the one in the file.
            parsed.Thread["assistantId"] = chosen;
        }
        JObject summary = new()
        {
            ["title"] = parsed.Thread["title"]?.ToString() ?? "",
            ["assistantId"] = parsed.Thread["assistantId"],
            ["messageCount"] = ((JArray)parsed.Thread["messages"]).Count,
            ["hasBranches"] = parsed.HasBranches,
            ["skipped"] = parsed.Skipped,
            ["warnings"] = new JArray(parsed.Warnings),
            ["sample"] = new JArray(parsed.Sample.Select(m => new JObject { ["role"] = m.Role, ["text"] = m.Text }))
        };
        if (preview)
        {
            return new JObject { ["success"] = true, ["preview"] = true, ["summary"] = summary };
        }
        JObject saved = ThreadStorageService.SaveImported(session.User, parsed.Thread);
        string savedId = saved["id"]?.ToString();
        summary["title"] = saved["title"];
        return new JObject
        {
            ["success"] = true,
            ["threadId"] = savedId,
            ["thread"] = ThreadStorageService.GetThreadIndex(session.User).OfType<JObject>().FirstOrDefault(r => r["id"]?.ToString() == savedId),
            ["summary"] = summary
        };
    }

    /// <summary>Reads an optional whole number from a request: <paramref name="fallback"/> when absent or null, an error when it is not an integer or is outside the range.</summary>
    private static bool TryReadInt(JObject input, string key, int fallback, int min, int max, out int value, out string error)
    {
        value = fallback;
        error = null;
        JToken token = input[key];
        if (token is null || token.Type == JTokenType.Null)
        {
            return true;
        }
        if (token is JValue { Type: JTokenType.Integer, Value: long number } && number >= min && number <= max)
        {
            value = (int)number;
            return true;
        }
        error = $"{key} must be a whole number from {min} to {max}.";
        return false;
    }
}
