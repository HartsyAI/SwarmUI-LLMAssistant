using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>API endpoints for tool CRUD and manual execution.</summary>
public static class ToolEndpoints
{
    /// <summary>Returns all registered tool definitions.</summary>
    public static async Task<JObject> LLMAssistantGetTools(Session session)
    {
        JObject settings = SettingsService.GetSettings();
        return new JObject
        {
            ["success"] = true,
            ["tools"] = ToolRegistryService.GetToolList(settings)
        };
    }

    /// <summary>Returns a single tool definition by ID.</summary>
    public static async Task<JObject> LLMAssistantGetTool(Session session, string toolId)
    {
        JObject tool = ToolRegistryService.GetTool(toolId);
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

    /// <summary>Creates or updates a tool definition.</summary>
    public static async Task<JObject> LLMAssistantSaveTool(Session session, JObject rawInput)
    {
        JObject toolData = rawInput["tool"] as JObject;
        if (toolData is null)
        {
            return new JObject { ["success"] = false, ["error"] = "No tool data provided." };
        }
        string id = ToolRegistryService.SaveTool(toolData);
        return new JObject { ["success"] = true, ["id"] = id };
    }

    /// <summary>Deletes a custom tool. Built-in tools cannot be deleted.</summary>
    public static async Task<JObject> LLMAssistantDeleteTool(Session session, string toolId)
    {
        bool deleted = ToolRegistryService.DeleteTool(toolId);
        return new JObject
        {
            ["success"] = deleted,
            ["error"] = deleted ? null : "Tool not found or cannot be deleted (built-in)."
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
