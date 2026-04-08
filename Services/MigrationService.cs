using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>One-time migration from MagicPrompt extension settings.</summary>
public static class MigrationService
{
    private const string MigratedKey = "migrated";

    /// <summary>Runs migration if MagicPrompt settings exist and we haven't migrated yet.</summary>
    public static void RunIfNeeded()
    {
        try
        {
            string migrated = Program.Sessions.GenericSharedUser.GetGenericData(SettingsService.DataName, MigratedKey);
            if (!string.IsNullOrEmpty(migrated))
            {
                return; // Already migrated
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
            // Don't block startup on migration failure
        }
    }

    private static void MigrateSettings(JObject mpSettings)
    {
        JObject newSettings = SettingsService.DefaultSettings;
        // Migrate instructions
        if (mpSettings["instructions"] is JObject mpInstructions)
        {
            JObject newInstructions = newSettings["instructions"] as JObject;
            string[] builtInKeys = ["chat", "vision", "caption", "prompt", "randomprompt", "instructiongen"];
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
