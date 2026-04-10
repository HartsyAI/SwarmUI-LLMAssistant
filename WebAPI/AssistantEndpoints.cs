using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>API endpoints for assistant CRUD and activation.</summary>
public static class AssistantEndpoints
{
    /// <summary>Returns all assistants and the active assistant ID.</summary>
    public static async Task<JObject> LLMAssistantGetAssistants(Session session)
    {
        JObject settings = SettingsService.GetSettings();
        return new JObject
        {
            ["success"] = true,
            ["assistants"] = AssistantService.GetAssistantList(settings),
            ["activeAssistantId"] = AssistantService.GetActiveAssistantId(settings)
        };
    }

    /// <summary>Returns a single assistant by ID.</summary>
    public static async Task<JObject> LLMAssistantGetAssistant(Session session, string assistantId)
    {
        JObject assistant = AssistantService.GetAssistant(assistantId);
        return new JObject
        {
            ["success"] = true,
            ["assistant"] = assistant
        };
    }

    /// <summary>Creates or updates an assistant.</summary>
    public static async Task<JObject> LLMAssistantSaveAssistant(Session session, JObject rawInput)
    {
        JObject assistantData = rawInput["assistant"] as JObject;
        if (assistantData is null)
        {
            return new JObject { ["success"] = false, ["error"] = "No assistant data provided." };
        }
        string id = AssistantService.SaveAssistant(assistantData);
        return new JObject { ["success"] = true, ["id"] = id };
    }

    /// <summary>Deletes a custom assistant.</summary>
    public static async Task<JObject> LLMAssistantDeleteAssistant(Session session, string assistantId)
    {
        if (assistantId == AssistantConstants.DefaultId)
        {
            return new JObject { ["success"] = false, ["error"] = "Cannot delete the default assistant." };
        }
        bool deleted = AssistantService.DeleteAssistant(assistantId);
        return new JObject
        {
            ["success"] = deleted,
            ["error"] = deleted ? null : "Assistant not found."
        };
    }

    /// <summary>Sets the active assistant.</summary>
    public static async Task<JObject> LLMAssistantSetActiveAssistant(Session session, string assistantId)
    {
        AssistantService.SetActiveAssistant(assistantId);
        return new JObject { ["success"] = true };
    }
}
