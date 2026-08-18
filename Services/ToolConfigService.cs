using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Per-tool runtime configuration with two-level layering: per-assistant overrides
/// per-user defaults.
///
/// <para>Storage:</para>
/// <list type="bullet">
/// <item><b>User layer</b> — <c>userSettings.toolConfig[toolId]</c>. The user's personal default
/// for the tool, used when no assistant override is set or no assistant is known.</item>
/// <item><b>Assistant layer</b> — <c>assistant.toolConfig[toolId]</c>. Set per-assistant in the
/// assistant editor; read via <see cref="AssistantResolver"/> so it inherits down the
/// <c>extends</c> chain. Lets the same tool behave differently for "Yuki" vs "Code-Reviewer".</item>
/// </list>
///
/// <para>Reads layer assistant on top of user (deep merge — assistant wins per-key but doesn't
/// nuke unrelated keys the user set).</para></summary>
public static class ToolConfigService
{
    public const string Key = "toolConfig";

    private static readonly JsonMergeSettings DeepMerge = new()
    {
        MergeArrayHandling = MergeArrayHandling.Replace,
        MergeNullValueHandling = MergeNullValueHandling.Merge
    };

    /// <summary>Returns the effective config block for a tool: user defaults overlaid with the
    /// resolved assistant's config. Empty JObject if nothing is set.</summary>
    public static JObject GetConfig(string toolId, User user, string assistantId = null)
    {
        if (string.IsNullOrEmpty(toolId))
        {
            return [];
        }
        JObject merged = [];
        if (user is not null)
        {
            JObject personal = SettingsService.GetUserSettings(user);
            if (personal[Key]?[toolId] is JObject userConfig)
            {
                merged.Merge(userConfig.DeepClone(), DeepMerge);
            }
        }
        if (!string.IsNullOrEmpty(assistantId) && user is not null)
        {
            ResolvedAssistant resolved = AssistantResolver.Resolve(assistantId, user);
            if (resolved.ToolConfig.TryGetValue(toolId, out JObject asstConfig) && asstConfig is not null)
            {
                merged.Merge(asstConfig.DeepClone(), DeepMerge);
            }
        }
        return merged;
    }

    /// <summary>Replaces the user-layer config block for a tool. Pass an empty JObject to clear.
    /// For per-assistant overrides, edit the assistant directly via the assistant save endpoint.</summary>
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
