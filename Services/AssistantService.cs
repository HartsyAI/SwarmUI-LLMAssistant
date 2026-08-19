using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Hartsy.Extensions.LLMAssistant.LLMs;

namespace Hartsy.Extensions.LLMAssistant.Services;

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
            AnnotateEffective(assistant, assistantId, settings, user, SettingsService.GetSettings()["assistants"] as JObject);
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
        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }
        // The resolver no longer force-inherits the default assistant, so an assistant with no text for
        // this mode falls back here: the settings-level instruction for the id (which the merged view
        // always seeds with the correct per-mode default for the 7 built-in ids), then that service's
        // own DefaultInstructions terminal fallback.
        return InstructionService.ResolveInstruction(featureInstructionId, settings, user);
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
    /// each entry so the UI can render a "shared" / "personal" badge, plus the resolver-derived
    /// effective view (see <see cref="AnnotateEffective"/>).</summary>
    public static JArray GetAssistantList(JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject assistants = settings["assistants"] as JObject;
        JArray result = [];
        if (assistants is null)
        {
            return result;
        }
        JObject sharedAssistants = SettingsService.GetSettings()["assistants"] as JObject;
        foreach (KeyValuePair<string, JToken> kvp in assistants)
        {
            if (kvp.Value is JObject obj)
            {
                JObject clone = (JObject)obj.DeepClone();
                AnnotateEffective(clone, kvp.Key, settings, user, sharedAssistants);
                result.Add(clone);
            }
        }
        return result;
    }

    /// <summary>Annotates an assistant entry with its inheritance-resolved effective view, so the
    /// frontend has one source of truth instead of re-deriving (and disagreeing about) resolution:
    /// <c>_effectiveToolIds</c> (the tool set the model will actually be offered),
    /// <c>_effectiveToolsEnabled</c> (the resolved master switch), and <c>_hasSharedCounterpart</c>
    /// (this personal entry shadows a shared one — deleting it reverts rather than removes).</summary>
    private static void AnnotateEffective(JObject entry, string id, JObject settings, User user, JObject sharedAssistants)
    {
        ResolvedAssistant resolved = AssistantResolver.Resolve(id, user, settings);
        entry["_effectiveToolIds"] = new JArray(resolved.EnabledToolIds.OrderBy(t => t).Cast<object>().ToArray());
        entry["_effectiveToolsEnabled"] = resolved.ToolsEnabled;
        if (entry["_scope"]?.ToString() == SettingsService.ScopePersonal && sharedAssistants?.ContainsKey(id) == true)
        {
            entry["_hasSharedCounterpart"] = true;
        }
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
    /// <summary>Fields the assistant editor owns outright — authoritative from a save payload.
    /// Anything NOT in this list (and not <c>instructions</c>, handled per-mode below) is preserved
    /// from the stored record, so a save can never destroy data the editor doesn't know about.</summary>
    private static readonly string[] EditorOwnedFields =
        ["name", "description", "icon", "color", "extends", "parameters", "toolsEnabled", "enabledToolIds", "toolConfig", "avatar"];

    /// <summary>Allowed shape for assistant ids — used directly as settings dictionary keys.</summary>
    private static readonly System.Text.RegularExpressions.Regex IdPattern = new("^[A-Za-z0-9_-]{1,64}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Validates a save payload. Returns null when acceptable, else a user-facing message.</summary>
    private static string ValidateAssistant(JObject data, string id, JObject mergedSettings)
    {
        if (!IdPattern.IsMatch(id))
        {
            return "Assistant id must be 1-64 characters of letters, digits, '-' or '_'.";
        }
        string name = data["name"]?.ToString() ?? "";
        if (name.Length is < 1 or > 100)
        {
            return "Assistant name must be 1-100 characters.";
        }
        if ((data["description"]?.ToString() ?? "").Length > 500)
        {
            return "Assistant description must be at most 500 characters.";
        }
        if (data["instructions"] is JObject instr)
        {
            foreach (KeyValuePair<string, JToken> kv in instr)
            {
                string text = kv.Value is JObject node ? node["default"]?.ToString() : kv.Value?.ToString();
                if ((text ?? "").Length > 20000)
                {
                    return $"Instruction '{kv.Key}' is too long (max 20,000 characters).";
                }
                if (kv.Value is JObject vnode && vnode["variants"] is JArray variants && variants.Count > 50)
                {
                    return $"Instruction '{kv.Key}' has too many variants (max 50).";
                }
            }
        }
        string extendsId = data["extends"]?.ToString();
        if (!string.IsNullOrEmpty(extendsId))
        {
            JObject assistants = mergedSettings["assistants"] as JObject ?? [];
            if (extendsId == id)
            {
                return "An assistant cannot extend itself.";
            }
            if (assistants[extendsId] is not JObject)
            {
                return $"Parent assistant '{extendsId}' does not exist.";
            }
            // Save-time cycle walk (the resolver is also cycle-safe at read time, but rejecting here
            // gives the user an actionable error instead of a silently-truncated chain).
            HashSet<string> visited = [id];
            string current = extendsId;
            for (int depth = 0; depth < AssistantResolver.MaxDepth && !string.IsNullOrEmpty(current); depth++)
            {
                if (!visited.Add(current))
                {
                    return $"Extending '{extendsId}' would create an inheritance cycle.";
                }
                current = (assistants[current] as JObject)?["extends"]?.ToString();
            }
        }
        return null;
    }

    /// <summary>Builds the record to store: the existing stored record (if any) with the editor-owned
    /// fields replaced from the payload. Instructions merge per-mode: the built-in mode ids are
    /// authoritative from the payload (absent = cleared), any other instruction id on the stored
    /// record is preserved. <c>created</c> is preserved; <c>isBuiltIn</c> is forced from stored state
    /// (never trusted from the client).</summary>
    private static JObject MergeForSave(JObject payload, JObject existing, bool storedIsBuiltIn)
    {
        JObject result = existing is null ? [] : (JObject)existing.DeepClone();
        result["id"] = payload["id"];
        foreach (string field in EditorOwnedFields)
        {
            if (payload.ContainsKey(field))
            {
                result[field] = payload[field]?.DeepClone();
            }
            else if (field is "extends" or "avatar")
            {
                // Absent nullable identity fields mean "cleared" (the editor omits them when unset);
                // the others absent mean "unchanged".
                result.Remove(field);
            }
        }
        JObject mergedInstructions = existing?["instructions"] as JObject ?? [];
        mergedInstructions = (JObject)mergedInstructions.DeepClone();
        JObject payloadInstructions = payload["instructions"] as JObject ?? [];
        foreach (string mode in InstructionIds.All)
        {
            if (payloadInstructions.ContainsKey(mode))
            {
                mergedInstructions[mode] = payloadInstructions[mode]?.DeepClone();
            }
            else
            {
                mergedInstructions.Remove(mode);
            }
        }
        // Non-built-in instruction ids from the payload are accepted too (API callers may set them).
        foreach (KeyValuePair<string, JToken> kv in payloadInstructions)
        {
            if (!InstructionIds.All.Contains(kv.Key))
            {
                mergedInstructions[kv.Key] = kv.Value?.DeepClone();
            }
        }
        result["instructions"] = mergedInstructions;
        if (existing?["created"] is JToken created && created.Type != JTokenType.Null)
        {
            result["created"] = created.DeepClone();
        }
        else if (result["created"] is null)
        {
            result["created"] = DateTime.UtcNow.ToString("o");
        }
        result["updated"] = DateTime.UtcNow.ToString("o");
        if (storedIsBuiltIn)
        {
            result["isBuiltIn"] = true;
        }
        else
        {
            result.Remove("isBuiltIn");
        }
        return SettingsService.StripScope(result);
    }

    public static (string Id, string Error) SaveAssistant(JObject assistantData, User user, string scope = null)
    {
        if (assistantData is null)
        {
            return (null, "No assistant data provided.");
        }
        scope = NormalizeScope(scope);
        if (scope == SettingsService.ScopeShared && !CanWriteShared(user))
        {
            return (null, "Shared writes require the llm_shared_write permission.");
        }
        string id = assistantData["id"]?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            id = $"assistant-{Guid.NewGuid():N}";
            assistantData["id"] = id;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject merged = SettingsService.GetMergedSettings(user);
            string validationError = ValidateAssistant(assistantData, id, merged);
            if (validationError is not null)
            {
                return (null, validationError);
            }
            // isBuiltIn comes from stored state only: the target layer's record if present, else the
            // shared counterpart (so a personal overlay of Swarmie keeps the flag — and a client can't
            // spoof it onto a new assistant).
            JObject sharedLayer = SettingsService.GetSettings();
            JObject sharedAssistants = sharedLayer["assistants"] as JObject ?? [];
            if (scope == SettingsService.ScopeShared)
            {
                JObject existing = sharedAssistants[id] as JObject;
                bool storedIsBuiltIn = existing?["isBuiltIn"]?.Value<bool>() == true;
                bool isUpdate = existing is not null;
                sharedAssistants[id] = MergeForSave(assistantData, existing, storedIsBuiltIn);
                sharedLayer["assistants"] = sharedAssistants;
                SettingsService.ReplaceSharedSettings(sharedLayer);
                // Shared changes affect every user.
                AssistantResolver.InvalidateAll();
                AuditLogService.RecordSharedWrite(isUpdate ? "update" : "create", $"assistant:{id}", user,
                    new JObject { ["name"] = assistantData["name"]?.ToString() });
            }
            else
            {
                JObject personal = SettingsService.GetUserSettings(user);
                JObject assistants = personal["assistants"] as JObject ?? [];
                JObject existing = assistants[id] as JObject;
                bool storedIsBuiltIn = (existing?["isBuiltIn"]?.Value<bool>() == true)
                    || (sharedAssistants[id] as JObject)?["isBuiltIn"]?.Value<bool>() == true;
                assistants[id] = MergeForSave(assistantData, existing, storedIsBuiltIn);
                personal["assistants"] = assistants;
                SettingsService.ReplaceUserSettings(user, personal);
                AssistantResolver.Invalidate(user);
            }
        }
        return (id, null);
    }

    /// <summary>Deletes an assistant. If <paramref name="scope"/> is null, auto-detects which
    /// layer owns the ID (preferring personal). Personal deletes are always allowed — removing a
    /// personal overlay of a shared id is the "revert to default" path. Shared deletes require
    /// <see cref="LLMAssistantAPI.PermSharedWrite"/> and are refused for built-in records (the
    /// shared baseline must always carry Swarmie).</summary>
    /// <summary>Outcome of a <see cref="DeleteAssistant"/> call, so the endpoint/UI can phrase the
    /// result correctly ("deleted" vs "reverted to the shared version").</summary>
    public enum DeleteResult
    {
        /// <summary>Nothing removed — not found, or not permitted.</summary>
        Failed,
        /// <summary>The record was removed and no other layer supplies this id.</summary>
        Deleted,
        /// <summary>A personal overlay was removed and the shared version now shows again.</summary>
        Reverted,
    }

    public static DeleteResult DeleteAssistant(string assistantId, User user, string scope = null)
    {
        scope = NormalizeScope(scope, allowAuto: true);
        lock (SettingsService.SettingsLock)
        {
            // Personal layer
            JObject personal = SettingsService.GetUserSettings(user);
            JObject personalAssistants = personal["assistants"] as JObject;
            bool inPersonal = personalAssistants is not null && personalAssistants.ContainsKey(assistantId);
            // Shared layer
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
                return DeleteResult.Failed;
            }
            if (scope == SettingsService.ScopeShared)
            {
                if (!inShared || !CanWriteShared(user))
                {
                    return DeleteResult.Failed;
                }
                // Shared built-ins (Swarmie) are the permanent baseline — they can never be removed
                // from the shared layer. Personal overlays of them ARE removable (that's the revert
                // path below); shared non-built-ins remain admin-deletable.
                if ((sharedAssistants[assistantId] as JObject)?["isBuiltIn"]?.Value<bool>() == true)
                {
                    return DeleteResult.Failed;
                }
                sharedAssistants.Remove(assistantId);
                if (shared["activeAssistantId"]?.ToString() == assistantId)
                {
                    shared["activeAssistantId"] = AssistantConstants.DefaultId;
                }
                SettingsService.ReplaceSharedSettings(shared);
                AssistantResolver.InvalidateAll();
                AuditLogService.RecordSharedWrite("delete", $"assistant:{assistantId}", user);
                return DeleteResult.Deleted;
            }
            else
            {
                if (!inPersonal)
                {
                    return DeleteResult.Failed;
                }
                personalAssistants.Remove(assistantId);
                if (personal["activeAssistantId"]?.ToString() == assistantId)
                {
                    personal.Remove("activeAssistantId");
                }
                SettingsService.ReplaceUserSettings(user, personal);
                AssistantResolver.Invalidate(user);
                // Removing a personal overlay of a shared id is a revert — the shared version
                // (including built-in Swarmie) is what the merged view shows from now on.
                return inShared ? DeleteResult.Reverted : DeleteResult.Deleted;
            }
        }
    }

    /// <summary>Sets the active assistant. This is always a personal preference — it goes into
    /// the user's override layer, never the shared baseline.</summary>
    public static void SetActiveAssistant(string assistantId, User user)
    {
        lock (SettingsService.SettingsLock)
        {
            JObject personal = SettingsService.GetUserSettings(user);
            personal["activeAssistantId"] = assistantId;
            SettingsService.ReplaceUserSettings(user, personal);
        }
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
