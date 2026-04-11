using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>API endpoints for tool CRUD and manual execution. User-scoped: reads return the
/// caller's merged view (shared ⊕ personal); writes target personal unless <c>scope: "shared"</c>
/// is passed and the caller holds <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
public static class ToolEndpoints
{
    /// <summary>Returns all registered tool definitions visible to the caller.</summary>
    public static async Task<JObject> LLMAssistantGetTools(Session session)
    {
        JObject settings = SettingsService.GetMergedSettings(session.User);
        return new JObject
        {
            ["success"] = true,
            ["tools"] = ToolRegistryService.GetToolList(settings, session.User),
            ["canWriteShared"] = session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false
        };
    }

    /// <summary>Returns a single tool definition by ID from the caller's merged view.</summary>
    public static async Task<JObject> LLMAssistantGetTool(Session session, string toolId)
    {
        JObject tool = ToolRegistryService.GetTool(toolId, user: session.User);
        if (tool is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Tool not found." };
        }
        return new JObject
        {
            ["success"] = true,
            ["tool"] = tool
        };
    }

    /// <summary>Creates or updates a tool definition. Accepts an optional <c>scope</c>.</summary>
    public static async Task<JObject> LLMAssistantSaveTool(Session session, JObject rawInput)
    {
        JObject toolData = rawInput["tool"] as JObject;
        if (toolData is null)
        {
            return new JObject { ["success"] = false, ["error"] = "No tool data provided." };
        }
        string scope = rawInput["scope"]?.ToString();
        string id = ToolRegistryService.SaveTool(toolData, session.User, scope);
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

    /// <summary>Deletes a tool. Auto-detects scope if not provided.</summary>
    public static async Task<JObject> LLMAssistantDeleteTool(Session session, string toolId, string scope = null)
    {
        bool deleted = ToolRegistryService.DeleteTool(toolId, session.User, scope);
        return new JObject
        {
            ["success"] = deleted,
            ["error"] = deleted ? null : "Tool not found or cannot be deleted (built-in or not permitted)."
        };
    }

    /// <summary>Manually execute a tool (dev affordance, bypasses LLM).</summary>
    public static async Task<JObject> LLMAssistantExecuteTool(Session session, JObject rawInput)
    {
        string toolId = rawInput["toolId"]?.ToString();
        JObject args = rawInput["args"] as JObject ?? new JObject();
        if (string.IsNullOrEmpty(toolId))
        {
            return new JObject { ["success"] = false, ["error"] = "toolId is required" };
        }
        JObject result = await ToolExecutorService.ExecuteTool(toolId, args, session);
        return new JObject
        {
            ["success"] = true,
            ["result"] = result
        };
    }
}
