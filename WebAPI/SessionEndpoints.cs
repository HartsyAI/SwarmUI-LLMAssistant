using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Per-user session state endpoints — active thread, selected model, etc.
/// These let a headless API client resume exactly where the UI left off without needing
/// to track local state.</summary>
public static class SessionEndpoints
{
    /// <summary>Returns the full session state for the current user.</summary>
    public static async Task<JObject> LLMAssistantGetSessionState(Session session)
    {
        JObject state = SessionStateService.Get(session.User);
        return new JObject
        {
            ["success"] = true,
            ["state"] = state
        };
    }

    /// <summary>Merges the given patch into the current user's session state.
    /// Send <c>null</c> for a key to clear it.</summary>
    public static async Task<JObject> LLMAssistantSetSessionState(Session session, JObject rawInput)
    {
        JObject patch = rawInput?["state"] as JObject ?? rawInput?["patch"] as JObject;
        if (patch is null)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "No 'state' patch object provided."
            };
        }
        JObject newState = SessionStateService.Patch(session.User, patch);
        return new JObject
        {
            ["success"] = true,
            ["state"] = newState
        };
    }
}
