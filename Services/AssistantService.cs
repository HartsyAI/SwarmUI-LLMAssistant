using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Centralized service for assistant CRUD, resolution, and parameter merging.</summary>
public static class AssistantService
{
    private static readonly JsonMergeSettings MergeSettings = new()
    {
        MergeArrayHandling = MergeArrayHandling.Replace,
        MergeNullValueHandling = MergeNullValueHandling.Merge
    };

    /// <summary>Gets the active assistant ID from settings.</summary>
    public static string GetActiveAssistantId(JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        return settings["activeAssistantId"]?.ToString() ?? AssistantConstants.DefaultId;
    }

    /// <summary>Gets an assistant by ID. Falls back to default if not found.</summary>
    public static JObject GetAssistant(string assistantId, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
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

    /// <summary>Gets the active assistant object.</summary>
    public static JObject GetActiveAssistant(JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        return GetAssistant(GetActiveAssistantId(settings), settings);
    }

    /// <summary>Resolves instruction text for a feature from the given assistant.
    /// Falls back to Default assistant if the assistant doesn't define it.</summary>
    public static string ResolveInstruction(string featureInstructionId, string assistantId = null, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        assistantId ??= GetActiveAssistantId(settings);
        JObject assistant = GetAssistant(assistantId, settings);
        JObject instructions = assistant["instructions"] as JObject;
        string text = instructions?[featureInstructionId]?.ToString();
        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }
        // Fall back to default assistant
        if (assistantId != AssistantConstants.DefaultId)
        {
            JObject defaultAssistant = GetAssistant(AssistantConstants.DefaultId, settings);
            JObject defaultInstructions = defaultAssistant["instructions"] as JObject;
            text = defaultInstructions?[featureInstructionId]?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }
        return DefaultInstructions.Prompt;
    }

    /// <summary>Resolves parameters by merging global defaults with assistant defaults.</summary>
    public static JObject ResolveParameters(string assistantId = null, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        assistantId ??= GetActiveAssistantId(settings);
        JObject globalParams = settings["parameters"] as JObject ?? new JObject();
        JObject assistant = GetAssistant(assistantId, settings);
        JObject assistantParams = assistant["parameters"] as JObject;
        JObject result = (JObject)globalParams.DeepClone();
        if (assistantParams is not null && assistantParams.Count > 0)
        {
            result.Merge(assistantParams, MergeSettings);
        }
        return result;
    }

    /// <summary>Gets all assistants as a JArray for the UI.</summary>
    public static JArray GetAssistantList(JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
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

    /// <summary>Saves an assistant (create or update).</summary>
    public static string SaveAssistant(JObject assistantData, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        JObject assistants = settings["assistants"] as JObject ?? new JObject();
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
        assistants[id] = assistantData;
        settings["assistants"] = assistants;
        SettingsService.SaveSettings(settings);
        return id;
    }

    /// <summary>Deletes a custom assistant. Cannot delete the default.</summary>
    public static bool DeleteAssistant(string assistantId, JObject settings = null)
    {
        if (assistantId == AssistantConstants.DefaultId)
        {
            return false;
        }
        settings ??= SettingsService.GetSettings();
        JObject assistants = settings["assistants"] as JObject;
        if (assistants is null || !assistants.ContainsKey(assistantId))
        {
            return false;
        }
        assistants.Remove(assistantId);
        if (settings["activeAssistantId"]?.ToString() == assistantId)
        {
            settings["activeAssistantId"] = AssistantConstants.DefaultId;
        }
        SettingsService.SaveSettings(settings);
        return true;
    }

    /// <summary>Sets the active assistant.</summary>
    public static void SetActiveAssistant(string assistantId, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        settings["activeAssistantId"] = assistantId;
        SettingsService.SaveSettings(settings);
    }
}
