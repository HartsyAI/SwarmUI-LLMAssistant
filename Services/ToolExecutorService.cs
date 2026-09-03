using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Tools;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Validates and executes tools by ID with args. Wraps exceptions into structured error results.</summary>
public static class ToolExecutorService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Maps built-in tool <b>handler IDs</b> to the permission required to execute them.
    /// Keyed on the handler (the code that actually runs), not the tool definition's id alias —
    /// otherwise a user could define a custom tool <c>{ id: "harmless", handlerId: "shell_exec" }</c>
    /// and bypass the shell_exec permission gate. Custom handlers (no entry here) require an
    /// explicit entry to run; if none, the tool runs with no permission gate beyond enable-state.</summary>
    private static readonly Dictionary<string, PermInfo> HandlerPermissions = new()
    {
        [ToolConstants.GenerateImage] = LLMAssistantAPI.PermToolGenerateImage,
        [ToolConstants.CreateImagePreset] = LLMAssistantAPI.PermToolCreateImagePreset,
        [ToolConstants.CaptionImage] = LLMAssistantAPI.PermToolVision,
        [ToolConstants.FuseImageDescriptions] = LLMAssistantAPI.PermToolVision,
        [ToolConstants.BatchCaptionFolder] = LLMAssistantAPI.PermToolVision,
        [ToolConstants.WebSearch] = LLMAssistantAPI.PermToolWebSearch,
        [ToolConstants.FileRead] = LLMAssistantAPI.PermToolFileRead,
        [ToolConstants.FileWrite] = LLMAssistantAPI.PermToolFileWrite,
        [ToolConstants.HttpRequest] = LLMAssistantAPI.PermToolHttpRequest,
        [ToolConstants.ShellExec] = LLMAssistantAPI.PermToolShellExec,
        [ToolConstants.MemoryWrite] = LLMAssistantAPI.PermToolMemory,
        [ToolConstants.MemoryRead] = LLMAssistantAPI.PermToolMemory,
        [ToolConstants.SwarmDocs] = LLMAssistantAPI.PermToolSwarmDocs,
        [ToolConstants.SetLedProfile] = LLMAssistantAPI.PermToolDeviceAction,
        [ToolConstants.SetVolume] = LLMAssistantAPI.PermToolDeviceAction,
        [ToolConstants.MuteMic] = LLMAssistantAPI.PermToolDeviceAction,
    };

    /// <summary>Executes a tool by ID with the given arguments and request context.</summary>
    public static async Task<JObject> ExecuteTool(string toolId, JObject args, Session session, string assistantId = null, string threadId = null, string modelId = null, CancellationToken ct = default)
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
            // Enforce the assistant's resolved allowlist (null assistantId = dev "Run Test", perms only).
            // ToolsEnabled is deliberately not checked: the per-thread toggle may override it on.
            if (!string.IsNullOrEmpty(assistantId))
            {
                ResolvedAssistant resolved = AssistantResolver.Resolve(assistantId, session?.User);
                if (!resolved.EnabledToolIds.Contains(toolId))
                {
                    return Error($"Tool '{toolId}' is not enabled for assistant '{assistantId}'.");
                }
            }
            string handlerId = tool["handlerId"]?.ToString();
            ToolHandler handler = ToolRegistryService.GetHandler(handlerId);
            if (handler is null)
            {
                return Error($"No handler registered for tool: {toolId} (handlerId={handlerId})");
            }
            // Per-handler permission check. Keyed on handlerId so custom tool aliases can't
            // escape the perm gate of the underlying handler.
            if (HandlerPermissions.TryGetValue(handlerId, out PermInfo requiredPerm))
            {
                User user = session?.User;
                if (user is null || !user.HasPermission(requiredPerm))
                {
                    Logs.Warning($"[LLMAssistant] User {(user?.UserID ?? "<none>")} lacks permission '{requiredPerm.ID}' to run handler {handlerId} (via tool {toolId})");
                    return Error($"You do not have permission to use the '{toolId}' tool (handler: {handlerId}). Ask an admin to grant '{requiredPerm.DisplayName}'.");
                }
            }
            // Per-user-per-hour rate limit. Applied AFTER perms so a permission-denied caller
            // doesn't poison their own rate budget. No-op for handlers without a default limit
            // and for users who explicitly disabled the limit via their tool config.
            (bool allowed, int limit, int currentCount, TimeSpan retryAfter) = ToolRateLimitService.TryAcquire(session?.User, handlerId);
            if (!allowed)
            {
                int retryMinutes = (int)Math.Ceiling(retryAfter.TotalMinutes);
                if (retryMinutes < 1) { retryMinutes = 1; }
                Logs.Warning($"[LLMAssistant] Rate limit hit for user {session?.User?.UserID ?? "<none>"} on handler {handlerId} ({currentCount}/{limit}/hr).");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = $"Rate limit exceeded for '{toolId}' ({limit}/hour). Try again in ~{retryMinutes} minute{(retryMinutes == 1 ? "" : "s")}.",
                    ["rateLimited"] = true,
                    ["retryAfterSeconds"] = (int)retryAfter.TotalSeconds,
                };
            }
            // Basic validation: ensure required params are present
            string validationError = ValidateArgs(tool["parameters"] as JObject, args);
            if (validationError is not null)
            {
                return Error(validationError);
            }
            Logs.Debug($"[LLMAssistant] Executing tool {toolId} (handler {handlerId}, assistant {assistantId ?? "<none>"}) with args: {args?.ToString(Newtonsoft.Json.Formatting.None)}");
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultTimeout);
            ToolExecutionContext context = new()
            {
                Args = args ?? new JObject(),
                Session = session,
                AssistantId = assistantId,
                ThreadId = threadId,
                ModelId = modelId,
                Ct = timeoutCts.Token
            };
            JObject result = await handler.Execute(context);
            if (result is null)
            {
                result = Error("Tool returned null result.");
            }
            if (result["success"] is null)
            {
                result["success"] = true;
            }
            // Audit the call after execution so we capture the outcome (success/failure + error).
            // No-op for non-audited handlers and when the audit flag is off.
            AuditLogService.RecordToolCall(handlerId, toolId, session?.User, assistantId, threadId, args, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            JObject cancelled = Error("Tool execution timed out or was cancelled.");
            // Still audit cancellations — long-running shell/file operations that get killed are
            // exactly the events an admin would want to see post-incident.
            AuditLogService.RecordToolCall(GetHandlerIdSafe(toolId, session), toolId, session?.User, assistantId, threadId, args, cancelled);
            return cancelled;
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Tool {toolId} threw: {ex.Message}");
            JObject err = Error(ex.Message);
            AuditLogService.RecordToolCall(GetHandlerIdSafe(toolId, session), toolId, session?.User, assistantId, threadId, args, err);
            return err;
        }
    }

    /// <summary>Best-effort handler-id lookup for the audit path on the exception branches.
    /// We've already left the scope where handlerId was a local, so re-resolve. Returns empty
    /// string on any failure so the audit call is still a safe no-op.</summary>
    private static string GetHandlerIdSafe(string toolId, Session session)
    {
        try { return ToolRegistryService.GetTool(toolId, user: session?.User)?["handlerId"]?.ToString() ?? ""; }
        catch { return ""; }
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
