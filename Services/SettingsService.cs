using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Manages extension-level settings (instructions, features, parameters). Backend config is in Server > Backends.</summary>
public static class SettingsService
{
    public const string DataName = "llmassistant";
    public const string ConfigKey = "config";

    /// <summary>Default settings structure.</summary>
    public static JObject DefaultSettings => new()
    {
        ["preferredModel"] = "",
        ["preferredVisionModel"] = "",
        ["parameters"] = new JObject
        {
            ["temperature"] = 1.0,
            ["maxTokens"] = 1024,
            ["topP"] = 0.9,
            ["maxContextMessages"] = 0
        },
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

    /// <summary>Saves settings (merges with existing).</summary>
    public static JObject SaveSettings(JObject incoming)
    {
        JObject current = GetSettings();
        current.Merge(incoming, MergeSettings);
        Program.Sessions.GenericSharedUser.SaveGenericData(DataName, ConfigKey, current.ToString(Formatting.None));
        return current;
    }

    /// <summary>Resets settings to defaults.</summary>
    public static JObject ResetSettings()
    {
        JObject defaults = DefaultSettings;
        Program.Sessions.GenericSharedUser.SaveGenericData(DataName, ConfigKey, defaults.ToString(Formatting.None));
        return defaults;
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
