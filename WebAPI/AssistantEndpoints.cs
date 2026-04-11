using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>API endpoints for assistant CRUD and activation. All endpoints are user-scoped:
/// reads return the caller's merged view (shared ⊕ personal), writes target the caller's
/// personal layer unless they explicitly pass <c>scope: "shared"</c> AND hold
/// <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
public static class AssistantEndpoints
{
    /// <summary>Returns all assistants visible to the caller (shared + personal) and the caller's
    /// active assistant ID. Each entry is tagged with a <c>_scope</c> marker so the UI can show a
    /// "shared"/"personal" badge.</summary>
    public static async Task<JObject> LLMAssistantGetAssistants(Session session)
    {
        JObject settings = SettingsService.GetMergedSettings(session.User);
        return new JObject
        {
            ["success"] = true,
            ["assistants"] = AssistantService.GetAssistantList(settings, session.User),
            ["activeAssistantId"] = AssistantService.GetActiveAssistantId(settings, session.User),
            ["canWriteShared"] = session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false
        };
    }

    /// <summary>Returns a single assistant by ID from the caller's merged view.</summary>
    public static async Task<JObject> LLMAssistantGetAssistant(Session session, string assistantId)
    {
        JObject assistant = AssistantService.GetAssistant(assistantId, user: session.User);
        return new JObject
        {
            ["success"] = true,
            ["assistant"] = assistant
        };
    }

    /// <summary>Creates or updates an assistant. Accepts an optional <c>scope</c> field
    /// ("personal" or "shared"). Defaults to personal. Shared writes require
    /// <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
    public static async Task<JObject> LLMAssistantSaveAssistant(Session session, JObject rawInput)
    {
        JObject assistantData = rawInput["assistant"] as JObject;
        if (assistantData is null)
        {
            return new JObject { ["success"] = false, ["error"] = "No assistant data provided." };
        }
        string scope = rawInput["scope"]?.ToString();
        string id = AssistantService.SaveAssistant(assistantData, session.User, scope);
        if (id is null)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "Not permitted to save to the requested scope (shared writes require llm_shared_write)."
            };
        }
        return new JObject { ["success"] = true, ["id"] = id, ["scope"] = scope ?? SettingsService.ScopePersonal };
    }

    /// <summary>Deletes an assistant. Auto-detects personal vs shared if <c>scope</c> is not
    /// provided. Shared deletes require <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
    public static async Task<JObject> LLMAssistantDeleteAssistant(Session session, string assistantId, string scope = null)
    {
        if (assistantId == AssistantConstants.DefaultId)
        {
            return new JObject { ["success"] = false, ["error"] = "Cannot delete the default assistant." };
        }
        bool deleted = AssistantService.DeleteAssistant(assistantId, session.User, scope);
        return new JObject
        {
            ["success"] = deleted,
            ["error"] = deleted ? null : "Assistant not found or not permitted."
        };
    }

    /// <summary>Sets the active assistant (personal preference, always stored in user layer).</summary>
    public static async Task<JObject> LLMAssistantSetActiveAssistant(Session session, string assistantId)
    {
        AssistantService.SetActiveAssistant(assistantId, session.User);
        return new JObject { ["success"] = true };
    }

    /// <summary>Returns the resolved active assistant object for the caller.</summary>
    public static async Task<JObject> LLMAssistantGetActiveAssistant(Session session)
    {
        JObject settings = SettingsService.GetMergedSettings(session.User);
        JObject assistant = AssistantService.GetActiveAssistant(settings, session.User);
        return new JObject
        {
            ["success"] = true,
            ["activeAssistantId"] = AssistantService.GetActiveAssistantId(settings, session.User),
            ["assistant"] = assistant
        };
    }
}
