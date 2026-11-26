using SwarmUI.Accounts;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Registers all LLM Assistant API endpoints and permissions.</summary>
public static class LLMAssistantAPI
{
    /// <summary>Permission group under which all LLM Assistant permissions are registered.</summary>
    public static readonly PermInfoGroup LLMAssistantPermGroup = new("LLMAssistant",
        "Permissions for the LLM Assistant extension.");

    /// <summary>Permission to send chat messages to LLM backends.</summary>
    public static readonly PermInfo PermChat = Permissions.Register(new(
        "llm_chat", "LLM Chat",
        "Allows sending messages to LLM backends.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to read and modify LLM Assistant settings.</summary>
    public static readonly PermInfo PermSettings = Permissions.Register(new(
        "llm_settings", "LLM Settings",
        "Allows reading and modifying LLM Assistant settings.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to list available LLM models.</summary>
    public static readonly PermInfo PermModels = Permissions.Register(new(
        "llm_models", "LLM Models",
        "Allows listing available LLM models.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to create and manage chat threads.</summary>
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

    /// <summary>Permission to create a <b>command</b> hook: a policy that runs a program on the server around tool calls. A rule hook (plain data) needs
    /// no special permission. Defaults to NOBODY, like <c>shell_exec</c>, since it is the same power.</summary>
    public static readonly PermInfo PermHookCommand = Permissions.Register(new(
        "llm_hook_command", "LLM Hook Command",
        "Allows creating hooks that run a program on the SwarmUI host before or after the LLM's tool calls. This is equivalent to shell access for whoever writes the hook. Only grant to fully trusted admin users.",
        PermissionDefault.NOBODY, LLMAssistantPermGroup, PermSafetyLevel.POWERFUL));

    /// <summary>Permission to add, change and remove MCP servers. A stdio server is a program the host starts, so this is the same power as shell
    /// access for whoever holds it; an http server makes the host connect to a URL of their choosing.</summary>
    public static readonly PermInfo PermMcpManage = Permissions.Register(new(
        "llm_mcp_manage", "LLM MCP Manage",
        "Allows adding, editing and removing MCP servers. A stdio server is a program started on the SwarmUI host with the environment variables the admin sets, which is equivalent to shell access; an http server makes the host connect to the URL given. Only grant to fully trusted admin users.",
        PermissionDefault.ADMINS, LLMAssistantPermGroup, PermSafetyLevel.POWERFUL));

    /// <summary>Permission to use code mode: the tools that read, change and run things in a chat's workspace (<c>code_read</c>, <c>code_edit</c>,
    /// <c>code_bash</c> and the rest). <c>code_bash</c> runs commands as the SwarmUI user, so this is the same power as shell access. Defaults to NOBODY.</summary>
    public static readonly PermInfo PermCodeMode = Permissions.Register(new(
        "llm_code_mode", "LLM Code Mode",
        "Allows the LLM's code tools: reading, searching, writing and editing files in a chat's workspace, keeping a task list, and running shell commands (code_bash), which is equivalent to shell access as the SwarmUI user. A tool still has to be enabled and given to an assistant. Only grant to fully trusted users.",
        PermissionDefault.NOBODY, LLMAssistantPermGroup, PermSafetyLevel.POWERFUL));

    /// <summary>Permission to allow folders elsewhere on the host as workspaces, which every user with code mode can then work in.</summary>
    public static readonly PermInfo PermWorkspaceRoots = Permissions.Register(new(
        "llm_workspace_roots", "LLM Workspace Roots",
        "Allows choosing folders on the SwarmUI host (a project checkout, for example) that users with code mode can use as workspaces. Everything in an allowed folder can be read, and changed if the user can use the code tools that change files. Only grant to fully trusted admin users.",
        PermissionDefault.ADMINS, LLMAssistantPermGroup, PermSafetyLevel.POWERFUL));

    /// <summary>Permission to call the tools of the MCP servers an admin has set up. What they can do depends on the server; Ask mode still asks before
    /// any that is not read-only.</summary>
    public static readonly PermInfo PermToolMcp = Permissions.Register(new(
        "llm_tool_mcp", "[LLM Tool] MCP Servers",
        "Allows the LLM to call the tools offered by the MCP servers an admin has set up. What those tools can do depends on the server. Each assistant still has to have a tool switched on before it can use it.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to let the LLM load skills (the <c>skill</c> tool). A skill is only text an admin or the user wrote, so this is low risk; it is a permission so
    /// an admin can switch skills off for a role.</summary>
    public static readonly PermInfo PermToolSkills = Permissions.Register(new(
        "llm_tool_skills", "[LLM Tool] Skills",
        "Allows the LLM to load the skills (written instructions for particular tasks) that the user, an admin, or a trusted workspace provides, by calling the skill tool.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to let the LLM start and direct sessions of its own (the <c>session_*</c> tools). Each session runs in the background with the user's tools, so this is
    /// a permission an admin grants deliberately; a session can never use more than the chat that started it.</summary>
    public static readonly PermInfo PermToolSessions = Permissions.Register(new(
        "llm_tool_sessions", "[LLM Tool] Sessions",
        "Allows the LLM to start sessions (chats of their own that run in the background), send them messages, read their reports, and stop or archive them, by calling the session tools. A session can use only the tools, and be no less careful about approvals, than the chat that started it.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to have schedules: prompts that run on a timetable as sessions, with no one watching. It is for the Schedules tab and its routes; the assistant's own
    /// <c>schedule_*</c> tools take <see cref="PermToolSchedules"/> as well.</summary>
    public static readonly PermInfo PermSchedules = Permissions.Register(new(
        "llm_schedules", "[LLM] Schedules",
        "Allows the user to make schedules: prompts that run later, on a timetable, in the background as sessions, with the user's tools and no one watching. Each run uses the tools saved on the schedule and its approval mode.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to let the LLM make schedules (the <c>schedule_*</c> tools). What it makes is saved switched off, and the user turns it on.</summary>
    public static readonly PermInfo PermToolSchedules = Permissions.Register(new(
        "llm_tool_schedules", "[LLM Tool] Schedules",
        "Allows the LLM to make schedules (prompts that run later in the background), list and cancel the ones it made, by calling the schedule tools. What it makes starts switched off, until the user turns it on in Settings.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to have a knowledge library: documents the user adds (kept on the server, under the user's own folder) that are searched by keywords, and by meaning when an embedding
    /// model is set (which sends the documents' text to that model's server).</summary>
    public static readonly PermInfo PermKnowledge = Permissions.Register(new(
        "llm_knowledge", "[LLM] Knowledge Library",
        "Allows the user to add documents to a personal knowledge library and search it. When the user picks an embedding model, the documents' text is sent to that model's backend to make vectors for searching by meaning.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to let the LLM search the user's knowledge library (the <c>search_knowledge</c> tool). It only reads, and only the calling user's own library.</summary>
    public static readonly PermInfo PermToolKnowledge = Permissions.Register(new(
        "llm_tool_knowledge", "[LLM Tool] Search Knowledge",
        "Allows the LLM to search the user's own knowledge library by calling the search_knowledge tool, and to read the excerpts it returns.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

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

    /// <summary>Permission to use the image catalog tools (<c>list_image_models</c>, <c>list_loras</c>, <c>list_wildcards</c>, <c>list_presets</c>). They only read, and only what core already shows the
    /// calling user (their model access, their presets); wildcard contents are not returned unless a call asks for samples.</summary>
    public static readonly PermInfo PermToolImageCatalog = Permissions.Register(new(
        "llm_tool_image_catalog", "[LLM Tool] Image Catalog",
        "Allows the LLM to list the image models, LoRAs, wildcards and presets available to the calling user (list_image_models, list_loras, list_wildcards, list_presets), so it can choose what to generate with. Read-only; each user sees only what SwarmUI already shows them.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

    /// <summary>Permission to use the <c>web_search</c> built-in tool.</summary>
    public static readonly PermInfo PermToolWebSearch = Permissions.Register(new(
        "llm_tool_web_search", "[LLM Tool] Web Search",
        "Allows the LLM to call the built-in web_search tool (DuckDuckGo scrape) to fetch results from the internet.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup, PermSafetyLevel.UNTESTED));

    /// <summary>Permission to use the <c>file_read</c> built-in tool (sandboxed to SwarmUI/Data and the file_write output sandbox).</summary>
    public static readonly PermInfo PermToolFileRead = Permissions.Register(new(
        "llm_tool_file_read", "[LLM Tool] Read File",
        "Allows the LLM to call the built-in file_read tool. Sandboxed to SwarmUI's Data directory and the calling user's file_write output sandbox, but still lets the LLM read arbitrary files within those.",
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
    /// <summary>Permission for the voice-satellite action tools (<c>set_led_profile</c>, <c>set_volume</c>,
    /// <c>mute_mic</c>). These do nothing on the server — they are returned to a calling device, which acts on
    /// them — so the risk is bounded by what that hardware can do, not by server access.</summary>
    public static readonly PermInfo PermToolDeviceAction = Permissions.Register(new(
        "llm_tool_device_action", "[LLM Tool] Device Actions",
        "Allows the LLM to control a connected voice satellite: its LED show, its speaker volume, and whether its microphone is muted. The server only relays these; the device carries them out.",
        PermissionDefault.POWERUSERS, LLMAssistantPermGroup));

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
        API.RegisterAPICall(ChatEndpoints.LLMAssistantVoiceTurn, true, PermChat);
        // Streaming variant of the voice turn above: native tool-call ids via the Tools package's ToolLoop,
        // Hartsy-local only (see the method doc for why there's no cross-provider fallback here).
        API.RegisterAPICall(ChatEndpoints.LLMAssistantVoiceTurnWS, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantSendMessageWS, true, PermChat);
        // Branching: edit-into-new-branch and regenerate-as-new-branch both stream like a normal send.
        API.RegisterAPICall(ChatEndpoints.LLMAssistantEditMessageWS, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantRegenerateWS, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantStopGeneration, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantResolveToolApproval, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantCompactThread, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantCreateThread, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantTestInstruction, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantUploadChatImage, true, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantUploadChatVideo, true, PermChat);
        API.RegisterAPICall(DocumentEndpoints.LLMAssistantUploadChatDocument, true, PermChat);
        API.RegisterAPICall(DocumentEndpoints.LLMAssistantExtractDocument, false, PermChat);
        API.RegisterAPICall(ChatEndpoints.LLMAssistantCountTokens, false, PermChat);
        // Settings
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantGetSettings, false, PermSettings);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantSaveSettings, true, PermSettings);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantResetSettings, true, PermSettings);
        // Audit log — admin-only. The endpoint itself re-checks PermSharedWrite for defense-in-depth.
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantGetAuditLog, false, PermSharedWrite);
        API.RegisterAPICall(SettingsEndpoints.LLMAssistantSetAuditLogEnabled, true, PermSharedWrite);
        // Usage and cost: the price table and budget, one chat's figures, the day's and week's totals
        API.RegisterAPICall(UsageEndpoints.LLMAssistantGetUsageSettings, false, PermSettings);
        API.RegisterAPICall(UsageEndpoints.LLMAssistantSaveUsageSettings, true, PermSettings);
        API.RegisterAPICall(UsageEndpoints.LLMAssistantGetThreadUsage, false, PermThreads);
        API.RegisterAPICall(UsageEndpoints.LLMAssistantGetUsageTotals, false, PermThreads);
        // Models
        API.RegisterAPICall(ModelEndpoints.LLMAssistantGetModels, false, PermModels);
        API.RegisterAPICall(ModelEndpoints.LLMAssistantDecide, true, PermChat);
        API.RegisterAPICall(ModelEndpoints.LLMAssistantUnloadModels, true, PermModels);
        // Gigabytes of disk and bandwidth, so admin-only like the audit log.
        API.RegisterAPICall(ModelEndpoints.LLMAssistantDownloadModel, true, PermSharedWrite);
        // Threads
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantGetThreads, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantGetThread, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantDeleteThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantRenameThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantSetActiveLeaf, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantSetThreadToolsEnabled, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantSetThreadMemory, true, PermThreads);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantListWorkspaceFiles, false, PermChat);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantReadWorkspaceFile, false, PermChat);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantListWorkspaceChanges, false, PermChat);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantGetWorkspaceChange, false, PermChat);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantRevertWorkspaceChange, true, PermChat);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantSetThreadMode, true, PermThreads);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantGetWorkspaces, false, PermChat);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantCreateWorkspace, true, PermChat);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantSetThreadWorkspace, true, PermThreads);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantGetWorkspaceRoots, false, PermSettings);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantSaveWorkspaceRoot, true, PermSettings);
        API.RegisterAPICall(WorkspaceEndpoints.LLMAssistantDeleteWorkspaceRoot, true, PermSettings);
        API.RegisterAPICall(McpEndpoints.LLMAssistantGetMcpServers, false, PermSettings);
        API.RegisterAPICall(McpEndpoints.LLMAssistantSaveMcpServer, true, PermSettings);
        API.RegisterAPICall(McpEndpoints.LLMAssistantDeleteMcpServer, true, PermSettings);
        API.RegisterAPICall(McpEndpoints.LLMAssistantTestMcpServer, true, PermSettings);
        API.RegisterAPICall(McpEndpoints.LLMAssistantReconnectMcpServer, true, PermSettings);
        API.RegisterAPICall(HookEndpoints.LLMAssistantGetHooks, false, PermSettings);
        API.RegisterAPICall(HookEndpoints.LLMAssistantSaveHook, true, PermSettings);
        API.RegisterAPICall(HookEndpoints.LLMAssistantDeleteHook, true, PermSettings);
        API.RegisterAPICall(HookEndpoints.LLMAssistantTestHook, true, PermSettings);
        API.RegisterAPICall(PromptAssetEndpoints.LLMAssistantGetPromptAssets, false, PermChat);
        API.RegisterAPICall(PromptAssetEndpoints.LLMAssistantGetPromptAsset, false, PermChat);
        API.RegisterAPICall(PromptAssetEndpoints.LLMAssistantExpandPromptAsset, false, PermChat);
        API.RegisterAPICall(PromptAssetEndpoints.LLMAssistantSavePromptAsset, true, PermSettings);
        API.RegisterAPICall(PromptAssetEndpoints.LLMAssistantDeletePromptAsset, true, PermSettings);
        API.RegisterAPICall(PromptAssetEndpoints.LLMAssistantSetWorkspaceTrust, true, PermSettings);
        API.RegisterAPICall(PromptStyleEndpoints.LLMAssistantGetPromptStyles, false, PermChat);
        API.RegisterAPICall(PromptStyleEndpoints.LLMAssistantGetPromptStyle, false, PermChat);
        API.RegisterAPICall(PromptStyleEndpoints.LLMAssistantResolvePromptStyle, false, PermChat);
        API.RegisterAPICall(PromptStyleEndpoints.LLMAssistantSavePromptStyle, true, PermSettings);
        API.RegisterAPICall(PromptStyleEndpoints.LLMAssistantDeletePromptStyle, true, PermSettings);
        API.RegisterAPICall(PromptStyleEndpoints.LLMAssistantSetStyleOverrides, true, PermSettings);
        API.RegisterAPICall(PromptRewriteEndpoints.LLMAssistantRewritePrompt, true, PermChat);
        API.RegisterAPICall(PromptRewriteEndpoints.LLMAssistantGetRewritePolicy, false, PermChat);
        API.RegisterAPICall(PromptRewriteEndpoints.LLMAssistantSetThreadAutoEnhance, true, PermThreads);
        API.RegisterAPICall(ChildSessionEndpoints.LLMAssistantArchiveSession, true, PermThreads);
        API.RegisterAPICall(HeadlessEndpoints.LLMAssistantReadiness, false, PermChat);
        API.RegisterAPICall(HeadlessEndpoints.LLMAssistantRunTurn, true, PermChat);
        // First-run guide and starter set: the caller's own state and items; the install checks llm_settings / llm_threads per item.
        API.RegisterAPICall(OnboardingEndpoints.LLMAssistantGetOnboarding, false, PermChat);
        API.RegisterAPICall(OnboardingEndpoints.LLMAssistantSetOnboarding, true, PermChat);
        API.RegisterAPICall(OnboardingEndpoints.LLMAssistantInstallStarterSet, true, PermChat);
        API.RegisterAPICall(KnowledgeEndpoints.LLMAssistantGetKnowledge, false, PermKnowledge);
        API.RegisterAPICall(KnowledgeEndpoints.LLMAssistantAddKnowledge, true, PermKnowledge);
        API.RegisterAPICall(KnowledgeEndpoints.LLMAssistantDeleteKnowledge, true, PermKnowledge);
        API.RegisterAPICall(KnowledgeEndpoints.LLMAssistantSearchKnowledge, false, PermKnowledge);
        API.RegisterAPICall(KnowledgeEndpoints.LLMAssistantSetKnowledgeSettings, true, PermKnowledge);
        API.RegisterAPICall(KnowledgeEndpoints.LLMAssistantReindexKnowledge, true, PermKnowledge);
        API.RegisterAPICall(ScheduleEndpoints.LLMAssistantGetSchedules, false, PermSchedules);
        API.RegisterAPICall(ScheduleEndpoints.LLMAssistantSaveSchedule, true, PermSchedules);
        API.RegisterAPICall(ScheduleEndpoints.LLMAssistantDeleteSchedule, true, PermSchedules);
        API.RegisterAPICall(ScheduleEndpoints.LLMAssistantRunSchedule, true, PermSchedules);
        API.RegisterAPICall(ScheduleEndpoints.LLMAssistantPreviewSchedule, false, PermSchedules);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantSetThreadApprovalMode, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantDeleteMessage, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantEditMessage, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantExportThread, false, PermThreads);
        // Chat library: full-text search, pins, import (export is above)
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantSearchThreads, false, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantPinThread, true, PermThreads);
        API.RegisterAPICall(ThreadEndpoints.LLMAssistantImportThread, true, PermThreads);
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
        // Image catalog (what the list_* tools return) and the image defaults
        API.RegisterAPICall(ImageToolEndpoints.LLMAssistantListImageModels, false, PermToolImageCatalog);
        API.RegisterAPICall(ImageToolEndpoints.LLMAssistantListLoras, false, PermToolImageCatalog);
        API.RegisterAPICall(ImageToolEndpoints.LLMAssistantListWildcards, false, PermToolImageCatalog);
        API.RegisterAPICall(ImageToolEndpoints.LLMAssistantListPresets, false, PermToolImageCatalog);
        API.RegisterAPICall(ImageToolEndpoints.LLMAssistantGetImageDefaults, false, PermSettings);
        API.RegisterAPICall(ImageToolEndpoints.LLMAssistantSetImageDefaults, true, PermSettings);
        // Companion overlay
        API.RegisterAPICall(CompanionEndpoints.LLMAssistantGetCompanionContext, false, PermCompanion);
        // User memory / profile (strictly per-user, gated behind PermChat since it's personal
        // conversation state rather than extension configuration)
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantGetUserProfile, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantClearUserProfile, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantGetMemoryChanges, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantUndoMemoryChange, true, PermChat);
        // Review and control (M3): every one works on the calling user's own notes and settings; only the admin switch is shared-layer, and it takes llm_shared_write.
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantListMemoryNotes, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantGetMemoryNote, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantAddMemoryNote, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantPinMemoryNote, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantSetAssistantMemory, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantUpdateMemoryNote, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantDeleteMemoryNotes, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantExportMemory, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantPreviewMemoryImport, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantImportMemory, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantGetMemoryControls, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantSetMemoryControls, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantSetMemoryAdmin, true, PermSharedWrite);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantGetMemorySettings, false, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantSetMemorySettings, true, PermChat);
        API.RegisterAPICall(MemoryEndpoints.LLMAssistantExtractMemory, true, PermChat);
    }
}
