using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Tools;
using SwarmUI.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Validates and executes tools by ID with args. Wraps exceptions into structured error results.</summary>
public static class ToolExecutorService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Maps built-in tool IDs to the permission required to execute them.
    /// Custom (user-defined) tools have no entry and pass through.</summary>
    private static readonly Dictionary<string, PermInfo> BuiltInToolPermissions = new()
    {
        [ToolConstants.GenerateImage] = LLMAssistantAPI.PermToolGenerateImage,
        [ToolConstants.WebSearch] = LLMAssistantAPI.PermToolWebSearch,
        [ToolConstants.FileRead] = LLMAssistantAPI.PermToolFileRead,
        [ToolConstants.FileWrite] = LLMAssistantAPI.PermToolFileWrite,
        [ToolConstants.HttpRequest] = LLMAssistantAPI.PermToolHttpRequest,
        [ToolConstants.ShellExec] = LLMAssistantAPI.PermToolShellExec,
        [ToolConstants.MemoryWrite] = LLMAssistantAPI.PermToolMemory,
        [ToolConstants.MemoryRead] = LLMAssistantAPI.PermToolMemory,
        [ToolConstants.SwarmDocs] = LLMAssistantAPI.PermToolSwarmDocs,
    };

    /// <summary>Executes a tool by ID with the given arguments.</summary>
    public static async Task<JObject> ExecuteTool(string toolId, JObject args, Session session, CancellationToken ct = default)
    {
        try
        {
            JObject tool = ToolRegistryService.GetTool(toolId, user: session?.User);
            if (tool is null)
            {
                return Error($"Tool not found: {toolId}");
            }
            if (tool["enabled"]?.Value<bool>() == false)
            {
                return Error($"Tool is disabled: {toolId}");
            }
            // Per-tool SwarmUI permission check for built-ins
            if (BuiltInToolPermissions.TryGetValue(toolId, out PermInfo requiredPerm))
            {
                User user = session?.User;
                if (user is null || !user.HasPermission(requiredPerm))
                {
                    Logs.Warning($"[LLMAssistant] User {(user?.UserID ?? "<none>")} lacks permission '{requiredPerm.ID}' to run tool {toolId}");
                    return Error($"You do not have permission to use the '{toolId}' tool. Ask an admin to grant '{requiredPerm.DisplayName}'.");
                }
            }
            string handlerId = tool["handlerId"]?.ToString();
            ToolHandler handler = ToolRegistryService.GetHandler(handlerId);
            if (handler is null)
            {
                return Error($"No handler registered for tool: {toolId} (handlerId={handlerId})");
            }
            // Basic validation: ensure required params are present
            string validationError = ValidateArgs(tool["parameters"] as JObject, args);
            if (validationError is not null)
            {
                return Error(validationError);
            }
            Logs.Debug($"[LLMAssistant] Executing tool {toolId} with args: {args?.ToString(Newtonsoft.Json.Formatting.None)}");
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultTimeout);
            JObject result = await handler.Execute(args ?? new JObject(), session, timeoutCts.Token);
            if (result is null)
            {
                return Error("Tool returned null result.");
            }
            if (result["success"] is null)
            {
                result["success"] = true;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return Error("Tool execution timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Tool {toolId} threw: {ex.Message}");
            return Error(ex.Message);
        }
    }

    private static JObject Error(string message)
    {
        return new JObject
        {
            ["success"] = false,
            ["error"] = message
        };
    }

    private static string ValidateArgs(JObject schema, JObject args)
    {
        if (schema is null)
        {
            return null;
        }
        if (schema["required"] is JArray required)
        {
            foreach (JToken req in required)
            {
                string name = req.ToString();
                if (args is null || args[name] is null || args[name].Type == JTokenType.Null)
                {
                    return $"Missing required parameter: {name}";
                }
            }
        }
        return null;
    }
}
