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
