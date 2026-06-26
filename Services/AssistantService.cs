using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Extensions.LLMAssistant.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Centralized service for assistant CRUD, resolution, and parameter merging.
///
/// <para>Multi-user model: reads always go through <see cref="SettingsService.GetMergedSettings"/>
/// so a user sees shared (admin-managed) assistants + their own personal assistants. Writes go
/// to the layer identified by the <c>scope</c> parameter (<see cref="SettingsService.ScopePersonal"/>
/// by default, or <see cref="SettingsService.ScopeShared"/> when the caller has
/// <see cref="LLMAssistantAPI.PermSharedWrite"/>).</para>
/// </summary>
public static class AssistantService
{
    private static readonly JsonMergeSettings MergeSettings = new()
    {
        MergeArrayHandling = MergeArrayHandling.Replace,
        MergeNullValueHandling = MergeNullValueHandling.Merge
    };

    /// <summary>Gets the active assistant ID from the user's merged view. Personal overrides win.</summary>
    public static string GetActiveAssistantId(JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        return settings["activeAssistantId"]?.ToString() ?? AssistantConstants.DefaultId;
    }

    /// <summary>Gets an assistant by ID from the user's merged view. Falls back to default if not found.</summary>
    public static JObject GetAssistant(string assistantId, JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject assistants = settings["assistants"] as JObject;
        if (assistants?[assistantId] is JObject assistant)
        {
            return assistant;
        }
        if (assistantId != AssistantConstants.DefaultId && assistants?[AssistantConstants.DefaultId] is JObject defaultAssistant)
        {
            return defaultAssistant;
        }
        return SettingsService.BuildDefaultAssistant();
    }

    /// <summary>Gets the active assistant object for a user.</summary>
    public static JObject GetActiveAssistant(JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        return GetAssistant(GetActiveAssistantId(settings, user), settings, user);
    }

    /// <summary>Resolves instruction text for a feature from the given assistant. Routes through
    /// <see cref="AssistantResolver"/> so inheritance (<c>extends</c>) and per-model variants are
    /// applied. Pass <paramref name="modelInfo"/> when known so model-specific variants can win.
    /// Falls back to <see cref="DefaultInstructions.Prompt"/> if no candidate matches.</summary>
    public static string ResolveInstruction(string featureInstructionId, string assistantId = null, JObject settings = null, User user = null, LLMModelInfo modelInfo = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        assistantId ??= GetActiveAssistantId(settings, user);
        ResolvedAssistant resolved = AssistantResolver.Resolve(assistantId, user, settings);
        string text = resolved.ResolveInstruction(featureInstructionId, modelInfo);
        return string.IsNullOrEmpty(text) ? DefaultInstructions.Prompt : text;
    }

    /// <summary>Resolves parameters by merging global defaults with the resolved assistant
    /// (which already has inheritance applied).</summary>
    public static JObject ResolveParameters(string assistantId = null, JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        assistantId ??= GetActiveAssistantId(settings, user);
        JObject globalParams = settings["parameters"] as JObject ?? new JObject();
        ResolvedAssistant resolved = AssistantResolver.Resolve(assistantId, user, settings);
        JObject result = (JObject)globalParams.DeepClone();
        if (resolved.Parameters.Count > 0)
        {
            result.Merge(resolved.Parameters, MergeSettings);
        }
        return result;
    }

    /// <summary>Gets all assistants as a JArray for the UI. Includes the <c>_scope</c> marker on
    /// each entry so the UI can render a "shared" / "personal" badge.</summary>
    public static JArray GetAssistantList(JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject assistants = settings["assistants"] as JObject;
        JArray result = [];
        if (assistants is null)
        {
            return result;
        }
        foreach (KeyValuePair<string, JToken> kvp in assistants)
        {
            if (kvp.Value is JObject obj)
            {
                result.Add(obj.DeepClone());
            }
        }
        return result;
    }

