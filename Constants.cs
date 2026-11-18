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
    /// <summary>Video-prompt-enhancement instruction id. Like <see cref="Prompt"/>, but for text-to-video prompts (motion, camera, pacing).</summary>
    public const string VideoPrompt = "video-prompt";
    /// <summary>Instruction-generation (meta) instruction id.</summary>
    public const string InstructionGen = "instructiongen";
    /// <summary>Short, glanceable single-paragraph replies tuned for the floating Companion overlay
    /// (Clippy-style helper). Used when a Companion message is sent; falls back to <see cref="Chat"/>
    /// if an assistant has not defined its own companion text.</summary>
    public const string Companion = "companion";

    /// <summary>All built-in instruction IDs in display order.</summary>
    public static readonly string[] All = [Chat, Vision, Caption, Prompt, RandomPrompt, InstructionGen, Companion, VideoPrompt];
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

/// <summary>Thread modes: an ordinary chat, or a coding session in a workspace. See HARNESS-ROADMAP.md.</summary>
public static class ThreadModes
{
    /// <summary>The ordinary chat workspace. Threads saved without a mode are this.</summary>
    public const string Chat = "chat";
    /// <summary>Coding sessions: the chat works in a workspace, brings the code tools and shows the workspace panel (see <see cref="Services.CodeModeService"/>).</summary>
    public const string Code = "code";

    /// <summary>Whether <paramref name="mode"/> is a known thread mode.</summary>
    public static bool IsValid(string mode) => mode is Chat or Code;
}

/// <summary>Reserved: how tool calls are gated in code mode. Not enforced yet. See HARNESS-ROADMAP.md.</summary>
public static class ApprovalModes
{
    /// <summary>Reads run freely; writes, edits and shell commands ask first.</summary>
    public const string Ask = "ask";
    /// <summary>Everything inside the workspace runs without asking.</summary>
    public const string Auto = "auto";
    /// <summary>Read-only tools only, until the user accepts a plan.</summary>
    public const string Plan = "plan";
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
    /// <summary>Longest tool result replayed to the model from saved history; a longer one is cut with a marker.</summary>
    public const int MaxReplayedToolResultChars = 4000;

    /// <summary>Handler type: built-in C# ToolHandler implementation.</summary>
    public const string HandlerBuiltIn = "builtin";

    /// <summary>The handler id of every tool an MCP server offers (one handler serves them all; the tool's definition says which).</summary>
    public const string Mcp = "mcp";

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

    /// <summary>IDs of the code tools: they work in a chat's workspace, are off by default and need <c>llm_code_mode</c>. They are not in
    /// <see cref="BuiltInIds"/>, so no assistant has them until someone switches them on.</summary>
    public const string CodeRead = "code_read", CodeWrite = "code_write", CodeEdit = "code_edit", CodeGlob = "code_glob", CodeGrep = "code_grep", CodeBash = "code_bash", CodeTodo = "code_todo";

    /// <summary>All the code tool IDs (each is its own handler id).</summary>
    public static readonly string[] CodeIds = [CodeRead, CodeWrite, CodeEdit, CodeGlob, CodeGrep, CodeBash, CodeTodo];

    /// <summary>All built-in tool IDs.
    public static readonly string[] BuiltInIds = [GenerateImage, CreateImagePreset, CaptionImage, FuseImageDescriptions, BatchCaptionFolder, WebSearch, FileRead, FileWrite, HttpRequest, ShellExec, MemoryWrite, MemoryRead, SwarmDocs, SetLedProfile, SetVolume, MuteMic];
}
