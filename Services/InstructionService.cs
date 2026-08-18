using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Resolves instructions by ID/title and substitutes variables.
///
/// <para>Instructions live in <c>settings.instructions</c> and are read from the user's merged
/// view — a user sees shared/admin instruction defaults plus any of their own custom instructions
/// transparently.</para>
/// </summary>
public static class InstructionService
{
    /// <summary>Regex for variable references: {{variableName}}.</summary>
    private static readonly Regex VariablePattern = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    /// <summary>Resolves an instruction ID to its content text using the user's merged view.</summary>
    public static string ResolveInstruction(string instructionId, JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject instructions = settings["instructions"] as JObject;
        if (instructions is null)
        {
            return DefaultInstructions.Prompt;
        }
        if (string.IsNullOrEmpty(instructionId))
        {
            instructionId = GetFeatureMapping(FeatureKeys.ChatMode, settings, user);
        }
        // Check built-in instructions first
        if (instructions[instructionId] is JValue builtIn)
        {
            return builtIn.ToString();
        }
        // Check custom instructions by ID
        JObject custom = instructions["custom"] as JObject;
        if (custom?[instructionId] is JObject customInstr)
        {
            return customInstr["content"]?.ToString() ?? DefaultInstructions.Prompt;
        }
        // Check custom instructions by title (case-insensitive)
        if (custom is not null)
        {
            foreach (KeyValuePair<string, JToken> entry in custom)
            {
                if (entry.Value is JObject obj && string.Equals(obj["title"]?.ToString(), instructionId, StringComparison.OrdinalIgnoreCase))
                {
                    return obj["content"]?.ToString() ?? DefaultInstructions.Prompt;
                }
            }
        }
        // Fallback to prompt instruction
        return instructions[InstructionIds.Prompt]?.ToString() ?? DefaultInstructions.Prompt;
    }

    /// <summary>Gets the instruction ID mapped to a feature.</summary>
    public static string GetFeatureMapping(string featureName, JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject mappings = settings["featureMappings"] as JObject;
        return mappings?[featureName]?.ToString() ?? InstructionIds.Prompt;
    }

    /// <summary>Substitutes {{variable}} placeholders in instruction text.</summary>
    public static string SubstituteVariables(string instruction, Dictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(instruction) || variables is null || variables.Count == 0)
        {
            return instruction;
        }
        return VariablePattern.Replace(instruction, match =>
        {
            string varName = match.Groups[1].Value;
            return variables.TryGetValue(varName, out string value) ? value : match.Value;
        });
    }

    /// <summary>Gets all available instructions (built-in + custom) as a list for UI.</summary>
    public static JArray GetInstructionList(JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject instructions = settings["instructions"] as JObject;
        JArray result = [];
        if (instructions is null) return result;
        // Built-in instructions
        string[] builtInKeys = InstructionIds.All;
        foreach (string key in builtInKeys)
        {
            if (instructions[key] is JValue val)
            {
                result.Add(new JObject
                {
                    ["id"] = key,
                    ["title"] = key[0].ToString().ToUpper() + key[1..],
                    ["content"] = val.ToString(),
                    ["isBuiltIn"] = true,
                    ["categories"] = new JArray()
                });
            }
        }
        // Custom instructions
        if (instructions["custom"] is JObject custom)
        {
            foreach (KeyValuePair<string, JToken> entry in custom)
            {
                if (entry.Value is JObject obj)
                {
                    result.Add(new JObject
                    {
                        ["id"] = entry.Key,
                        ["title"] = obj["title"]?.ToString() ?? entry.Key,
                        ["content"] = obj["content"]?.ToString() ?? "",
                        ["tooltip"] = obj["tooltip"]?.ToString() ?? "",
                        ["categories"] = obj["categories"] ?? new JArray(),
                        ["isBuiltIn"] = false,
                        ["created"] = obj["created"]?.ToString(),
                        ["updated"] = obj["updated"]?.ToString()
                    });
                }
            }
        }
        return result;
    }
}
