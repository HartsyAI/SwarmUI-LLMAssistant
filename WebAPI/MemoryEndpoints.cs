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
    /// <summary>Returns the calling user's memory: <c>profile</c> in the old profile's shape (what the welcome screen
    /// reads), <c>notes</c> (every note: id, text, kind, origin, threadId, created, lastUsed, pinned, slot, negative, in
    /// the order they are shown in the prompt) and <c>limits</c>.</summary>
    public static async Task<JObject> LLMAssistantGetUserProfile(Session session)
    {
        await Task.CompletedTask;
        try
        {
            MemoryNoteSet set = MemoryService.Load(session?.User);
            return new JObject
            {
                ["success"] = true,
                ["profile"] = set.ToProfileView(),
                ["notes"] = MemoryService.NotesView(session?.User),
                ["limits"] = new JObject
                {
                    ["maxNotes"] = MemoryNoteSet.MaxNotes,
                    ["maxNoteChars"] = MemoryNoteSet.MaxNoteChars,
                    ["maxPinned"] = MemoryNoteSet.MaxPinned,
                    ["promptBudgetChars"] = MemoryRecall.PromptBudgetChars
                }
            };
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
