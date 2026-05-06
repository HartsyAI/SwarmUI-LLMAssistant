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

    /// <summary>Creates or updates a tool definition. Accepts an optional <c>scope</c>.
    /// The <c>tool</c> field may be either a JObject or a JSON string (the UI sends the latter).</summary>
    public static async Task<JObject> LLMAssistantSaveTool(Session session, JObject rawInput)
    {
        JObject toolData = rawInput["tool"] as JObject;
        if (toolData is null && rawInput["tool"]?.Type == JTokenType.String)
        {
            try { toolData = JObject.Parse(rawInput["tool"].ToString()); }
            catch { /* fall through to error */ }
        }
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

    /// <summary>Returns the calling user's per-tool config block (eg generate_image's default
    /// preset, file_write's extra extensions). Returns an empty object if nothing is configured.</summary>
    public static async Task<JObject> LLMAssistantGetToolConfig(Session session, string toolId)
    {
        if (string.IsNullOrEmpty(toolId))
        {
            return new JObject { ["success"] = false, ["error"] = "toolId is required" };
        }
        return new JObject
        {
            ["success"] = true,
            ["toolId"] = toolId,
            ["config"] = ToolConfigService.GetConfig(toolId, session.User)
        };
    }

    /// <summary>Replaces the calling user's per-tool config block. The <c>config</c> field may
    /// be a JObject or a JSON string. Pass an empty object to clear it.</summary>
    public static async Task<JObject> LLMAssistantSetToolConfig(Session session, JObject rawInput)
    {
        string toolId = rawInput["toolId"]?.ToString();
        if (string.IsNullOrEmpty(toolId))
        {
            return new JObject { ["success"] = false, ["error"] = "toolId is required" };
        }
        JObject config = rawInput["config"] as JObject;
        if (config is null && rawInput["config"]?.Type == JTokenType.String)
        {
            try { config = JObject.Parse(rawInput["config"].ToString()); }
            catch { /* fall through */ }
        }
        config ??= [];
        ToolConfigService.SetConfig(toolId, config, session.User);
        return new JObject { ["success"] = true, ["toolId"] = toolId, ["config"] = config };
    }

    /// <summary>Lists the calling user's saved T2I presets — used by the generate_image tool
    /// config UI to populate the default-preset dropdown.</summary>
    public static async Task<JObject> LLMAssistantGetImagePresets(Session session)
    {
        JArray presets = [];
        if (session?.User is not null)
        {
            foreach (Text2Image.T2IPreset preset in session.User.GetAllPresets())
            {
                presets.Add(new JObject
                {
                    ["title"] = preset.Title,
                    ["description"] = preset.Description ?? ""
                });
            }
        }
        return new JObject { ["success"] = true, ["presets"] = presets };
    }

    /// <summary>Manually execute a tool (dev affordance, bypasses LLM).
    /// The <c>arguments</c> field may be a JObject or a JSON string.</summary>
    public static async Task<JObject> LLMAssistantExecuteTool(Session session, JObject rawInput)
    {
        string toolId = rawInput["toolId"]?.ToString();
        JObject args = (rawInput["arguments"] ?? rawInput["args"]) as JObject;
        if (args is null)
        {
            JToken argToken = rawInput["arguments"] ?? rawInput["args"];
            if (argToken?.Type == JTokenType.String)
            {
                try { args = JObject.Parse(argToken.ToString()); }
                catch { /* fall through */ }
            }
        }
        args ??= new JObject();
        if (string.IsNullOrEmpty(toolId))
        {
            return new JObject { ["success"] = false, ["error"] = "toolId is required" };
        }
        // Manual exec doesn't carry assistant/thread context; tools receive null and fall back
        // to user-level config. Honors the same per-handler perm gate as agentic execution.
        JObject result = await ToolExecutorService.ExecuteTool(toolId, args, session, assistantId: null, threadId: null, modelId: null);
        return new JObject
        {
            ["success"] = true,
            ["result"] = result
        };
    }
}
