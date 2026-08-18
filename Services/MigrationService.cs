using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>One-time migrations for the instructions-to-assistants model and built-in tool seeding.</summary>
public static class MigrationService
{
    private const string MigratedAssistantsKey = "migrated_assistants";
    private const string MigratedToolsKey = "migrated_tools";

    /// <summary>Runs all pending migrations.</summary>
    public static void RunIfNeeded()
    {
        MigrateToAssistants();
        MigrateTools();
        BackfillBuiltInTools();
        BackfillCompanionDefaults();
    }

    /// <summary>Idempotent top-up for the floating Companion overlay. Adds the default
    /// <c>companion</c> instruction text to any existing built-in assistant that doesn't
    /// have one yet, and seeds the shared <c>companion</c> settings block. Does NOT touch
    /// custom user assistants — those keep whatever the user authored, and the resolver
    /// already falls back to the global default instruction if they leave it blank.</summary>
    private static void BackfillCompanionDefaults()
    {
        try
        {
            JObject settings = SettingsService.GetSettings();
            bool changed = false;
            // Seed the global instructions[companion] entry (used as the resolver fallback)
            if (settings["instructions"] is JObject instr && instr[InstructionIds.Companion] is null)
            {
                instr[InstructionIds.Companion] = DefaultInstructions.Companion;
                changed = true;
            }
            // Top up the default (built-in) assistant. Custom assistants keep whatever the user has.
            if (settings["assistants"] is JObject assistants
                && assistants[AssistantConstants.DefaultId] is JObject defaultAssistant
                && defaultAssistant["instructions"] is JObject defaultInstr
                && defaultInstr[InstructionIds.Companion] is null)
            {
                defaultInstr[InstructionIds.Companion] = DefaultInstructions.Companion;
                changed = true;
            }
            // Seed the global companion settings block (only used as the baseline; per-user
            // overrides live in the user's personal layer).
            if (settings["companion"] is null)
            {
                settings["companion"] = SettingsService.BuildDefaultCompanionSettings();
                changed = true;
            }
            // Wire the feature mapping so InstructionService can resolve `companion-mode`.
            if (settings["featureMappings"] is JObject mappings && mappings[FeatureKeys.CompanionMode] is null)
            {
                mappings[FeatureKeys.CompanionMode] = InstructionIds.Companion;
                changed = true;
            }
            if (changed)
            {
                SettingsService.SaveSettings(settings);
                Logs.Info("[LLMAssistant] Backfilled companion instruction + settings defaults.");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Companion backfill failed: {ex.Message}");
        }
    }

    /// <summary>Seeds built-in tools into settings and enables them on the default assistant for existing installs.</summary>
    private static void MigrateTools()
    {
        try
        {
            string migrated = Program.Sessions.GenericSharedUser.GetGenericData(SettingsService.DataName, MigratedToolsKey);
            if (!string.IsNullOrEmpty(migrated))
            {
                return;
            }
            JObject settings = SettingsService.GetSettings();
            JObject tools = settings["tools"] as JObject ?? new JObject();
            JObject defaultTools = ToolRegistryService.BuildDefaultTools();
            foreach (KeyValuePair<string, JToken> kvp in defaultTools)
            {
                if (!tools.ContainsKey(kvp.Key))
                {
                    tools[kvp.Key] = kvp.Value;
                }
            }
            settings["tools"] = tools;
            if (settings["assistants"] is JObject assistants)
            {
                if (assistants[AssistantConstants.DefaultId] is JObject defaultAssistant)
                {
                    if (defaultAssistant["enabledToolIds"] is not JArray existing || existing.Count == 0)
                    {
                        defaultAssistant["enabledToolIds"] = new JArray(ToolConstants.BuiltInIds.Cast<object>().ToArray());
                    }
                }
                foreach (KeyValuePair<string, JToken> kvp in assistants)
                {
                    if (kvp.Value is JObject assistant && assistant["enabledToolIds"] is null)
                    {
                        assistant["enabledToolIds"] = new JArray();
                    }
                }
            }
            SettingsService.SaveSettings(settings);
            Program.Sessions.GenericSharedUser.SaveGenericData(SettingsService.DataName, MigratedToolsKey, "true");
            Logs.Info("[LLMAssistant] Seeded built-in tools and tool assignments.");
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Tool migration failed: {ex.Message}");
        }
    }

    /// <summary>Idempotent top-up: ensures every built-in tool defined in <see cref="ToolRegistryService.BuildDefaultTools"/>
    /// is present in settings, so existing installs automatically pick up new built-ins added in later versions.
    /// Does NOT auto-enable new tools on existing assistants — the user explicitly opts in (especially for dangerous
    /// tools like <c>shell_exec</c>).</summary>
    private static void BackfillBuiltInTools()
    {
        try
        {
            JObject settings = SettingsService.GetSettings();
            JObject tools = settings["tools"] as JObject ?? new JObject();
            JObject defaultTools = ToolRegistryService.BuildDefaultTools();
            bool changed = false;
            foreach (KeyValuePair<string, JToken> kvp in defaultTools)
            {
                if (!tools.ContainsKey(kvp.Key))
                {
                    tools[kvp.Key] = kvp.Value;
                    changed = true;
                    Logs.Info($"[LLMAssistant] Backfilled new built-in tool: {kvp.Key}");
                }
            }
            if (changed)
            {
                settings["tools"] = tools;
                SettingsService.SaveSettings(settings);
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Built-in tool backfill failed: {ex.Message}");
        }
    }

    /// <summary>Migrates existing instructions + custom instructions into the assistants model.</summary>
    private static void MigrateToAssistants()
    {
        try
        {
            string migrated = Program.Sessions.GenericSharedUser.GetGenericData(SettingsService.DataName, MigratedAssistantsKey);
            if (!string.IsNullOrEmpty(migrated))
            {
                return;
            }
            JObject settings = SettingsService.GetSettings();
            // If assistants already have more than just the default, skip
            if (settings["assistants"] is JObject existing && existing.Count > 0)
            {
                Program.Sessions.GenericSharedUser.SaveGenericData(SettingsService.DataName, MigratedAssistantsKey, "true");
                return;
            }
            // Build default assistant from existing instruction overrides
            JObject defaultAssistant = SettingsService.BuildDefaultAssistant();
            JObject instructions = settings["instructions"] as JObject;
            if (instructions is not null)
            {
                JObject assistantInstr = defaultAssistant["instructions"] as JObject;
                foreach (string key in InstructionIds.All)
                {
                    if (instructions[key] is JValue val && !string.IsNullOrEmpty(val.ToString()))
                    {
                        assistantInstr[key] = val.ToString();
                    }
                }
            }
            JObject assistants = new()
            {
                [AssistantConstants.DefaultId] = defaultAssistant
            };
            // Convert custom instructions into separate assistants
            JObject custom = instructions?["custom"] as JObject;
            if (custom is not null)
            {
                foreach (KeyValuePair<string, JToken> kvp in custom)
                {
                    if (kvp.Value is not JObject customInstr)
                    {
                        continue;
                    }
                    string content = customInstr["content"]?.ToString();
                    if (string.IsNullOrEmpty(content))
                    {
                        continue;
                    }
                    string title = customInstr["title"]?.ToString() ?? kvp.Key;
                    JObject newAssistant = (JObject)defaultAssistant.DeepClone();
                    string newId = $"migrated-{kvp.Key}";
                    newAssistant["id"] = newId;
                    newAssistant["name"] = title;
                    newAssistant["description"] = customInstr["tooltip"]?.ToString() ?? "";
                    newAssistant["isBuiltIn"] = false;
                    (newAssistant["instructions"] as JObject)[InstructionIds.Chat] = content;
                    assistants[newId] = newAssistant;
                }
            }
            settings["assistants"] = assistants;
            settings["activeAssistantId"] = AssistantConstants.DefaultId;
            SettingsService.SaveSettings(settings);
            Program.Sessions.GenericSharedUser.SaveGenericData(SettingsService.DataName, MigratedAssistantsKey, "true");
            Logs.Info("[LLMAssistant] Migrated instructions to assistants model.");
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Assistant migration failed: {ex.Message}");
        }
    }

}
