using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Manages extension-level settings (instructions, features, parameters). Backend config is in Server > Backends.
///
/// <para>Storage model (multi-user aware):</para>
/// <list type="bullet">
/// <item><b>Shared layer</b> — one blob at <c>__shared/llmassistant/config</c>, containing the admin-curated
/// baseline (shared assistants, shared tools, default instructions, default parameters). Only users with the
/// <c>llm_shared_write</c> permission may mutate this layer.</item>
/// <item><b>User layer</b> — one blob per user at <c>{userId}/llmassistant/user_config</c>, containing that
/// user's personal overrides, personal assistants, personal tools, and their preferred model / parameters.</item>
/// </list>
/// <para>A user's effective view (<see cref="GetMergedSettings"/>) is shared ⊕ user. For dict-valued fields like
/// <c>assistants</c> and <c>tools</c>, entries are unioned and the personal layer wins on ID collision. The
/// extension tags each entry with a <c>_scope</c> marker (<c>"shared"</c> or <c>"personal"</c>) so the UI can
/// render badges and know which layer a delete targets.</para>
/// </summary>
public static class SettingsService
{
    public const string DataName = "llmassistant";
    public const string ConfigKey = "config";           // shared / admin-managed baseline
    public const string UserConfigKey = "user_config";  // per-user override layer

    /// <summary>Scope marker injected on dict entries (assistants/tools) so the UI can distinguish
    /// admin-managed shared items from the user's personal items.</summary>
    public const string ScopeShared = "shared";
    public const string ScopePersonal = "personal";

    /// <summary>Default settings structure.</summary>
    public static JObject DefaultSettings => new()
    {
        ["preferredModel"] = "",
        ["preferredVisionModel"] = "",
        ["activeAssistantId"] = AssistantConstants.DefaultId,
        ["parameters"] = new JObject
        {
            ["temperature"] = 1.0,
            ["maxTokens"] = 1024,
            ["topP"] = 0.9,
            ["maxContextMessages"] = 0
        },
        ["assistants"] = new JObject
        {
            [AssistantConstants.DefaultId] = BuildDefaultAssistant()
        },
        ["tools"] = ToolRegistryService.BuildDefaultTools(),
        // Legacy keys kept for backward compatibility and T2I prompt tags
        ["instructions"] = new JObject
        {
            [InstructionIds.Chat] = DefaultInstructions.Chat,
            [InstructionIds.Vision] = DefaultInstructions.Vision,
            [InstructionIds.Caption] = DefaultInstructions.Caption,
            [InstructionIds.Prompt] = DefaultInstructions.Prompt,
            [InstructionIds.RandomPrompt] = DefaultInstructions.RandomPrompt,
            [InstructionIds.InstructionGen] = DefaultInstructions.InstructionGen,
            ["custom"] = new JObject()
        },
        ["featureMappings"] = new JObject
        {
            [FeatureKeys.EnhancePrompt] = InstructionIds.Prompt,
            [FeatureKeys.MagicVision] = InstructionIds.Caption,
            [FeatureKeys.ChatMode] = InstructionIds.Chat,
            [FeatureKeys.VisionMode] = InstructionIds.Vision,
            [FeatureKeys.PromptMode] = InstructionIds.Prompt,
            [FeatureKeys.RandomPrompt] = InstructionIds.RandomPrompt,
            [FeatureKeys.GenerateInstruction] = InstructionIds.InstructionGen
        }
    };

    /// <summary>Builds the default assistant object with all built-in instructions.</summary>
    public static JObject BuildDefaultAssistant() => new()
    {
        ["id"] = AssistantConstants.DefaultId,
        ["name"] = "Default Assistant",
        ["description"] = "General-purpose AI assistant for SwarmUI",
        ["icon"] = "chat",
        ["color"] = "#7c8aff",
        ["instructions"] = new JObject
        {
            [InstructionIds.Chat] = DefaultInstructions.Chat,
            [InstructionIds.Vision] = DefaultInstructions.Vision,
            [InstructionIds.Caption] = DefaultInstructions.Caption,
            [InstructionIds.Prompt] = DefaultInstructions.Prompt,
            [InstructionIds.RandomPrompt] = DefaultInstructions.RandomPrompt,
            [InstructionIds.InstructionGen] = DefaultInstructions.InstructionGen
        },
        ["parameters"] = new JObject(),
        ["enabledToolIds"] = new JArray(ToolConstants.BuiltInIds.Cast<object>().ToArray()),
        ["isBuiltIn"] = true,
        ["created"] = DateTime.UtcNow.ToString("o"),
        ["updated"] = DateTime.UtcNow.ToString("o")
    };

