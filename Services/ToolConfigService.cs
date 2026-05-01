using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Per-user, per-tool runtime configuration (eg <c>generate_image</c>'s default preset,
/// <c>file_write</c>'s extra allowed extensions). Stored in the user's personal settings under
/// a top-level <c>toolConfig</c> JObject keyed by tool ID. Kept separate from the tool definition
/// (which lives in the <c>tools</c> dict) so that built-in tool defs stay shared/baseline while
/// each user can still tune behavior independently.</summary>
public static class ToolConfigService
{
    public const string Key = "toolConfig";

    /// <summary>Returns the config block for a specific tool, or an empty JObject if none.
    /// Reads from the user's personal layer only — config is intentionally per-user.</summary>
    public static JObject GetConfig(string toolId, User user)
    {
        if (string.IsNullOrEmpty(toolId) || user is null)
        {
            return [];
        }
        JObject personal = SettingsService.GetUserSettings(user);
        return personal[Key]?[toolId] as JObject ?? [];
    }

    /// <summary>Replaces the config block for a specific tool. Pass an empty JObject to clear it.</summary>
    public static void SetConfig(string toolId, JObject config, User user)
    {
        if (string.IsNullOrEmpty(toolId) || user is null)
        {
            return;
        }
        JObject personal = SettingsService.GetUserSettings(user);
        JObject all = personal[Key] as JObject;
        if (all is null)
        {
            all = [];
            personal[Key] = all;
        }
        all[toolId] = config ?? [];
        SettingsService.ReplaceUserSettings(user, personal);
    }
}
