using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Hooks;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Routes for the hooks screen: list, save, delete, and a dry run that shows what a hook would do without running any tool.</summary>
public static class HookEndpoints
{
    /// <summary>Every hook the caller can see, and what they may do with them. Request: none.</summary>
    public static Task<JObject> LLMAssistantGetHooks(Session session) => Task.FromResult(new JObject
    {
        ["success"] = true,
        ["hooks"] = HookService.GetHookList(session.User),
        ["canWriteShared"] = session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false,
        ["canCommand"] = session.User?.HasPermission(LLMAssistantAPI.PermHookCommand) ?? false,
    });

    /// <summary>Creates or updates a hook. Request: <c>{ hook, scope? }</c>; <c>hook</c> may be an object or a JSON string. A shared hook needs
    /// <c>llm_shared_write</c> and a command hook needs <c>llm_hook_command</c>. Returns <c>{ success, id }</c>.</summary>
    public static Task<JObject> LLMAssistantSaveHook(Session session, JObject rawInput)
    {
        JObject data = ReadHook(rawInput);
        if (data is null)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "No hook data provided." });
        }
        string id = HookService.SaveHook(data, session.User, rawInput["scope"]?.ToString(), out string error);
        return Task.FromResult(id is null
            ? new JObject { ["success"] = false, ["error"] = error ?? "The hook could not be saved." }
            : new JObject { ["success"] = true, ["id"] = id });
    }

    /// <summary>Deletes a hook. Request: <c>{ hookId, scope? }</c>.</summary>
    public static Task<JObject> LLMAssistantDeleteHook(Session session, string hookId, string scope = null)
    {
        bool deleted = HookService.DeleteHook(hookId, session.User, scope);
        return Task.FromResult(deleted
            ? new JObject { ["success"] = true }
            : new JObject { ["success"] = false, ["error"] = "Hook not found, or you may not change that layer." });
    }

    /// <summary>Shows what one hook would do to a call, without running any tool. Request: <c>{ hook, toolId, arguments, result? }</c>, where
    /// <c>hook</c> is the one being edited (saved or not). A pre hook returns <c>{ denied, message, forceAsk, arguments, notes }</c>; a post hook
    /// returns <c>{ notes }</c>. A command hook runs its program, so testing one needs <c>llm_hook_command</c>.</summary>
    public static async Task<JObject> LLMAssistantTestHook(Session session, JObject rawInput)
    {
        JObject data = ReadHook(rawInput);
        if (data is null)
        {
            return new JObject { ["success"] = false, ["error"] = "No hook data provided." };
        }
        if (string.IsNullOrWhiteSpace(data["id"]?.ToString()))
        {
            data["id"] = "test";
        }
        ToolHook hook = ToolHook.FromJson(data, SettingsService.ScopePersonal);
        string problem = hook.Validate();
        if (problem is not null)
        {
            return new JObject { ["success"] = false, ["error"] = problem };
        }
        if (hook.Kind == ToolHook.KindCommand && !(session.User?.HasPermission(LLMAssistantAPI.PermHookCommand) ?? false))
        {
            return new JObject { ["success"] = false, ["error"] = "Testing a command hook runs its program, so it needs the LLM Hook Command permission." };
        }
        string toolId = rawInput["toolId"]?.ToString();
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return new JObject { ["success"] = false, ["error"] = "toolId is required." };
        }
        JObject args = JsonInput.ReadObject(rawInput["arguments"]) ?? [];
        JObject result = JsonInput.ReadObject(rawInput["result"]);
        HookEngine engine = new(new ProcessHookCommandRunner());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(ToolHook.MaxTimeoutSeconds + 5));
        if (hook.Phase == ToolHook.PhasePost)
        {
            List<string> notes = await engine.RunPostAsync([hook], toolId, toolId, args, result ?? new JObject { ["success"] = true }, timeout.Token);
            return new JObject { ["success"] = true, ["phase"] = hook.Phase, ["notes"] = new JArray(notes) };
        }
        PreHookOutcome outcome = await engine.RunPreAsync([hook], toolId, toolId, args, timeout.Token);
        return new JObject
        {
            ["success"] = true,
            ["phase"] = hook.Phase,
            ["denied"] = outcome.Denied,
            ["message"] = outcome.Message,
            ["forceAsk"] = outcome.ForceAsk,
            ["arguments"] = outcome.Arguments,
            ["notes"] = new JArray(outcome.Notes),
        };
    }

    /// <summary>Reads the <c>hook</c> field, which the browser sends as an object or as a JSON string.</summary>
    private static JObject ReadHook(JObject rawInput) => JsonInput.ReadObject(rawInput["hook"]);
}
