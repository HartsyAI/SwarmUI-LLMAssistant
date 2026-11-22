using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;
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
        // Read, change and written under the settings lock, so two partial updates cannot overwrite each other.
        lock (SettingsService.SettingsLock)
        {
            return SetMemorySettings(session, rawInput);
        }
    }

    private static JObject SetMemorySettings(Session session, JObject rawInput)
    {
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
            Logs.Debug($"[LLMAssistant] LLMAssistantExtractMemory failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = "The extraction could not be run." };
        }
    }

    // ---- Reviewing and editing the notes (every route works on session.User's own notes only) ----

    /// <summary>The words for a refusal code of a change to the notes.</summary>
    private static string Explain(string code) => code switch
    {
        "newer" => "These notes were saved by a newer version of this extension, so nothing can be changed until it is updated.",
        "notfound" => "That note is no longer there.",
        "empty" => "A note cannot be empty.",
        "kind" => "That is not a kind of note.",
        "slot" => "The name, pronouns, bio and current work notes keep their kind and stay pinned.",
        "duplicate" => "Another note already says that.",
        "pinlimit" => $"At most {MemoryNoteSet.MaxPinned} notes can be pinned.",
        "full" => $"Memory is full ({MemoryNoteSet.MaxNotes} notes); delete one first.",
        "slotfilled" => "That slot already has a note; change that note instead.",
        "cursor" => "That cursor is not valid.",
        "filter" => "That is not a kind to filter by.",
        _ => "That could not be done."
    };

    /// <summary>A failure reply: <c>error</c> is a sentence, <c>code</c> the short code.</summary>
    private static JObject Failure(string code) => new() { ["success"] = false, ["code"] = code, ["error"] = Explain(code) };

    /// <summary>One page of the calling user's notes, in the order the prompt shows them. Request: <c>{ query?, kind?, limit?, cursor? }</c> (<c>query</c> matches the text in any case, at most 200 characters;
    /// <c>kind</c> is profile, preference, dislike, fact, project, style or pinned; <c>limit</c> 1 to 200, 100 when left out; <c>cursor</c> is the <c>nextCursor</c> of the page before). Reply: <c>{ success, notes[],
    /// total, matched, nextCursor, limits, state }</c> (<c>nextCursor</c> is empty on the last page; each text is cut to 500 characters with <c>truncated</c>), or <c>{ success: false, code, error }</c>.</summary>
    public static async Task<JObject> LLMAssistantListMemoryNotes(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            if (session?.User is null)
            {
                return new JObject { ["success"] = false, ["error"] = "No user." };
            }
            string query = rawInput?["query"]?.Type == JTokenType.String ? (string)rawInput["query"] : "";
            string kind = rawInput?["kind"]?.Type == JTokenType.String ? (string)rawInput["kind"] : "";
            string cursor = rawInput?["cursor"]?.Type is JTokenType.String or JTokenType.Integer ? rawInput["cursor"].ToString() : "";
            int limit = rawInput?["limit"]?.Type == JTokenType.Integer ? (int)Math.Clamp(rawInput["limit"].Value<long>(), 0, MemoryService.MaxListPage) : 0;
            MemoryStore store = MemoryService.StoreFor(session.User);
            JObject page = MemoryService.ListNotes(store.Load(), query, kind, limit, cursor);
            if (page is null)
            {
                return Failure(MemoryService.ListKinds.Contains((kind ?? "").Trim().ToLowerInvariant()) || string.IsNullOrWhiteSpace(kind) ? "cursor" : "filter");
            }
            page["success"] = true;
            page["state"] = store.Status() switch { MemoryLoadStatus.Lossy => "lossy", MemoryLoadStatus.Corrupt => "corrupt", MemoryLoadStatus.Newer => "newer", _ => "ok" };
            page["limits"] = new JObject { ["maxNotes"] = MemoryNoteSet.MaxNotes, ["maxNoteChars"] = MemoryNoteSet.MaxNoteChars, ["maxPinned"] = MemoryNoteSet.MaxPinned };
            return page;
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Adds a note the calling user wrote (origin <c>user</c>). Request: <c>{ text, kind?, pinned?, slot? }</c> (<c>kind</c> is preference, dislike, fact, project or style, fact when left out; <c>slot</c>
    /// is name, pronouns, bio or currentWork and fills that slot with a pinned note when it is empty). Never drops another note to make room. Reply: <c>{ success, note }</c>, or <c>{ success: false, code, error }</c>
    /// (<c>empty</c>, <c>kind</c>, <c>slot</c>, <c>slotfilled</c>, <c>duplicate</c>, <c>full</c>, <c>pinlimit</c>, <c>newer</c>).</summary>
    public static async Task<JObject> LLMAssistantAddMemoryNote(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            if (session?.User is null)
            {
                return new JObject { ["success"] = false, ["error"] = "No user." };
            }
            string text = rawInput?["text"]?.Type == JTokenType.String ? (string)rawInput["text"] : null;
            string kind = rawInput?["kind"]?.Type == JTokenType.String ? (string)rawInput["kind"] : null;
            string slot = rawInput?["slot"]?.Type == JTokenType.String ? (string)rawInput["slot"] : null;
            bool pinned = rawInput?["pinned"]?.Type == JTokenType.Boolean && (bool)rawInput["pinned"];
            MemoryManageResult result = MemoryService.StoreFor(session.User).Add(text, kind, pinned, slot);
            return result.Ok ? new JObject { ["success"] = true, ["note"] = MemoryService.NoteView(result.Note) } : Failure(result.Error);
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Pins or unpins one note of the calling user (a pinned note is always in the prompt; at most 25; the name, pronouns, bio and current work stay pinned). Request: <c>{ id, pinned }</c>.
    /// Reply: <c>{ success, note }</c>, or <c>{ success: false, code, error }</c> (<c>notfound</c>, <c>slot</c>, <c>pinlimit</c>, <c>newer</c>).</summary>
    public static async Task<JObject> LLMAssistantPinMemoryNote(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            string id = rawInput?["id"]?.Type == JTokenType.String ? (string)rawInput["id"] : null;
            if (session?.User is null || string.IsNullOrWhiteSpace(id) || id.Length > 64)
            {
                return Failure("notfound");
            }
            if (rawInput["pinned"]?.Type != JTokenType.Boolean)
            {
                return new JObject { ["success"] = false, ["error"] = "pinned must be true or false." };
            }
            MemoryManageResult result = MemoryService.StoreFor(session.User).Update(id, null, null, (bool)rawInput["pinned"]);
            return result.Ok ? new JObject { ["success"] = true, ["note"] = MemoryService.NoteView(result.Note) } : Failure(result.Error);
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Switches the automatic extractor on or off for one assistant (the per-assistant opt-out). Request: <c>{ assistantId, enabled, scope? }</c> (<c>scope</c> is personal, the default, or shared,
    /// which needs <c>llm_shared_write</c>). Reply: <c>{ success, id, enabled }</c>, or <c>{ success: false, error }</c>.</summary>
    public static async Task<JObject> LLMAssistantSetAssistantMemory(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            string id = rawInput?["assistantId"]?.Type == JTokenType.String ? (string)rawInput["assistantId"] : null;
            if (session?.User is null || string.IsNullOrWhiteSpace(id) || id.Length > 200)
            {
                return new JObject { ["success"] = false, ["error"] = "assistantId is required." };
            }
            if (rawInput["enabled"]?.Type != JTokenType.Boolean)
            {
                return new JObject { ["success"] = false, ["error"] = "enabled must be true or false." };
            }
            // GetAssistant falls back to the default assistant for an id it does not know, which must not be saved as this one.
            bool exists = SettingsService.GetMergedSettings(session.User)["assistants"] is JObject all && all[id] is JObject;
            JObject assistant = exists ? AssistantService.GetAssistant(id, user: session.User) : null;
            if (assistant is null)
            {
                return new JObject { ["success"] = false, ["error"] = "Assistant not found." };
            }
            assistant["memoryExtraction"] = (bool)rawInput["enabled"];
            string scope = rawInput["scope"]?.Type == JTokenType.String ? (string)rawInput["scope"] : null;
            (string saved, string error) = AssistantService.SaveAssistant(assistant, session.User, scope);
            return saved is null
                ? new JObject { ["success"] = false, ["error"] = error ?? "Could not save the assistant." }
                : new JObject { ["success"] = true, ["id"] = saved, ["enabled"] = (bool)rawInput["enabled"] };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>One note in full (the list cuts long texts). Request: <c>{ id }</c>. Reply: <c>{ success, note }</c>.</summary>
    public static async Task<JObject> LLMAssistantGetMemoryNote(Session session, string id)
    {
        await Task.CompletedTask;
        try
        {
            MemoryNote note = session?.User is null || string.IsNullOrWhiteSpace(id) || id.Length > 64 ? null : MemoryService.StoreFor(session.User).Get(id);
            if (note is null)
            {
                return Failure("notfound");
            }
            JObject view = MemoryService.NoteView(note);
            view["text"] = note.Text;
            view["truncated"] = false;
            return new JObject { ["success"] = true, ["note"] = view };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Changes one note of the calling user. Request: <c>{ id, text?, kind?, pinned? }</c> (<c>kind</c> is preference, dislike, fact, project or style; a field left out stays as it is).
    /// Reply: <c>{ success, note }</c>, or <c>{ success: false, code, error }</c>.</summary>
    public static async Task<JObject> LLMAssistantUpdateMemoryNote(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            string id = rawInput?["id"]?.Type == JTokenType.String ? (string)rawInput["id"] : null;
            if (session?.User is null || string.IsNullOrWhiteSpace(id) || id.Length > 64)
            {
                return Failure("notfound");
            }
            string text = rawInput["text"]?.Type == JTokenType.String ? (string)rawInput["text"] : null;
            string kind = rawInput["kind"]?.Type == JTokenType.String ? (string)rawInput["kind"] : null;
            bool? pinned = rawInput["pinned"]?.Type == JTokenType.Boolean ? (bool)rawInput["pinned"] : null;
            MemoryManageResult result = MemoryService.StoreFor(session.User).Update(id, text, kind, pinned);
            return result.Ok
                ? new JObject { ["success"] = true, ["note"] = MemoryService.NoteView(result.Note) }
                : Failure(result.Error);
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Deletes notes of the calling user (final: not journaled). Request: <c>{ ids: [..] }</c>, at most <see cref="MemoryNoteSet.MaxNotes"/>. Reply: <c>{ success, deleted }</c>.</summary>
    public static async Task<JObject> LLMAssistantDeleteMemoryNotes(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            if (session?.User is null || rawInput?["ids"] is not JArray array)
            {
                return new JObject { ["success"] = false, ["error"] = "ids is required." };
            }
            List<string> ids = [.. array.Where(t => t.Type == JTokenType.String).Select(t => (string)t).Where(s => s.Length is > 0 and <= 64).Distinct().Take(MemoryNoteSet.MaxNotes)];
            MemoryManageResult result = MemoryService.StoreFor(session.User).Delete(ids);
            return result.Ok ? new JObject { ["success"] = true, ["deleted"] = result.Count } : Failure(result.Error);
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>The calling user's notes as an export file's content (see the README). Reply: <c>{ success, export }</c>; the export is at most about 1 MB.</summary>
    public static async Task<JObject> LLMAssistantExportMemory(Session session)
    {
        await Task.CompletedTask;
        try
        {
            return session?.User is null
                ? new JObject { ["success"] = false, ["error"] = "No user." }
                : new JObject { ["success"] = true, ["export"] = MemoryService.StoreFor(session.User).Export() };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>What importing a file would do to the calling user's notes, without changing anything: <c>{ success, preview }</c> (counts and the first notes that would be added), or
    /// <c>{ success: false, code, error }</c> when the file cannot be read. Request: <c>{ data }</c>, the file's text (at most 1.5 million characters).</summary>
    public static async Task<JObject> LLMAssistantPreviewMemoryImport(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            string data = rawInput?["data"]?.Type == JTokenType.String ? (string)rawInput["data"] : null;
            if (session?.User is null)
            {
                return new JObject { ["success"] = false, ["error"] = "No user." };
            }
            MemoryImportPlan plan = MemoryService.StoreFor(session.User).PreviewImport(data, new MemoryNeverSave(MemoryExtractionSettings.ForUser(session.User).NeverSave));
            return ImportReply(plan);
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Adds the notes of a file to the calling user's own, never replacing or dropping one (see <see cref="MemoryImport"/>). The plan is worked out again here from the file and the notes as
    /// they are now. Request: <c>{ data }</c>. Reply: <c>{ success, preview }</c> where <c>preview</c> describes what was added.</summary>
    public static async Task<JObject> LLMAssistantImportMemory(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            string data = rawInput?["data"]?.Type == JTokenType.String ? (string)rawInput["data"] : null;
            if (session?.User is null)
            {
                return new JObject { ["success"] = false, ["error"] = "No user." };
            }
            MemoryImportPlan plan = MemoryService.StoreFor(session.User).Import(data, new MemoryNeverSave(MemoryExtractionSettings.ForUser(session.User).NeverSave), out string refused);
            return refused.Length > 0 ? Failure(refused) : ImportReply(plan);
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>The reply for an import plan.</summary>
    private static JObject ImportReply(MemoryImportPlan plan) => plan.Error.Length > 0
        ? new JObject
        {
            ["success"] = false,
            ["code"] = plan.Error,
            ["error"] = plan.Error switch
            {
                "too-large" => "That file is too large to be a memory export.",
                "not-json" => "That file is not valid JSON.",
                "no-notes" => "That file has no list of notes in it.",
                "empty" => "That file is empty.",
                _ => Explain(plan.Error)
            }
        }
        : new JObject { ["success"] = true, ["preview"] = plan.ToClientJson() };

    // ---- Pause, retention and the admin switch ----

    /// <summary>The privacy controls: <c>{ success, pause: { saving, recall }, retention: { unusedDays, min, max }, admin: { extractionDisabled, canWrite }, extractionAllowed }</c>. <c>extractionAllowed</c>
    /// is whether the automatic extractor may run for the calling user right now (their own switch on, not paused, not switched off by an admin).</summary>
    public static async Task<JObject> LLMAssistantGetMemoryControls(Session session)
    {
        await Task.CompletedTask;
        try
        {
            MemoryControls controls = MemoryControls.ForUser(session?.User);
            return new JObject
            {
                ["success"] = true,
                ["pause"] = new JObject { ["saving"] = controls.SavingPaused, ["recall"] = controls.RecallPaused },
                ["retention"] = new JObject { ["unusedDays"] = controls.RetentionDays, ["min"] = MemoryControls.MinRetentionDays, ["max"] = MemoryControls.MaxRetentionDays },
                ["admin"] = new JObject { ["extractionDisabled"] = controls.AdminExtractionDisabled, ["canWrite"] = session?.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false },
                ["extractionAllowed"] = controls.ExtractionAllowed(MemoryExtractionSettings.ForUser(session?.User).Enabled)
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Changes the calling user's own pause switches and retention. Request: any of <c>savingPaused</c>, <c>recallPaused</c> (booleans) and <c>unusedDays</c> (0 for off, otherwise
    /// 7 to 3650). Setting <c>unusedDays</c> applies it at once (not while recall is paused); the reply says what was forgotten in <c>forgotten</c> (it can be undone from the history). Replies with the new controls, like
    /// <see cref="LLMAssistantGetMemoryControls"/>.</summary>
    public static async Task<JObject> LLMAssistantSetMemoryControls(Session session, JObject rawInput)
    {
        try
        {
            if (session?.User is null)
            {
                return new JObject { ["success"] = false, ["error"] = "No user." };
            }
            JObject patch = MemoryControls.UserPatch(rawInput);
            if (patch is null)
            {
                return new JObject { ["success"] = false, ["error"] = "Nothing to change." };
            }
            MemoryControls before = MemoryControls.ForUser(session.User);
            SettingsService.PatchUserSettings(session.User, patch);
            int forgotten = MemoryService.ApplyControlsChange(MemoryService.StoreFor(session.User), session.User.UserID, before, MemoryControls.ForUser(session.User), patch[MemoryControls.RetentionKey] is not null);
            JObject reply = await LLMAssistantGetMemoryControls(session);
            if (patch[MemoryControls.RetentionKey] is not null)
            {
                reply["forgotten"] = forgotten;
            }
            return reply;
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>The admin switch that turns the automatic extractor off for every user, who cannot turn it back on. Needs <c>llm_shared_write</c>. Request: <c>{ extractionDisabled }</c>. It is
    /// kept in the shared settings, is audited, and is read from the shared layer only.</summary>
    public static async Task<JObject> LLMAssistantSetMemoryAdmin(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        try
        {
            if (session?.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) != true)
            {
                return new JObject { ["success"] = false, ["error"] = "Changing this for every user requires llm_shared_write permission." };
            }
            if (rawInput?["extractionDisabled"]?.Type != JTokenType.Boolean)
            {
                return new JObject { ["success"] = false, ["error"] = "extractionDisabled must be true or false." };
            }
            bool disabled = (bool)rawInput["extractionDisabled"];
            lock (SettingsService.SettingsLock)
            {
                SettingsService.ReplaceSharedSettings(MemoryControls.WithAdminSwitch(SettingsService.GetSettings(), disabled));
            }
            AssistantResolver.InvalidateAll();
            AuditLogService.RecordSharedWrite("update", "memory_admin", session.User, new JObject { ["extractionDisabled"] = disabled });
            return await LLMAssistantGetMemoryControls(session);
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
