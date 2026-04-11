using SwarmUI.Accounts;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Registers all LLM Assistant API endpoints and permissions.</summary>
public static class LLMAssistantAPI
{
    public static readonly PermInfoGroup LLMAssistantPermGroup = new("LLMAssistant",
        "Permissions for the LLM Assistant extension.");

    public static readonly PermInfo PermChat = Permissions.Register(new(
        "llm_chat", "LLM Chat",
        "Allows sending messages to LLM backends.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    public static readonly PermInfo PermSettings = Permissions.Register(new(
        "llm_settings", "LLM Settings",
        "Allows reading and modifying LLM Assistant settings.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    public static readonly PermInfo PermModels = Permissions.Register(new(
        "llm_models", "LLM Models",
        "Allows listing available LLM models.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    public static readonly PermInfo PermThreads = Permissions.Register(new(
        "llm_threads", "LLM Threads",
        "Allows creating and managing chat threads.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to write to the shared/admin layer of LLM Assistant settings
    /// (shared assistants, shared tools, shared default instructions). Without this, a
    /// user's saves always target their personal override layer.</summary>
    public static readonly PermInfo PermSharedWrite = Permissions.Register(new(
        "llm_shared_write", "LLM Shared Write",
        "Allows creating/editing/deleting shared (admin-managed) assistants, tools, and instructions that are visible to every user of the instance. Users without this permission can still create personal assistants/tools, but they only exist for themselves.",
        PermissionDefault.ADMINS, LLMAssistantPermGroup));

    /// <summary>Permission to use the <c>generate_image</c> built-in tool.</summary>
    public static readonly PermInfo PermToolGenerateImage = Permissions.Register(new(
        "llm_tool_generate_image", "[LLM Tool] Generate Image",
        "Allows the LLM to call the built-in generate_image tool to create images via the user's backends.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup, PermSafetyLevel.UNTESTED));

    /// <summary>Permission to use the <c>web_search</c> built-in tool.</summary>
    public static readonly PermInfo PermToolWebSearch = Permissions.Register(new(
        "llm_tool_web_search", "[LLM Tool] Web Search",
        "Allows the LLM to call the built-in web_search tool (DuckDuckGo scrape) to fetch results from the internet.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup, PermSafetyLevel.UNTESTED));

    /// <summary>Permission to use the <c>file_read</c> built-in tool (sandboxed to SwarmUI/Data).</summary>
    public static readonly PermInfo PermToolFileRead = Permissions.Register(new(
        "llm_tool_file_read", "[LLM Tool] Read File",
        "Allows the LLM to call the built-in file_read tool. Sandboxed to SwarmUI's Data directory, but still lets the LLM read arbitrary files within it.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup, PermSafetyLevel.RISKY));

    /// <summary>Permission to use the <c>http_request</c> built-in tool.</summary>
    public static readonly PermInfo PermToolHttpRequest = Permissions.Register(new(
        "llm_tool_http_request", "[LLM Tool] HTTP Request",
        "Allows the LLM to call the built-in http_request tool to make outbound HTTP(S) requests. Blocks loopback/private/link-local addresses, but can still reach any public internet URL from the SwarmUI server's network.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup, PermSafetyLevel.RISKY));

    /// <summary>Permission to use the <c>shell_exec</c> built-in tool. <b>Extremely dangerous</b> — defaults to NOBODY.</summary>
    public static readonly PermInfo PermToolShellExec = Permissions.Register(new(
        "llm_tool_shell_exec", "[LLM Tool] Shell Exec",
        "Allows the LLM to call the built-in shell_exec tool to run arbitrary shell commands on the SwarmUI host. This is equivalent to giving the LLM (and anyone who can chat with it) full local shell access. Only grant to fully trusted admin users on trusted models.",
        PermissionDefault.NOBODY, LLMAssistantPermGroup, PermSafetyLevel.POWERFUL));

    public static void Register()
    {
        // Chat
        API.RegisterAPICall(ChatEndpoints.LLMAssistantSendMessage, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantSendMessageWS, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantCountTokens, false, PermChat);
        // Settings
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantGetSettings, false, PermSettings);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantSaveSettings, true, PermSettings);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantResetSettings, true, PermSettings);
        // Models
        API.RegisterAPICall(ModelEndpoints.LLMAssistantGetModels, false, PermModels);
        // Threads
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantGetThreads, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantGetThread, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantSaveThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantDeleteThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantRenameThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantExportThread, false, PermThreads);
        // Per-user session state (active thread / last model / etc.)
        API.RegisterAPICall(SessionEndpoints.LLMAssistantGetSessionState, false, PermThreads);
        API.RegisterAPICall(SessionEndpoints.LLMAssistantSetSessionState, true, PermThreads);
        // Per-thread assets (artifacts promoted from messages/tool results)
        API.RegisterAPICall(AssetEndpoints.LLMAssistantGetAssets, false, PermThreads);
        API.RegisterAPICall(AssetEndpoints.LLMAssistantGetAsset, false, PermThreads);
        API.RegisterAPICall(AssetEndpoints.LLMAssistantDeleteAsset, true, PermThreads);
        // Instructions (legacy, kept for T2I prompt tag compatibility)
        API.RegisterAPICall(InstructionEndpoints.LLMAssistantGetInstructions, false, PermSettings);
        API.RegisterAPICall(InstructionEndpoints.LLMAssistantSaveInstruction, true, PermSettings);
        API.RegisterAPICall(InstructionEndpoints.LLMAssistantDeleteInstruction, true, PermSettings);
        // Assistants
        API.RegisterAPICall(AssistantEndpoints.LLMAssistantGetAssistants, false, PermSettings);
        API.RegisterAPICall(AssistantEndpoints.LLMAssistantGetAssistant, false, PermSettings);
        API.RegisterAPICall(AssistantEndpoints.LLMAssistantGetActiveAssistant, false, PermSettings);
        API.RegisterAPICall(AssistantEndpoints.LLMAssistantSaveAssistant, true, PermSettings);
        API.RegisterAPICall(AssistantEndpoints.LLMAssistantDeleteAssistant, true, PermSettings);
        API.RegisterAPICall(AssistantEndpoints.LLMAssistantSetActiveAssistant, true, PermSettings);
        // Tools
        API.RegisterAPICall(ToolEndpoints.LLMAssistantGetTools, false, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantGetTool, false, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantSaveTool, true, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantDeleteTool, true, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantExecuteTool, true, PermSettings);
    }
}
