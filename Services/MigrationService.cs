using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>One-time migrations from MagicPrompt and instructions-to-assistants.</summary>
public static class MigrationService
{
    private const string MigratedKey = "migrated";
    private const string MigratedAssistantsKey = "migrated_assistants";
    private const string MigratedToolsKey = "migrated_tools";

    /// <summary>Runs all pending migrations.</summary>
    public static void RunIfNeeded()
    {
        MigrateFromMagicPrompt();
        MigrateToAssistants();
        MigrateTools();
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

    private static void MigrateFromMagicPrompt()
    {
        try
        {
            string migrated = Program.Sessions.GenericSharedUser.GetGenericData(SettingsService.DataName, MigratedKey);
            if (!string.IsNullOrEmpty(migrated))
            {
                return;
            }
            string mpConfig = Program.Sessions.GenericSharedUser.GetGenericData("magicprompt", "config");
            if (string.IsNullOrEmpty(mpConfig))
            {
                Logs.Info("[LLMAssistant] No MagicPrompt settings found, skipping migration.");
                Program.Sessions.GenericSharedUser.SaveGenericData(SettingsService.DataName, MigratedKey, "true");
                return;
            }
            JObject mpSettings = JObject.Parse(mpConfig);
            MigrateSettings(mpSettings);
            Program.Sessions.GenericSharedUser.SaveGenericData(SettingsService.DataName, MigratedKey, "true");
            Logs.Info("[LLMAssistant] Successfully migrated settings from MagicPrompt.");
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Migration from MagicPrompt failed: {ex.Message}");
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

    private static void MigrateSettings(JObject mpSettings)
    {
        JObject newSettings = SettingsService.DefaultSettings;
        // Migrate instructions
        if (mpSettings["instructions"] is JObject mpInstructions)
        {
            JObject newInstructions = newSettings["instructions"] as JObject;
            string[] builtInKeys = InstructionIds.All;
            foreach (string key in builtInKeys)
            {
                if (mpInstructions[key] is JValue val && !string.IsNullOrEmpty(val.ToString()))
                {
                    newInstructions[key] = val.DeepClone();
                }
            }
            // Migrate custom instructions
            if (mpInstructions["custom"] is JObject mpCustom)
            {
                newInstructions["custom"] = mpCustom.DeepClone();
            }
        }
        // Migrate model preferences (best-effort)
        if (mpSettings["model"] is JValue model && !string.IsNullOrEmpty(model.ToString()))
        {
            newSettings["preferredModel"] = model.ToString();
        }
        if (mpSettings["visionmodel"] is JValue visionModel && !string.IsNullOrEmpty(visionModel.ToString()))
        {
            newSettings["preferredVisionModel"] = visionModel.ToString();
        }
        // Note: Backend URLs/auth NOT migrated — different architecture (Swarm native vs extension HTTP)
        SettingsService.SaveSettings(newSettings);
    }
}
