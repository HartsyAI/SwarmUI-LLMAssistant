using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Onboarding;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

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
        // Only include "error" on failure (a present "error" key — even null — is logged as an error).
        if (!deleted)
        {
            return new JObject { ["success"] = false, ["error"] = "Tool not found or cannot be deleted (built-in or not permitted)." };
        }
        return new JObject { ["success"] = true };
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
    /// config UI to populate the default-preset dropdown, and by the Companion overlay's
    /// "Suggest a preset" button. The <c>summary</c> field surfaces the same one-line breakdown
    /// (model, steps, sampler, dimensions, LoRAs) that the LLM sees when picking presets via
    /// the agentic tool flow, so the Companion has enough context to recommend intelligently.</summary>
    public static async Task<JObject> LLMAssistantGetImagePresets(Session session)
    {
        JArray presets = [];
        if (session?.User is not null)
        {
            foreach (SwarmUI.Text2Image.T2IPreset preset in session.User.GetAllPresets())
            {
                presets.Add(new JObject
                {
                    ["title"] = preset.Title,
                    ["description"] = preset.Description ?? "",
                    ["summary"] = GenerateImageTool.FormatPresetSummary(preset)
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
        // Optional context (sent by the in-chat tool picker) so per-assistant tool config applies and the
        // result/asset associates with the active thread. Null for the Settings "Run Test" caller.
        string assistantId = rawInput["assistantId"]?.ToString();
        string threadId = rawInput["threadId"]?.ToString();
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
        // The page names the saved message as it names the row it drew, so the row can be deleted or answered from at once. A name the chat holds is refused before anything runs.
        string messageId = rawInput["messageId"]?.ToString();
        if (!string.IsNullOrEmpty(messageId) && !ThreadStorageService.IsValidMessageId(messageId))
        {
            return new JObject { ["success"] = false, ["error"] = $"messageId must be 1 to {ThreadStorageService.MaxMessageIdChars} letters, digits, dashes or underscores." };
        }
        if (!string.IsNullOrEmpty(messageId) && !string.IsNullOrEmpty(threadId) && session?.User is not null
            && MessageActions.Find(ThreadStorageService.GetThread(session.User, threadId), messageId) is not null)
        {
            return new JObject { ["success"] = false, ["error"] = $"The chat already has a message '{messageId}'." };
        }
        // Honors the same per-handler permission gate as agentic execution (enforced inside ExecuteTool),
        // so this endpoint is safe at the chat permission level. assistant/thread context is optional.
        JObject result = await ToolExecutorService.ExecuteTool(toolId, args, session, assistantId: assistantId, threadId: threadId, modelId: null);

        // When invoked from the in-chat picker (threadId present), persist the call as an assistant
        // message so it survives reload and shows in the thread's Assets — identical to a model-driven call.
        string callId = rawInput["callId"]?.ToString();
        if (string.IsNullOrEmpty(callId))
        {
            callId = $"tool-{Guid.NewGuid():N}";
        }
        // The id the call was saved under: not the one asked for when another request took that while the tool ran.
        string savedAs = null;
        if (!string.IsNullOrEmpty(threadId) && session?.User is not null)
        {
            try
            {
                JObject call = new()
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["toolCalls"] = new JArray { new JObject { ["id"] = callId, ["name"] = toolId, ["arguments"] = args, ["result"] = result } },
                    ["meta"] = new JObject { ["manualTool"] = true },
                };
                if (!string.IsNullOrEmpty(messageId))
                {
                    call["id"] = messageId;
                }
                JObject appended = ThreadStorageService.AppendMessage(session.User, threadId, call);
                // A tool run by hand in a chat ends the first run, as a message does. One run outside a chat (a test from Settings) does not come here.
                if (appended is not null)
                {
                    savedAs = call["id"]?.ToString();
                    OnboardingService.NoteMessage(session.User);
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[LLMAssistant] Failed to persist manual tool call to thread {threadId}: {ex.Message}");
            }
        }
        JObject reply = new()
        {
            ["success"] = true,
            ["result"] = result,
            ["callId"] = callId,
        };
        if (savedAs is not null)
        {
            reply["messageId"] = savedAs;
        }
        return reply;
    }
}
