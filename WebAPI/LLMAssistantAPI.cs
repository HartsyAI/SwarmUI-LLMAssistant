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

    /// <summary>Permission to use the <c>create_image_preset</c> built-in tool. The actual preset
    /// save also requires SwarmUI's core <c>manage_presets</c> permission (checked at the
    /// AddNewPreset endpoint level), so this gate is purely about whether the LLM is allowed to
    /// initiate the operation.</summary>
    public static readonly PermInfo PermToolCreateImagePreset = Permissions.Register(new(
        "llm_tool_create_image_preset", "[LLM Tool] Create Image Preset",
        "Allows the LLM to call the built-in create_image_preset tool, which saves a new T2I preset to the calling user's account. The user must also have SwarmUI's core 'Manage Presets' permission for the save to actually succeed.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup, PermSafetyLevel.UNTESTED));

    /// <summary>Permission to use the vision-related built-in tools (<c>caption_image</c>,
    /// <c>fuse_image_descriptions</c>, <c>batch_caption_folder</c>). These all run a vision LLM
    /// pass on user-supplied imagery; gating them as a group keeps role config simple.</summary>
    public static readonly PermInfo PermToolVision = Permissions.Register(new(
        "llm_tool_vision", "[LLM Tool] Vision",
        "Allows the LLM to call the built-in vision tools (caption_image, fuse_image_descriptions, batch_caption_folder). Each runs a vision-model pass on user-supplied images.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

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

    /// <summary>Permission to use the <c>file_write</c> built-in tool (sandboxed to SwarmUI/Output).</summary>
    public static readonly PermInfo PermToolFileWrite = Permissions.Register(new(
        "llm_tool_file_write", "[LLM Tool] Write File",
        "Allows the LLM to call the built-in file_write tool. Sandboxed to an Output subfolder managed by the extension, but still lets the LLM create/overwrite files within that sandbox.",
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

    /// <summary>Permission to use the <c>memory_read</c> / <c>memory_write</c> built-in tools.
    /// Memory is strictly per-user — a user's profile is never visible to any other user and
    /// there is no shared memory layer — so this is a low-risk permission, but it's still gated
    /// so admins can disable memory features instance-wide if desired.</summary>
    public static readonly PermInfo PermToolMemory = Permissions.Register(new(
        "llm_tool_memory", "[LLM Tool] Memory",
        "Allows the LLM to read and write the calling user's personal memory profile (preferred name, preferences, current work, and other facts worth remembering across conversations). Memory is strictly per-user — a user's profile is never visible to any other user, and there is no shared memory layer.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission for the <c>swarm_docs</c> built-in tool — lets the LLM list and read
    /// SwarmUI's bundled documentation (the markdown files under the install's <c>docs/</c>
    /// folder). Sandboxed strictly to that directory; no access to user data or arbitrary disk
    /// paths. Safe to grant broadly — docs are public reference material.</summary>
    public static readonly PermInfo PermToolSwarmDocs = Permissions.Register(new(
        "llm_tool_swarm_docs", "[LLM Tool] SwarmUI Docs",
        "Allows the LLM to list and read SwarmUI's bundled documentation files (docs/*.md). Sandboxed strictly to the docs folder — cannot access user data or other files. Used by Swarmie and other helper assistants to give exact, doc-grounded how-to answers.",
        PermissionDefault.USER, LLMAssistantPermGroup));

    /// <summary>Permission to use the floating Companion overlay (Clippy-style helper). Gates
    /// both the overlay itself and the <c>LLMAssistantGetCompanionContext</c> endpoint that
    /// surfaces the user's most recent generated image to the companion. Default POWERUSERS so
    /// it stays opt-in; admins can grant or deny instance-wide.</summary>
    public static readonly PermInfo PermCompanion = Permissions.Register(new(
        "llm_companion", "LLM Companion Overlay",
        "Allows using the floating Companion overlay — a small in-page helper that can critique your last image, suggest prompt fixes, and answer quick questions through the LLM Assistant.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    public static void Register()
    {
        // Chat
        API.RegisterAPICall(ChatEndpoints.LLMAssistantSendMessage, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantSendMessageWS, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantCreateThread, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantTestInstruction, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantUploadChatImage, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantCountTokens, false, PermChat);
        // Settings
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantGetSettings, false, PermSettings);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantSaveSettings, true, PermSettings);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantResetSettings, true, PermSettings);
        // Audit log — admin-only. The endpoint itself re-checks PermSharedWrite for defense-in-depth.
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantGetAuditLog, false, PermSharedWrite);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantSetAuditLogEnabled, true, PermSharedWrite);
        // Models
        API.RegisterAPICall(ModelEndpoints.LLMAssistantGetModels, false, PermModels);
        // Threads
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantGetThreads, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantGetThread, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantDeleteThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantRenameThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantDeleteMessage, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantEditMessage, true, PermThreads);
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
        API.RegisterAPICall(AssistantEndpoints.LLMAssistantUploadAssistantAvatar, true, PermSettings);
        API.RegisterAPICall(AssistantEndpoints.LLMAssistantGetStarterTemplates, false, PermSettings);
        // Tools
        API.RegisterAPICall(ToolEndpoints.LLMAssistantGetTools, false, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantGetTool, false, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantSaveTool, true, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantDeleteTool, true, PermSettings);
        // Chat-permission so the in-chat tool picker can run a tool directly. The per-tool gate
        // (llm_tool_*) is still enforced inside ExecuteTool, so this isn't a privilege escalation.
        API.RegisterAPICall(ToolEndpoints.LLMAssistantExecuteTool, true, PermChat);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantGetToolConfig, false, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantSetToolConfig, true, PermSettings);
        API.RegisterAPICall(ToolEndpoints.LLMAssistantGetImagePresets, false, PermSettings);
        // Companion overlay
        API.RegisterAPICall(CompanionEndpoints.LLMAssistantGetCompanionContext, false, PermCompanion);
        // User memory / profile (strictly per-user, gated behind PermChat since it's personal
        // conversation state rather than extension configuration)
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantGetUserProfile, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantClearUserProfile, true, PermChat);
    }
}
