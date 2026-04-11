using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Per-user session state for the LLM Assistant (active thread, last model, etc.).
/// Kept separate from the global <see cref="SettingsService"/> so different users can have
/// independent "resume where I left off" state without clobbering each other.</summary>
public static class SessionStateService
{
    public const string DataName = "llmassistant";
    public const string StateKey = "session_state";

    /// <summary>Known keys for session state entries.</summary>
    public static class Keys
    {
        public const string ActiveThreadId = "activeThreadId";
        public const string ActiveAssistantId = "activeAssistantId";
        public const string CurrentModel = "currentModel";
        public const string CurrentVisionModel = "currentVisionModel";
    }

    /// <summary>Loads the full session state object for a user. Always returns a non-null JObject.</summary>
    public static JObject Get(User user)
    {
        if (user is null)
        {
            return [];
        }
        string raw = user.GetGenericData(DataName, StateKey);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }
        try
        {
            return JObject.Parse(raw);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Replaces the full session state object for a user.</summary>
    public static void Replace(User user, JObject state)
    {
        if (user is null)
        {
            return;
        }
        state ??= [];
        user.SaveGenericData(DataName, StateKey, state.ToString(Formatting.None));
    }

    /// <summary>Merges the given patch into the user's current session state, then persists.
    /// Null values in the patch remove the key.</summary>
    public static JObject Patch(User user, JObject patch)
    {
        JObject current = Get(user);
        if (patch is not null)
        {
            foreach (KeyValuePair<string, JToken> kvp in patch)
            {
                if (kvp.Value is null || kvp.Value.Type == JTokenType.Null)
                {
                    current.Remove(kvp.Key);
                }
                else
                {
                    current[kvp.Key] = kvp.Value.DeepClone();
                }
            }
        }
        Replace(user, current);
        return current;
    }

    /// <summary>Convenience: gets the active thread ID for a user, or null.</summary>
    public static string GetActiveThreadId(User user)
    {
        return Get(user)[Keys.ActiveThreadId]?.ToString();
    }

    /// <summary>Convenience: sets (or clears) the active thread ID for a user.</summary>
    public static void SetActiveThreadId(User user, string threadId)
    {
        JObject patch = new()
        {
            [Keys.ActiveThreadId] = string.IsNullOrEmpty(threadId) ? null : threadId
        };
        Patch(user, patch);
    }
}
