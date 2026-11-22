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
