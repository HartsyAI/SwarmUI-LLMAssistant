using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Instruction CRUD endpoints. User-scoped with optional <c>scope</c> field.</summary>
public static class InstructionEndpoints
{
    /// <summary>Returns the caller's merged instruction list.</summary>
    public static async Task<JObject> LLMAssistantGetInstructions(Session session)
    {
        JArray instructions = InstructionService.GetInstructionList(user: session.User);
        return new JObject
        {
            ["success"] = true,
            ["instructions"] = instructions,
            ["canWriteShared"] = session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false
        };
    }

    /// <summary>Creates or updates an instruction. Defaults to personal layer; scope="shared"
    /// requires <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
    public static async Task<JObject> LLMAssistantSaveInstruction(Session session, JObject rawInput)
    {
        string id = rawInput["id"]?.ToString();
        string title = rawInput["title"]?.ToString();
        string content = rawInput["content"]?.ToString();
        bool isBuiltIn = rawInput["isBuiltIn"]?.Value<bool>() ?? false;
        string scope = rawInput["scope"]?.ToString();
        if (string.IsNullOrEmpty(content))
        {
            return new JObject { ["success"] = false, ["error"] = "Instruction content is required." };
        }
        bool wantsShared = string.Equals(scope, SettingsService.ScopeShared, StringComparison.OrdinalIgnoreCase);
        if (wantsShared && session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) != true)
        {
            return new JObject { ["success"] = false, ["error"] = "Shared writes require llm_shared_write permission." };
        }
        JObject targetLayer = wantsShared
            ? SettingsService.GetSettings()
            : SettingsService.GetUserSettings(session.User);
        JObject instructions = targetLayer["instructions"] as JObject;
        if (instructions is null)
        {
            instructions = [];
            targetLayer["instructions"] = instructions;
        }
        if (isBuiltIn && !string.IsNullOrEmpty(id) && instructions.ContainsKey(id))
        {
            // Update built-in instruction content
            instructions[id] = content;
        }
        else
        {
            // Custom instruction
            if (string.IsNullOrEmpty(id))
            {
                id = $"custom-{Guid.NewGuid():N}";
            }
            JObject custom = instructions["custom"] as JObject ?? [];
            JObject existing = custom[id] as JObject;
            custom[id] = new JObject
            {
                ["id"] = id,
                ["title"] = title ?? id,
                ["content"] = content,
                ["tooltip"] = rawInput["tooltip"]?.ToString() ?? "",
                ["categories"] = rawInput["categories"] ?? new JArray(),
                ["created"] = existing?["created"]?.ToString() ?? DateTime.UtcNow.ToString("o"),
                ["updated"] = DateTime.UtcNow.ToString("o")
            };
            instructions["custom"] = custom;
        }
        if (wantsShared)
        {
            SettingsService.ReplaceSharedSettings(targetLayer);
        }
        else
        {
            SettingsService.ReplaceUserSettings(session.User, targetLayer);
        }
        return new JObject
        {
            ["success"] = true,
            ["id"] = id,
            ["scope"] = wantsShared ? SettingsService.ScopeShared : SettingsService.ScopePersonal
        };
    }

    /// <summary>Deletes a custom instruction. Auto-detects layer if scope not provided.</summary>
    public static async Task<JObject> LLMAssistantDeleteInstruction(Session session, string id, string scope = null)
    {
        if (string.IsNullOrEmpty(id))
        {
            return new JObject { ["success"] = false, ["error"] = "Instruction ID is required." };
        }
        JObject personal = SettingsService.GetUserSettings(session.User);
        JObject personalCustom = (personal["instructions"] as JObject)?["custom"] as JObject;
        bool inPersonal = personalCustom is not null && personalCustom.ContainsKey(id);
        JObject shared = SettingsService.GetSettings();
        JObject sharedCustom = (shared["instructions"] as JObject)?["custom"] as JObject;
        bool inShared = sharedCustom is not null && sharedCustom.ContainsKey(id);
        string resolvedScope = scope;
        if (string.IsNullOrEmpty(resolvedScope))
        {
            resolvedScope = inPersonal ? SettingsService.ScopePersonal : (inShared ? SettingsService.ScopeShared : null);
        }
        if (resolvedScope is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Custom instruction '{id}' not found." };
        }
        if (string.Equals(resolvedScope, SettingsService.ScopeShared, StringComparison.OrdinalIgnoreCase))
        {
            if (session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) != true)
            {
                return new JObject { ["success"] = false, ["error"] = "Shared deletes require llm_shared_write permission." };
            }
            if (!inShared)
            {
                return new JObject { ["success"] = false, ["error"] = $"Custom instruction '{id}' not found in shared layer." };
            }
            sharedCustom.Remove(id);
            SettingsService.ReplaceSharedSettings(shared);
        }
        else
        {
            if (!inPersonal)
            {
                return new JObject { ["success"] = false, ["error"] = $"Custom instruction '{id}' not found in personal layer." };
            }
            personalCustom.Remove(id);
            SettingsService.ReplaceUserSettings(session.User, personal);
        }
        return new JObject { ["success"] = true };
    }
}