    private static readonly JsonMergeSettings MergeSettings = new()
    {
        MergeArrayHandling = MergeArrayHandling.Replace,
        MergeNullValueHandling = MergeNullValueHandling.Merge
    };

    /// <summary>Gets the current settings, merged with defaults for any missing keys.</summary>
    public static JObject GetSettings()
    {
        string raw = Program.Sessions.GenericSharedUser.GetGenericData(DataName, ConfigKey);
        JObject settings = string.IsNullOrEmpty(raw) ? new JObject() : JObject.Parse(raw);
        JObject result = (JObject)DefaultSettings.DeepClone();
        result.Merge(settings, MergeSettings);
        return result;
    }

    /// <summary>Saves settings (merges with existing). Legacy merge semantics — use
    /// <see cref="ReplaceSharedSettings"/> for write paths that need to remove keys (deletes).</summary>
    public static JObject SaveSettings(JObject incoming)
    {
        JObject current = GetSettings();
        current.Merge(incoming, MergeSettings);
        Program.Sessions.GenericSharedUser.SaveGenericData(DataName, ConfigKey, current.ToString(Formatting.None));
        return current;
    }

    /// <summary>Fully replaces the shared settings blob. Unlike <see cref="SaveSettings"/>, keys
    /// that are missing from <paramref name="replacement"/> will be removed on disk. Use this for
    /// any mutation of assistants/tools/instructions so that deletes are honored.</summary>
    public static JObject ReplaceSharedSettings(JObject replacement)
    {
        replacement ??= [];
        Program.Sessions.GenericSharedUser.SaveGenericData(DataName, ConfigKey, replacement.ToString(Formatting.None));
        return replacement;
    }

    /// <summary>Resets shared settings to defaults. Does NOT touch user overrides.</summary>
    public static JObject ResetSettings()
    {
        JObject defaults = DefaultSettings;
        Program.Sessions.GenericSharedUser.SaveGenericData(DataName, ConfigKey, defaults.ToString(Formatting.None));
        return defaults;
    }

