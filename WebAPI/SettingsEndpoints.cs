using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Settings CRUD endpoints.</summary>
public static class SettingsEndpoints
{
    public static async Task<JObject> LLMAssistantGetSettings(Session session)
    {
        return new JObject
        {
            ["success"] = true,
            ["settings"] = SettingsService.GetSettings()
        };
    }

    public static async Task<JObject> LLMAssistantSaveSettings(Session session, JObject rawInput)
    {
        JObject incoming = rawInput["settings"] as JObject ?? rawInput;
        JObject saved = SettingsService.SaveSettings(incoming);
        return new JObject
        {
            ["success"] = true,
            ["settings"] = saved
        };
    }

    public static async Task<JObject> LLMAssistantResetSettings(Session session)
    {
        JObject defaults = SettingsService.ResetSettings();
        return new JObject
        {
            ["success"] = true,
            ["settings"] = defaults
        };
    }
}
