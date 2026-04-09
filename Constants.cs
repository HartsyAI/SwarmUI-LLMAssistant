namespace SwarmUI.Extensions.LLMAssistant;

/// <summary>Built-in instruction IDs used across the extension.</summary>
public static class InstructionIds
{
    public const string Chat = "chat";
    public const string Vision = "vision";
    public const string Caption = "caption";
    public const string Prompt = "prompt";
    public const string RandomPrompt = "randomprompt";
    public const string InstructionGen = "instructiongen";

    /// <summary>All built-in instruction IDs in display order.</summary>
    public static readonly string[] All = [Chat, Vision, Caption, Prompt, RandomPrompt, InstructionGen];
}

/// <summary>Feature mapping keys that bind UI features to instruction IDs.</summary>
public static class FeatureKeys
{
    public const string EnhancePrompt = "enhance-prompt";
    public const string MagicVision = "magic-vision";
    public const string ChatMode = "chat-mode";
    public const string VisionMode = "vision-mode";
    public const string PromptMode = "prompt-mode";
    public const string RandomPrompt = "random-prompt";
    public const string GenerateInstruction = "generate-instruction";
}

/// <summary>Message role identifiers.</summary>
public static class Roles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}
