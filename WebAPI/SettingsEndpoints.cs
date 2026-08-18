using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Settings CRUD endpoints. Reads return the caller's merged (shared ⊕ personal) view
/// so the UI always sees a single effective settings object. Writes target the personal layer
/// by default; admins with <see cref="LLMAssistantAPI.PermSharedWrite"/> may pass
/// <c>scope: "shared"</c> to update the shared baseline.</summary>
public static class SettingsEndpoints
{
    /// <summary>JSON.NET merge policy used when layering shared settings under personal overrides.</summary>
    private static readonly JsonMergeSettings MergeSettings = new()
    {
        MergeArrayHandling = MergeArrayHandling.Replace,
        MergeNullValueHandling = MergeNullValueHandling.Merge
    };

    public static async Task<JObject> LLMAssistantGetSettings(Session session)
    {
        return new JObject
        {
            ["success"] = true,
            ["settings"] = SettingsService.GetMergedSettings(session.User),
            ["canWriteShared"] = session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false
        };
    }

    public static async Task<JObject> LLMAssistantSaveSettings(Session session, JObject rawInput)
    {
        JObject incoming = rawInput["settings"] as JObject ?? rawInput;
        string scope = rawInput["scope"]?.ToString();
        bool wantsShared = string.Equals(scope, SettingsService.ScopeShared, StringComparison.OrdinalIgnoreCase);
        if (wantsShared && session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) != true)
        {
            return new JObject { ["success"] = false, ["error"] = "Shared writes require llm_shared_write permission." };
        }
        // Strip dict fields (assistants/tools) — those are managed through their own endpoints
        // with explicit scope, so that a SaveSettings call doesn't accidentally wipe one layer's
        // dicts when the UI sends a merged view back. This keeps SaveSettings focused on scalar
        // preferences like preferredModel, parameters, activeAssistantId, instructions, featureMappings.
        JObject filtered = (JObject)incoming.DeepClone();
        filtered.Remove("assistants");
        filtered.Remove("tools");
        JObject saved;
        if (wantsShared)
        {
            JObject sharedCurrent = SettingsService.GetSettings();
            sharedCurrent.Merge(filtered, MergeSettings);
            SettingsService.ReplaceSharedSettings(sharedCurrent);
            saved = sharedCurrent;
            AuditLogService.RecordSharedWrite("update", "settings", session.User,
                new JObject { ["keys"] = string.Join(",", filtered.Properties().Select(p => p.Name)) });
        }
        else
        {
            SettingsService.PatchUserSettings(session.User, filtered);
            saved = SettingsService.GetMergedSettings(session.User);
        }
        return new JObject
        {
            ["success"] = true,
            ["settings"] = saved,
            ["scope"] = wantsShared ? SettingsService.ScopeShared : SettingsService.ScopePersonal
        };
    }

    /// <summary>Resets the caller's settings. By default, clears only the caller's personal
    /// override layer (leaving shared baseline intact). An admin with
    /// <see cref="LLMAssistantAPI.PermSharedWrite"/> may pass <c>scope: "shared"</c> to reset the
    /// shared baseline to factory defaults.</summary>
    public static async Task<JObject> LLMAssistantResetSettings(Session session, JObject rawInput)
    {
        string scope = rawInput?["scope"]?.ToString();
        bool wantsShared = string.Equals(scope, SettingsService.ScopeShared, StringComparison.OrdinalIgnoreCase);
        if (wantsShared)
        {
            if (session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) != true)
            {
                return new JObject { ["success"] = false, ["error"] = "Shared reset requires llm_shared_write permission." };
            }
            JObject defaults = SettingsService.ResetSettings();
            AuditLogService.RecordSharedWrite("reset", "settings", session.User);
            return new JObject { ["success"] = true, ["settings"] = defaults, ["scope"] = SettingsService.ScopeShared };
        }
        SettingsService.ResetUserSettings(session.User);
        return new JObject
        {
            ["success"] = true,
            ["settings"] = SettingsService.GetMergedSettings(session.User),
            ["scope"] = SettingsService.ScopePersonal
        };
    }

    /// <summary>Returns the current audit log state — enabled flag and the most recent N entries.
    /// Admin-only (requires <see cref="LLMAssistantAPI.PermSharedWrite"/>) because the log contains
    /// other users' tool invocations. Default tail size is 200; callable with <c>{ max: N }</c>.</summary>
    public static async Task<JObject> LLMAssistantGetAuditLog(Session session, JObject rawInput)
    {
        if (session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) != true)
        {
            return new JObject { ["success"] = false, ["error"] = "Audit log access requires llm_shared_write permission." };
        }
        int max = rawInput?["max"]?.Value<int>() ?? 200;
        if (max < 1) { max = 1; }
        if (max > 5000) { max = 5000; }
        return new JObject
        {
            ["success"] = true,
            ["enabled"] = AuditLogService.IsEnabled(),
            ["entries"] = AuditLogService.ReadRecent(max)
        };
    }

    /// <summary>Toggles the audit log on/off. Admin-only. Persists across restarts.</summary>
    public static async Task<JObject> LLMAssistantSetAuditLogEnabled(Session session, JObject rawInput)
    {
        if (session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) != true)
        {
            return new JObject { ["success"] = false, ["error"] = "Audit log toggle requires llm_shared_write permission." };
        }
        bool enabled = rawInput?["enabled"]?.Value<bool>() ?? false;
        AuditLogService.SetEnabled(enabled);
        // Self-audit the toggle so we can see who turned auditing off (only logged if it was already on).
        AuditLogService.RecordSharedWrite("audit_" + (enabled ? "enabled" : "disabled"), "audit_log", session.User);
        return new JObject { ["success"] = true, ["enabled"] = enabled };
    }
}
