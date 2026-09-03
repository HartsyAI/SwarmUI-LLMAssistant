namespace Hartsy.Extensions.LLMAssistant;

/// <summary>Built-in instruction IDs used across the extension.</summary>
public static class InstructionIds
{
    /// <summary>Default chat instruction id.</summary>
    public const string Chat = "chat";
    /// <summary>Vision (image-analysis) instruction id.</summary>
    public const string Vision = "vision";
    /// <summary>Image-captioning instruction id.</summary>
    public const string Caption = "caption";
    /// <summary>Prompt-enhancement instruction id.</summary>
    public const string Prompt = "prompt";
    /// <summary>Random-prompt-generation instruction id.</summary>
    public const string RandomPrompt = "randomprompt";
    /// <summary>Instruction-generation (meta) instruction id.</summary>
    public const string InstructionGen = "instructiongen";
    /// <summary>Short, glanceable single-paragraph replies tuned for the floating Companion overlay
    /// (Clippy-style helper). Used when a Companion message is sent — falls back to <see cref="Chat"/>
    /// if an assistant has not defined its own companion text.</summary>
    public const string Companion = "companion";

    /// <summary>All built-in instruction IDs in display order.</summary>
    public static readonly string[] All = [Chat, Vision, Caption, Prompt, RandomPrompt, InstructionGen, Companion];
}

/// <summary>Feature mapping keys that bind UI features to instruction IDs.</summary>
public static class FeatureKeys
{
    /// <summary>Feature key for the "enhance prompt" T2I tag.</summary>
    public const string EnhancePrompt = "enhance-prompt";
    /// <summary>Feature key for the "magic vision" T2I tag.</summary>
    public const string MagicVision = "magic-vision";
    /// <summary>Feature key for chat mode.</summary>
    public const string ChatMode = "chat-mode";
    /// <summary>Feature key for vision mode.</summary>
    public const string VisionMode = "vision-mode";
    /// <summary>Feature key for prompt mode.</summary>
    public const string PromptMode = "prompt-mode";
    /// <summary>Feature key for random-prompt generation.</summary>
    public const string RandomPrompt = "random-prompt";
    /// <summary>Feature key for instruction generation.</summary>
    public const string GenerateInstruction = "generate-instruction";
    /// <summary>Feature key for the Companion overlay.</summary>
    public const string CompanionMode = "companion-mode";
}

/// <summary>Message role identifiers.</summary>
public static class Roles
{
    /// <summary>The end user's role.</summary>
    public const string User = "user";
    /// <summary>The LLM's role.</summary>
    public const string Assistant = "assistant";
    /// <summary>The system/instruction role.</summary>
    public const string System = "system";
}

/// <summary>Assistant-related constants.</summary>
public static class AssistantConstants
{
    /// <summary>ID of the built-in default assistant.</summary>
    public const string DefaultId = "default";
}

/// <summary>Tool-calling related constants.</summary>
public static class ToolConstants
{
    /// <summary>Maximum number of agentic iterations (tool call rounds) per user message.</summary>
    public const int MaxAgenticIterations = 8;

    /// <summary>Handler type: built-in C# ToolHandler implementation.</summary>
    public const string HandlerBuiltIn = "builtin";

    /// <summary>Reserved: MCP server over stdio.</summary>
    public const string HandlerMcpStdio = "mcp_stdio";

    /// <summary>Reserved: MCP server over HTTP.</summary>
    public const string HandlerMcpHttp = "mcp_http";

    /// <summary>ID of the built-in image-generation tool.</summary>
    public const string GenerateImage = "generate_image";
    /// <summary>ID of the built-in T2I-preset-creation tool.</summary>
    public const string CreateImagePreset = "create_image_preset";
    /// <summary>ID of the built-in image-captioning tool.</summary>
    public const string CaptionImage = "caption_image";
    /// <summary>ID of the built-in tool that fuses multiple image descriptions.</summary>
    public const string FuseImageDescriptions = "fuse_image_descriptions";
    /// <summary>ID of the built-in batch folder-captioning tool.</summary>
    public const string BatchCaptionFolder = "batch_caption_folder";
    /// <summary>ID of the built-in web-search tool.</summary>
    public const string WebSearch = "web_search";
    /// <summary>ID of the built-in sandboxed file-read tool.</summary>
    public const string FileRead = "file_read";
    /// <summary>ID of the built-in sandboxed file-write tool.</summary>
    public const string FileWrite = "file_write";
    /// <summary>ID of the built-in outbound HTTP request tool.</summary>
    public const string HttpRequest = "http_request";
    /// <summary>ID of the built-in shell-execution tool.</summary>
    public const string ShellExec = "shell_exec";
    /// <summary>ID of the built-in memory-write tool.</summary>
    public const string MemoryWrite = "memory_write";
    /// <summary>ID of the built-in memory-read tool.</summary>
    public const string MemoryRead = "memory_read";
    /// <summary>ID of the built-in SwarmUI docs lookup tool.</summary>
    public const string SwarmDocs = "swarm_docs";

    /// <summary>ID of the voice-satellite LED-show tool. Executed by the device, not the server.</summary>
    public const string SetLedProfile = "set_led_profile";
    /// <summary>ID of the voice-satellite volume tool. Executed by the device, not the server.</summary>
    public const string SetVolume = "set_volume";
    /// <summary>ID of the voice-satellite microphone-mute tool. Executed by the device, not the server.</summary>
    public const string MuteMic = "mute_mic";

    /// <summary>All built-in tool IDs.</summary>
    public static readonly string[] BuiltInIds = [GenerateImage, CreateImagePreset, CaptionImage, FuseImageDescriptions, BatchCaptionFolder, WebSearch, FileRead, FileWrite, HttpRequest, ShellExec, MemoryWrite, MemoryRead, SwarmDocs, SetLedProfile, SetVolume, MuteMic];
}
