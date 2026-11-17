using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.Hooks;

/// <summary>Stores and reads hooks in the same two layers as assistants and tools: the shared layer an admin manages (it applies to every user)
/// and each user's personal layer (it applies to that user). Both layers apply to a call, shared first.</summary>
public static class HookService
{
    /// <summary>The settings key a layer keeps its hooks under, as an object of hook id to hook.</summary>
    private const string SettingsKey = "hooks";

    /// <summary>The hooks that apply to a user, shared layer first then personal, each in order. A personal command hook is left out when its
    /// owner no longer holds <c>llm_hook_command</c>, so revoking the permission also stops what was written under it.</summary>
    public static List<ToolHook> GetHooks(User user)
    {
        List<ToolHook> hooks = [];
        hooks.AddRange(ReadLayer(SettingsService.GetSettings(), SettingsService.ScopeShared));
        bool mayRunCommands = user is not null && user.HasPermission(LLMAssistantAPI.PermHookCommand);
        hooks.AddRange(ReadLayer(SettingsService.GetUserSettings(user), SettingsService.ScopePersonal).Where(h => h.Kind != ToolHook.KindCommand || mayRunCommands));
        return hooks;
    }

    /// <summary>Every hook the user can see, for the settings screen, as JSON tagged with its layer.</summary>
    public static JArray GetHookList(User user)
    {
        JArray list = [];
        foreach (ToolHook hook in ReadLayer(SettingsService.GetSettings(), SettingsService.ScopeShared).Concat(ReadLayer(SettingsService.GetUserSettings(user), SettingsService.ScopePersonal)))
        {
            JObject json = hook.ToJson();
            json["_scope"] = hook.Scope;
            list.Add(json);
        }
        return list;
    }

    /// <summary>Creates or updates a hook in a layer. Returns the id, or null with the reason in <paramref name="error"/>.</summary>
    public static string SaveHook(JObject data, User user, string scope, out string error)
    {
        error = null;
        scope = scope == SettingsService.ScopeShared ? SettingsService.ScopeShared : SettingsService.ScopePersonal;
        if (data is null)
        {
            error = "No hook data provided.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(data["id"]?.ToString()))
        {
            data["id"] = $"hook-{Guid.NewGuid():N}"[..21];
        }
        ToolHook hook = ToolHook.FromJson(data, scope);
        error = hook.Validate();
        if (error is not null)
        {
            return null;
        }
        if (scope == SettingsService.ScopeShared && (user is null || !user.HasPermission(LLMAssistantAPI.PermSharedWrite)))
        {
            error = "Saving a shared hook needs the LLM Shared Write permission.";
            return null;
        }
        if (hook.Kind == ToolHook.KindCommand && (user is null || !user.HasPermission(LLMAssistantAPI.PermHookCommand)))
        {
            error = "A command hook runs a program on the server, so it needs the LLM Hook Command permission.";
            return null;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject layer = scope == SettingsService.ScopeShared ? SettingsService.GetSettings() : SettingsService.GetUserSettings(user);
            JObject hooks = layer[SettingsKey] as JObject ?? [];
            bool isUpdate = hooks.ContainsKey(hook.Id);
            if (!isUpdate && hooks.Count >= ToolHook.MaxPerLayer)
            {
                error = $"A layer can hold up to {ToolHook.MaxPerLayer} hooks.";
                return null;
            }
            JObject stored = hook.ToJson();
            stored["created"] = hooks[hook.Id]?["created"] ?? DateTime.UtcNow.ToString("o");
            stored["updated"] = DateTime.UtcNow.ToString("o");
            hooks[hook.Id] = stored;
            layer[SettingsKey] = hooks;
            if (scope == SettingsService.ScopeShared)
            {
                SettingsService.ReplaceSharedSettings(layer);
                AuditLogService.RecordSharedWrite(isUpdate ? "update" : "create", $"hook:{hook.Id}", user, new JObject { ["name"] = hook.Name, ["kind"] = hook.Kind });
            }
            else
            {
                SettingsService.ReplaceUserSettings(user, layer);
            }
        }
        return hook.Id;
    }

    /// <summary>Deletes a hook from a layer. False when there is no such hook there or the user may not change that layer.</summary>
    public static bool DeleteHook(string id, User user, string scope)
    {
        scope = scope == SettingsService.ScopeShared ? SettingsService.ScopeShared : SettingsService.ScopePersonal;
        if (scope == SettingsService.ScopeShared && (user is null || !user.HasPermission(LLMAssistantAPI.PermSharedWrite)))
        {
            return false;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject layer = scope == SettingsService.ScopeShared ? SettingsService.GetSettings() : SettingsService.GetUserSettings(user);
            if (layer[SettingsKey] is not JObject hooks || !hooks.Remove(id))
            {
                return false;
            }
            layer[SettingsKey] = hooks;
            if (scope == SettingsService.ScopeShared)
            {
                SettingsService.ReplaceSharedSettings(layer);
                AuditLogService.RecordSharedWrite("delete", $"hook:{id}", user);
            }
            else
            {
                SettingsService.ReplaceUserSettings(user, layer);
            }
        }
        return true;
    }

    /// <summary>The valid hooks in one layer's settings, in run order. A stored hook that no longer validates is skipped rather than run.</summary>
    private static List<ToolHook> ReadLayer(JObject layer, string scope)
    {
        if (layer?[SettingsKey] is not JObject hooks)
        {
            return [];
        }
        List<ToolHook> result = [];
        foreach (JProperty property in hooks.Properties())
        {
            if (property.Value is not JObject json)
            {
                continue;
            }
            json["id"] ??= property.Name;
            ToolHook hook = ToolHook.FromJson(json, scope);
            if (hook.Validate() is null)
            {
                result.Add(hook);
            }
        }
        return [.. result.OrderBy(h => h.Order).ThenBy(h => h.Id, StringComparer.Ordinal)];
    }
}
