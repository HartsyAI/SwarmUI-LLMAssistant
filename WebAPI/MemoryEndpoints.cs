using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Memory;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Endpoints for the calling user's memory notes (read / clear). Both endpoints
/// operate strictly on <c>session.User</c>'s own notes — there is no addressing scheme that
/// could leak another user's data, by design. Notes are otherwise written by the model via the
/// <c>memory_write</c> built-in tool (review, edit and undo come with a later phase).</summary>
public static class MemoryEndpoints
{
    /// <summary>Returns the calling user's memory: <c>profile</c> (the preferred name, pronouns, bio cut short, current work and <c>noteCount</c>: what the welcome screen reads, kept small),
    /// <c>limits</c>, and <c>state</c> (<c>ok</c>, <c>lossy</c>, <c>corrupt</c> or <c>newer</c>: how the stored notes read). With <paramref name="includeNotes"/> also <c>notes</c>: every note (id,
    /// text cut to 500 characters with <c>truncated</c>, kind, origin, threadId, created, lastUsed, pinned, slot, negative) in the order they are shown in the prompt.</summary>
    public static async Task<JObject> LLMAssistantGetUserProfile(Session session, bool includeNotes = false)
    {
        await Task.CompletedTask;
        try
        {
            MemoryStore store = session?.User is null ? null : MemoryService.StoreFor(session.User);
            MemoryNoteSet set = store?.Load() ?? new MemoryNoteSet();
            JObject reply = new()
            {
                ["success"] = true,
                ["profile"] = MemoryService.ProfileSummary(set),
                ["state"] = (store?.Status() ?? MemoryLoadStatus.None) switch { MemoryLoadStatus.Lossy => "lossy", MemoryLoadStatus.Corrupt => "corrupt", MemoryLoadStatus.Newer => "newer", _ => "ok" },
                ["limits"] = new JObject
                {
                    ["maxNotes"] = MemoryNoteSet.MaxNotes,
                    ["maxNoteChars"] = MemoryNoteSet.MaxNoteChars,
                    ["maxPinned"] = MemoryNoteSet.MaxPinned,
                    ["promptBudgetChars"] = MemoryRecall.PromptBudgetChars
                }
            };
            if (includeNotes)
            {
                reply["notes"] = MemoryService.NotesView(set);
            }
            return reply;
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>The automatic extractor's recent changes for the calling user, newest first (at most <see cref="MemoryStore.MaxChangeSets"/>): each with what was added, updated (and what it said before),
    /// removed and dropped to make room. Request: none.</summary>
    public static async Task<JObject> LLMAssistantGetMemoryChanges(Session session)
    {
        await Task.CompletedTask;
        try
        {
            IReadOnlyList<MemoryChangeSet> sets = session?.User is null ? [] : MemoryService.StoreFor(session.User).Changes();
            return new JObject { ["success"] = true, ["changes"] = new JArray(sets.Select(s => s.ToClientJson())) };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Undoes one automatic change set of the calling user: notes that were added are removed, updated ones get their old text back, removed or dropped ones come back as they were. A note changed
    /// since is left alone. Reply: <c>{ success, found, undone, skipped, refused }</c>. Request: <c>{ changeId }</c>.</summary>
    public static async Task<JObject> LLMAssistantUndoMemoryChange(Session session, string changeId)
    {
        await Task.CompletedTask;
        try
        {
            if (session?.User is null || string.IsNullOrWhiteSpace(changeId))
            {
                return new JObject { ["success"] = false, ["error"] = "changeId is required." };
            }
            MemoryUndoResult result = MemoryService.StoreFor(session.User).Undo(changeId);
            return new JObject { ["success"] = true, ["found"] = result.Found, ["undone"] = result.Undone, ["skipped"] = result.Skipped, ["refused"] = result.Refused };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>The calling user's extractor settings: <c>{ success, settings: { enabled, model, neverSave[] }, limits }</c>. Only the user's own layer is read.</summary>
    public static async Task<JObject> LLMAssistantGetMemorySettings(Session session)
    {
        await Task.CompletedTask;
        MemoryExtractionSettings s = MemoryExtractionSettings.ForUser(session?.User);
        return new JObject { ["success"] = true, ["settings"] = SettingsJson(s), ["limits"] = new JObject { ["maxNeverSave"] = MemoryExtractionSettings.MaxNeverSave, ["maxNeverSaveChars"] = MemoryExtractionSettings.MaxNeverSaveChars, ["maxPerHour"] = MemoryExtractionService.MaxPerHour } };
    }

    private static JObject SettingsJson(MemoryExtractionSettings s) => new() { ["enabled"] = s.Enabled, ["model"] = s.Model, ["neverSave"] = new JArray(s.NeverSave) };

    /// <summary>Changes the calling user's extractor settings; only the fields sent are changed. <c>enabled</c> (bool), <c>model</c> (string of at most 200 characters; empty follows the chat model),
    /// <c>neverSave</c> (array of at most 50 strings of at most 200 characters; an entry between slashes must be a valid regular expression). Anything else is refused with the reason and nothing is
    /// changed. The settings live in the user's own layer only. Reply: <c>{ success, settings }</c>.</summary>
    public static async Task<JObject> LLMAssistantSetMemorySettings(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Authentication required." };
        }
        MemoryExtractionSettings current = MemoryExtractionSettings.ForUser(session.User);
        bool enabled = current.Enabled;
        string model = current.Model;
        List<string> never = [.. current.NeverSave];
        if (rawInput?["enabled"] is JToken e && e.Type != JTokenType.Null)
        {
            if (e.Type != JTokenType.Boolean)
            {
                return new JObject { ["success"] = false, ["error"] = "enabled must be true or false." };
            }
            enabled = (bool)e;
        }
        if (rawInput?["model"] is JToken m && m.Type != JTokenType.Null)
        {
            if (m.Type != JTokenType.String || ((string)m).Trim().Length > 200)
            {
                return new JObject { ["success"] = false, ["error"] = "model must be a string of at most 200 characters." };
            }
            model = ((string)m).Trim();
        }
        if (rawInput?["neverSave"] is JToken n && n.Type != JTokenType.Null)
        {
            if (n is not JArray list || list.Any(t => t.Type != JTokenType.String))
            {
                return new JObject { ["success"] = false, ["error"] = "neverSave must be an array of strings." };
            }
            if (list.Count > MemoryExtractionSettings.MaxNeverSave)
            {
                return new JObject { ["success"] = false, ["error"] = $"neverSave can have at most {MemoryExtractionSettings.MaxNeverSave} entries." };
            }
            List<string> entries = [];
            foreach (JToken t in list)
            {
                string problem = MemoryNeverSave.Problem((string)t);
                if (problem is not null)
                {
                    return new JObject { ["success"] = false, ["error"] = problem };
                }
                string entry = ((string)t).Trim();
                if (!entries.Contains(entry, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(entry);
                }
            }
            never = entries;
        }
        MemoryExtractionSettings saved = new(enabled, model, never);
        SettingsService.PatchUserSettings(session.User, new JObject { [MemoryExtractionSettings.Key] = SettingsJson(saved) });
        return new JObject { ["success"] = true, ["settings"] = SettingsJson(saved) };
    }

    /// <summary>Runs the memory extractor on an existing chat of the calling user, now, with the same gates, checks and limits as after a chat reply: the master switch, the memory permission
    /// (<c>llm_tool_memory</c>), a session chat, the chat's and the assistant's opt-outs, the never-save list, the user-message-only transcript, one extraction per message, the hourly limit and the
    /// back-off. Request: <c>{ threadId, messageId?, model? }</c>: <c>messageId</c> names a message the user wrote on the chat's active branch (default: the latest one); <c>model</c> is the chat model to
    /// follow when the settings name none. Reply: <c>{ success, status, detail, changes? }</c> with <c>status</c> <c>saved</c> (then <c>changes</c> is the change set, as <c>LLMAssistantGetMemoryChanges</c>
    /// lists it), <c>nothing</c>, <c>failed</c>, <c>cancelled</c> or <c>skipped</c> (<c>detail</c> says why). The call waits for the model; it is stopped by <c>LLMAssistantStopGeneration</c> for the chat.</summary>
    public static async Task<JObject> LLMAssistantExtractMemory(Session session, string threadId, string messageId = "", string model = "")
    {
        try
        {
            if (session?.User is null || string.IsNullOrWhiteSpace(threadId))
            {
                return new JObject { ["success"] = false, ["error"] = "threadId is required." };
            }
            if (!session.User.HasPermission(LLMAssistantAPI.PermToolMemory))
            {
                return new JObject { ["success"] = false, ["error"] = "Extracting memory needs the llm_tool_memory permission." };
            }
            JObject thread = ThreadStorageService.GetThread(session.User, threadId);
            if (thread is null)
            {
                return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
            }
            if (!string.IsNullOrWhiteSpace(messageId)
                && !ThreadStorageService.GetActivePath(thread).Any(m => m["id"]?.ToString() == messageId.Trim() && m["role"]?.ToString() == Roles.User))
            {
                return new JObject { ["success"] = false, ["error"] = "messageId must name a message the user wrote on this chat's active branch." };
            }
            using MemoryExtractionSlot slot = MemoryExtractionRegistry.Reserve(session.User.UserID, threadId);
            if (slot is null)
            {
                return new JObject { ["success"] = true, ["status"] = "skipped", ["detail"] = "an extraction is already running for this chat" };
            }
            ExtractionOutcome outcome = await MemoryExtractionService.ExtractForThreadAsync(session, threadId, model?.Trim(), slot, messageId?.Trim());
            JObject reply = new() { ["success"] = true, ["status"] = outcome.Status, ["detail"] = outcome.Detail };
            if (outcome.Changes is not null)
            {
                reply["changes"] = outcome.Changes.ToClientJson();
            }
            return reply;
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Clears every note of the calling user (and the old profile it was migrated from). Irreversible.</summary>
    public static async Task<JObject> LLMAssistantClearUserProfile(Session session)
    {
        await Task.CompletedTask;
        try
        {
            MemoryService.Clear(session?.User);
            return new JObject { ["success"] = true };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
