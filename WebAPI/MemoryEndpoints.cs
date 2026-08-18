using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Endpoints for the calling user's memory profile (read / clear). Both endpoints
/// operate strictly on <c>session.User</c>'s own profile — there is no addressing scheme that
/// could leak another user's data, by design. Memory contents are otherwise managed by the
/// LLM via the <c>memory_write</c> built-in tool.</summary>
public static class MemoryEndpoints
{
    /// <summary>Returns the calling user's memory profile (or an empty template if unset).
    /// Used by the personalized welcome screen and by future memory-management UIs.</summary>
    public static async Task<JObject> LLMAssistantGetUserProfile(Session session)
    {
        await Task.CompletedTask;
        try
        {
            JObject profile = UserProfileService.GetProfile(session?.User);
            return new JObject { ["success"] = true, ["profile"] = profile };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Clears the calling user's entire memory profile. Irreversible.</summary>
    public static async Task<JObject> LLMAssistantClearUserProfile(Session session)
    {
        await Task.CompletedTask;
        try
        {
            UserProfileService.ClearProfile(session?.User);
            return new JObject { ["success"] = true };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