    /// <summary>Saves an assistant (create or update) into the layer identified by <paramref name="scope"/>.
    /// <para>Scope rules:</para>
    /// <list type="bullet">
    /// <item><c>personal</c> (default) — writes to the current user's override layer. Any user with
    /// <see cref="LLMAssistantAPI.PermSettings"/> can do this.</item>
    /// <item><c>shared</c> — writes to the shared/admin baseline. Requires <see cref="LLMAssistantAPI.PermSharedWrite"/>.</item>
    /// </list>
    /// <para>Returns the new/updated assistant ID, or <c>null</c> if the caller is not allowed to
    /// write to the requested scope.</para>
    /// </summary>
    public static string SaveAssistant(JObject assistantData, User user, string scope = null)
    {
        if (assistantData is null)
        {
            return null;
        }
        scope = NormalizeScope(scope);
        if (scope == SettingsService.ScopeShared && !CanWriteShared(user))
        {
            return null;
        }
        string id = assistantData["id"]?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            id = $"assistant-{Guid.NewGuid():N}";
            assistantData["id"] = id;
        }
        assistantData["updated"] = DateTime.UtcNow.ToString("o");
        if (assistantData["created"] is null)
        {
            assistantData["created"] = DateTime.UtcNow.ToString("o");
        }
        JObject stripped = SettingsService.StripScope(assistantData);
        if (scope == SettingsService.ScopeShared)
        {
            JObject shared = SettingsService.GetSettings();
            JObject assistants = shared["assistants"] as JObject ?? [];
            bool isUpdate = assistants.ContainsKey(id);
            assistants[id] = stripped;
            shared["assistants"] = assistants;
            SettingsService.ReplaceSharedSettings(shared);
            // Shared changes affect every user.
            AssistantResolver.InvalidateAll();
            AuditLogService.RecordSharedWrite(isUpdate ? "update" : "create", $"assistant:{id}", user,
                new JObject { ["name"] = stripped["name"]?.ToString() });
            // If the shared version previously existed as a personal override and the user is just
            // pushing it "up", we don't auto-delete the personal copy — that's an explicit user action.
        }
        else
        {
            JObject personal = SettingsService.GetUserSettings(user);
            JObject assistants = personal["assistants"] as JObject ?? [];
            assistants[id] = stripped;
            personal["assistants"] = assistants;
            SettingsService.ReplaceUserSettings(user, personal);
            AssistantResolver.Invalidate(user);
        }
        return id;
    }

    /// <summary>Deletes an assistant. If <paramref name="scope"/> is null, auto-detects which
    /// layer owns the ID (preferring personal). Returns false if:
    /// <list type="bullet">
    /// <item>the ID is the default assistant (cannot be deleted),</item>
    /// <item>the ID is not found in either layer,</item>
    /// <item>the caller tried to delete a shared entry without <see cref="LLMAssistantAPI.PermSharedWrite"/>.</item>
    /// </list>
    /// </summary>
    public static bool DeleteAssistant(string assistantId, User user, string scope = null)
    {
        if (assistantId == AssistantConstants.DefaultId)
        {
            return false;
        }
        scope = NormalizeScope(scope, allowAuto: true);
        // Personal deletion
        JObject personal = SettingsService.GetUserSettings(user);
        JObject personalAssistants = personal["assistants"] as JObject;
        bool inPersonal = personalAssistants is not null && personalAssistants.ContainsKey(assistantId);
        // Shared deletion
        JObject shared = SettingsService.GetSettings();
        JObject sharedAssistants = shared["assistants"] as JObject;
        bool inShared = sharedAssistants is not null && sharedAssistants.ContainsKey(assistantId);
        if (scope is null)
        {
            // Auto: prefer personal if present
            scope = inPersonal ? SettingsService.ScopePersonal : (inShared ? SettingsService.ScopeShared : null);
        }
        if (scope is null)
        {
            return false;
        }
        if (scope == SettingsService.ScopeShared)
        {
            if (!inShared || !CanWriteShared(user))
            {
                return false;
            }
            sharedAssistants.Remove(assistantId);
            if (shared["activeAssistantId"]?.ToString() == assistantId)
            {
                shared["activeAssistantId"] = AssistantConstants.DefaultId;
            }
            SettingsService.ReplaceSharedSettings(shared);
            AssistantResolver.InvalidateAll();
            AuditLogService.RecordSharedWrite("delete", $"assistant:{assistantId}", user);
            return true;
        }
        else
        {
            if (!inPersonal)
            {
                return false;
            }
            personalAssistants.Remove(assistantId);
            if (personal["activeAssistantId"]?.ToString() == assistantId)
            {
                personal.Remove("activeAssistantId");
            }
            SettingsService.ReplaceUserSettings(user, personal);
            AssistantResolver.Invalidate(user);
            return true;
        }
    }

    /// <summary>Sets the active assistant. This is always a personal preference — it goes into
    /// the user's override layer, never the shared baseline.</summary>
    public static void SetActiveAssistant(string assistantId, User user)
    {
        JObject personal = SettingsService.GetUserSettings(user);
        personal["activeAssistantId"] = assistantId;
        SettingsService.ReplaceUserSettings(user, personal);
    }

    /// <summary>Normalizes a scope string. Null → <c>"personal"</c> unless <paramref name="allowAuto"/>.</summary>
    private static string NormalizeScope(string scope, bool allowAuto = false)
    {
        if (string.IsNullOrEmpty(scope))
        {
            return allowAuto ? null : SettingsService.ScopePersonal;
        }
        if (string.Equals(scope, SettingsService.ScopeShared, StringComparison.OrdinalIgnoreCase))
        {
            return SettingsService.ScopeShared;
        }
        return SettingsService.ScopePersonal;
    }

    /// <summary>True if the user holds <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
    private static bool CanWriteShared(User user)
    {
        return user is not null && user.HasPermission(LLMAssistantAPI.PermSharedWrite);
    }
}
