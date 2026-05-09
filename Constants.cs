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
    public const string EnhancePrompt = "enhance-prompt";
    public const string MagicVision = "magic-vision";
    public const string ChatMode = "chat-mode";
    public const string VisionMode = "vision-mode";
    public const string PromptMode = "prompt-mode";
    public const string RandomPrompt = "random-prompt";
    public const string GenerateInstruction = "generate-instruction";
    public const string CompanionMode = "companion-mode";
}

/// <summary>Message role identifiers.</summary>
public static class Roles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

/// <summary>Assistant-related constants.</summary>
public static class AssistantConstants
{
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

    // Built-in tool IDs
    public const string GenerateImage = "generate_image";
    public const string CreateImagePreset = "create_image_preset";
    public const string CaptionImage = "caption_image";
    public const string FuseImageDescriptions = "fuse_image_descriptions";
    public const string BatchCaptionFolder = "batch_caption_folder";
    public const string WebSearch = "web_search";
    public const string FileRead = "file_read";
    public const string FileWrite = "file_write";
    public const string HttpRequest = "http_request";
    public const string ShellExec = "shell_exec";
    public const string MemoryWrite = "memory_write";
    public const string MemoryRead = "memory_read";
    public const string SwarmDocs = "swarm_docs";

    /// <summary>All built-in tool IDs.</summary>
    public static readonly string[] BuiltInIds = [GenerateImage, CreateImagePreset, CaptionImage, FuseImageDescriptions, BatchCaptionFolder, WebSearch, FileRead, FileWrite, HttpRequest, ShellExec, MemoryWrite, MemoryRead, SwarmDocs];
}
