using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Instruction CRUD endpoints.</summary>
public static class InstructionEndpoints
{
    public static async Task<JObject> LLMAssistantGetInstructions(Session session)
    {
        JArray instructions = InstructionService.GetInstructionList();
        return new JObject
        {
            ["success"] = true,
            ["instructions"] = instructions
        };
    }

    public static async Task<JObject> LLMAssistantSaveInstruction(Session session, JObject rawInput)
    {
        string id = rawInput["id"]?.ToString();
        string title = rawInput["title"]?.ToString();
        string content = rawInput["content"]?.ToString();
        bool isBuiltIn = rawInput["isBuiltIn"]?.Value<bool>() ?? false;
        if (string.IsNullOrEmpty(content))
        {
            return new JObject { ["success"] = false, ["error"] = "Instruction content is required." };
        }
        JObject settings = SettingsService.GetSettings();
        JObject instructions = settings["instructions"] as JObject;
        if (isBuiltIn && instructions.ContainsKey(id))
        {
            // Update built-in instruction content
            instructions[id] = content;
        }
        else
        {
            // Custom instruction
            if (string.IsNullOrEmpty(id))
            {
                id = $"custom-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            }
            JObject custom = instructions["custom"] as JObject ?? new JObject();
            custom[id] = new JObject
            {
                ["id"] = id,
                ["title"] = title ?? id,
                ["content"] = content,
                ["tooltip"] = rawInput["tooltip"]?.ToString() ?? "",
                ["categories"] = rawInput["categories"] ?? new JArray(),
                ["created"] = custom[id]?["created"]?.ToString() ?? DateTime.UtcNow.ToString("o"),
                ["updated"] = DateTime.UtcNow.ToString("o")
            };
            instructions["custom"] = custom;
        }
        SettingsService.SaveSettings(settings);
        return new JObject
        {
            ["success"] = true,
            ["id"] = id
        };
    }

    public static async Task<JObject> LLMAssistantDeleteInstruction(Session session, string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return new JObject { ["success"] = false, ["error"] = "Instruction ID is required." };
        }
        JObject settings = SettingsService.GetSettings();
        JObject instructions = settings["instructions"] as JObject;
        JObject custom = instructions?["custom"] as JObject;
        if (custom is null || !custom.ContainsKey(id))
        {
            return new JObject { ["success"] = false, ["error"] = $"Custom instruction '{id}' not found." };
        }
        custom.Remove(id);
        SettingsService.SaveSettings(settings);
        return new JObject { ["success"] = true };
    }
}