    /// <summary>Loads a user's personal override layer. Returns an empty object if the user has no overrides.</summary>
    public static JObject GetUserSettings(User user)
    {
        if (user is null)
        {
            return [];
        }
        string raw = user.GetGenericData(DataName, UserConfigKey);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }
        try
        {
            return JObject.Parse(raw);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Replaces a user's personal override layer in full.</summary>
    public static void ReplaceUserSettings(User user, JObject userSettings)
    {
        if (user is null)
        {
            return;
        }
        userSettings ??= [];
        user.SaveGenericData(DataName, UserConfigKey, userSettings.ToString(Formatting.None));
    }

    /// <summary>Merges the given patch into the user's personal override layer. Returns the new personal layer.</summary>
    public static JObject PatchUserSettings(User user, JObject patch)
    {
        JObject current = GetUserSettings(user);
        if (patch is not null)
        {
            current.Merge(patch, MergeSettings);
        }
        ReplaceUserSettings(user, current);
        return current;
    }

    /// <summary>Clears a user's personal override layer entirely (reset-to-defaults for that user).
    /// Does NOT touch the shared layer.</summary>
    public static void ResetUserSettings(User user)
    {
        user?.DeleteGenericData(DataName, UserConfigKey);
    }

    /// <summary>Returns the effective settings for a user: shared baseline ⊕ user overrides.
    /// Dict-valued fields (<c>assistants</c>, <c>tools</c>) are unioned with per-entry <c>_scope</c> tags;
    /// personal entries win on ID collision. Scalar fields and object fields use the standard merge rules
    /// (personal wins, null values are preserved).</summary>
    public static JObject GetMergedSettings(User user)
    {
        JObject shared = GetSettings();
        if (user is null)
        {
            AnnotateScopes(shared, ScopeShared);
            return shared;
        }
        JObject personal = GetUserSettings(user);
        // Union merge for assistants/tools (the dicts need scope tagging)
        JObject mergedAssistants = UnionMergeDict(shared["assistants"] as JObject, personal["assistants"] as JObject);
        JObject mergedTools = UnionMergeDict(shared["tools"] as JObject, personal["tools"] as JObject);
        // Everything else: shallow merge with personal winning
        JObject result = (JObject)shared.DeepClone();
        result["assistants"] = mergedAssistants;
        result["tools"] = mergedTools;
        // Merge scalar + object fields (instructions, parameters, featureMappings, preferredModel, etc.)
        JObject personalClone = (JObject)personal.DeepClone();
        personalClone.Remove("assistants");
        personalClone.Remove("tools");
        result.Merge(personalClone, MergeSettings);
        return result;
    }

    /// <summary>Union-merges two dicts of entries (e.g., assistants or tools), tagging each entry's scope.
    /// Personal entries override shared entries with the same key.</summary>
    private static JObject UnionMergeDict(JObject sharedDict, JObject personalDict)
    {
        JObject result = [];
        if (sharedDict is not null)
        {
            foreach (KeyValuePair<string, JToken> kvp in sharedDict)
            {
                if (kvp.Value is not JObject entry)
                {
                    continue;
                }
                JObject clone = (JObject)entry.DeepClone();
                clone["_scope"] = ScopeShared;
                result[kvp.Key] = clone;
            }
        }
        if (personalDict is not null)
        {
            foreach (KeyValuePair<string, JToken> kvp in personalDict)
            {
                if (kvp.Value is not JObject entry)
                {
                    continue;
                }
                JObject clone = (JObject)entry.DeepClone();
                clone["_scope"] = ScopePersonal;
                result[kvp.Key] = clone;
            }
        }
        return result;
    }

    /// <summary>Tags every assistant/tool entry in a settings blob with the given scope marker.
    /// Used when returning the shared settings as-is for anonymous (null user) calls.</summary>
    private static void AnnotateScopes(JObject settings, string scope)
    {
        if (settings?["assistants"] is JObject assistants)
        {
            foreach (KeyValuePair<string, JToken> kvp in assistants)
            {
                if (kvp.Value is JObject entry)
                {
                    entry["_scope"] = scope;
                }
            }
        }
        if (settings?["tools"] is JObject tools)
        {
            foreach (KeyValuePair<string, JToken> kvp in tools)
            {
                if (kvp.Value is JObject entry)
                {
                    entry["_scope"] = scope;
                }
            }
        }
    }

    /// <summary>Strips the <c>_scope</c> marker from an entry before saving it back. The scope is metadata
    /// that lives in the merged view only; it should never end up in either the shared or user blob.</summary>
    public static JObject StripScope(JObject entry)
    {
        if (entry is null)
        {
            return null;
        }
        JObject clone = (JObject)entry.DeepClone();
        clone.Remove("_scope");
        return clone;
    }
}

/// <summary>Default instruction texts for each feature.</summary>
public static class DefaultInstructions
{
    public const string Chat = """
        You are a helpful AI assistant integrated into SwarmUI, a Stable Diffusion image generation interface.
        You can help users with prompt crafting, image generation tips, and general conversation.
        Be concise but thorough. When discussing image prompts, use Stable Diffusion terminology.
        """;

    public const string Vision = """
        You are a vision assistant. Analyze the provided image in detail.
        Describe what you see, including subjects, composition, style, colors, lighting, and mood.
        If asked specific questions about the image, answer them directly.
        """;

    public const string Caption = """
        Generate a detailed caption for this image suitable for use as a Stable Diffusion prompt.
        Focus on: subject description, art style, medium, lighting, color palette, composition, mood, and quality tags.
        Output ONLY the caption text, no explanations or prefixes.
        Use comma-separated tags and natural language descriptions mixed together.
        """;

    public const string Prompt = """
        You are a Stable Diffusion prompt expert. Take the user's basic idea and transform it into
        a detailed, high-quality image generation prompt. Include: subject details, art style, medium,
        lighting, color palette, composition, mood, and quality enhancers.
        Output ONLY the enhanced prompt, no explanations.
        """;

    public const string RandomPrompt = """
        Generate a random, creative, and detailed Stable Diffusion prompt for an interesting image.
        Be creative and varied. Include: subject, style, medium, lighting, colors, mood, and quality tags.
        Output ONLY the prompt, no explanations.
        """;

    public const string InstructionGen = """
        You are a system prompt engineer. Based on the user's description of what they want an AI to do,
        create a clear, effective system prompt / instruction set. The instruction should be specific,
        well-structured, and optimized for LLM performance.
        Output ONLY the instruction text, no meta-commentary.
        """;
}
