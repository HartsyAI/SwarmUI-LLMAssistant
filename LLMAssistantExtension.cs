using System.IO;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Mcp;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.T2I;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant;

/// <summary>LLM Assistant extension for SwarmUI. Provides chat UI, threads, instructions, and T2I integration
/// using SwarmUI's native LLM model registry and backends.</summary>
public class LLMAssistantExtension : Extension
{
    /// <summary>For startup logging only; must not shadow the base `Version` field core populates from git.</summary>
    public const string ExtensionVersion = "2.0.0-alpha.2";

    public LLMAssistantExtension()
    {
        // Shown in the Extensions tab until the repo is on the official extension list.
        ExtensionAuthor = "Hartsy";
        Description = "Full LLM workspace: persistent chats, custom assistants, agentic tool calling, vision, model comparison, a floating companion, and <llmprompt> Generate-tab integration.";
        License = "MIT";
        ReadmeURL = "https://github.com/HartsyAI/SwarmUI-LLMAssistant";
        Tags = ["tabs", "hartsy", "llm"];
    }

    public override void OnPreInit()
    {
        Logs.Info($"[LLMAssistant] Version {ExtensionVersion} loading...");
        // Extension JS (vendored libs under Assets/vendor are lazy-loaded by utils.js; no CDN).
        ScriptFiles.Add("Assets/utils.js");
        // The one place the front end touches core through a non-documented route (see the file header).
        ScriptFiles.Add("Assets/core-bridge.js");
        // Settings and parameter forms, built from core's make*Input helpers and input groups.
        ScriptFiles.Add("Assets/settings-forms.js");
        // The Hooks tab of the settings dialog (list, editor and tester).
        ScriptFiles.Add("Assets/hooks.js");
        // Code mode: the Chat / Code switch, the workspace picker and panel, the plan card.
        ScriptFiles.Add("Assets/code-mode.js");
        // The MCP Servers tab of the settings dialog.
        ScriptFiles.Add("Assets/mcp.js");
        // Skills and commands: the list the "/" picker reads (before tool-picker.js) and the settings tab.
        ScriptFiles.Add("Assets/prompt-assets.js");
        ScriptFiles.Add("Assets/skills.js");
        // The context bar, the marker where a long chat was summarized, and /compact.
        ScriptFiles.Add("Assets/context.js");
        ScriptFiles.Add("Assets/assets.js");
        // chat.js was split into focused modules (each an IIFE; public fns exposed on window).
        ScriptFiles.Add("Assets/chat/chat-render.js");
        ScriptFiles.Add("Assets/chat/chat-toolcalls.js");
        ScriptFiles.Add("Assets/chat/chat-findbar.js");
        ScriptFiles.Add("Assets/chat/chat-attachments.js");
        ScriptFiles.Add("Assets/chat/chat-composer.js");
        ScriptFiles.Add("Assets/chat/chat-pipeline.js");
        ScriptFiles.Add("Assets/chat/chat-compare.js");
        ScriptFiles.Add("Assets/threads.js");
        ScriptFiles.Add("Assets/tools.js");
        ScriptFiles.Add("Assets/tool-picker.js");
        ScriptFiles.Add("Assets/llmassistant.js");
        // Companion overlay (loaded last so its boot can read LLMAState helpers defined above).
        ScriptFiles.Add("Assets/companion.js");
        // Sections, plain labels, guide links and filled dropdowns for the LLM backend cards under Server > Backends.
        ScriptFiles.Add("Assets/llm-backend-cards.js");
        // CSS
        StyleSheetFiles.Add("Assets/llma-layout.css");
        StyleSheetFiles.Add("Assets/llma-topbar.css");
        StyleSheetFiles.Add("Assets/llma-welcome.css");
        StyleSheetFiles.Add("Assets/llma-chat.css");
        StyleSheetFiles.Add("Assets/llma-panel.css");
        StyleSheetFiles.Add("Assets/llma-settings.css");
        StyleSheetFiles.Add("Assets/llma-common.css");
        StyleSheetFiles.Add("Assets/llma-tools.css");
        StyleSheetFiles.Add("Assets/llma-assets.css");
        StyleSheetFiles.Add("Assets/llma-companion.css");
        StyleSheetFiles.Add("Assets/llma-tool-picker.css");
        StyleSheetFiles.Add("Assets/llma-backend-cards.css");
        StyleSheetFiles.Add("Assets/llma-code.css");
        // Static asset files (images), served at ExtensionFile/LLMAssistantExtension/Assets/<name>.
        // The SwarmUI logo doubles as Swarmie's avatar on the welcome hero and assistant cards.
        OtherAssets.Add("Assets/swarmui-logo.jpg");
        // Vendored front-end libraries (marked / highlight.js / DOMPurify / KaTeX + fonts / Mermaid).
        // Shipped locally so the extension has ZERO runtime external dependencies: no CDN, no internet.
        // Loaded on demand by Assets/utils.js. See Assets/vendor/FETCH.md.
        RegisterVendorAssets();
    }

    /// <summary>Registers every shippable file under <c>Assets/vendor/</c> (js/css/woff2) as a served
    /// asset, by enumeration, so the bundled libraries resolve from
    /// <c>/ExtensionFile/LLMAssistantExtension/Assets/vendor/...</c> with no CDN dependency. Auto-walking
    /// the folder means adding/removing a vendored file (or bumping a version) needs no code change.</summary>
    private void RegisterVendorAssets()
    {
        string vendorAbs = Path.Combine(FilePath, "Assets", "vendor");
        if (!Directory.Exists(vendorAbs))
        {
            Logs.Error("[LLMAssistant] Assets/vendor is missing, so markdown/math/diagram rendering will be unavailable. Run the vendoring step (see Assets/vendor/FETCH.md).");
            return;
        }
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(vendorAbs, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".js" && ext != ".css" && ext != ".woff2")
            {
                continue;
            }
            // Extension-relative, forward-slashed (URL form), eg "Assets/vendor/marked/marked.min.js".
            OtherAssets.Add(Path.GetRelativePath(FilePath, file).Replace('\\', '/'));
            count++;
        }
        Logs.Info($"[LLMAssistant] Registered {count} vendored library asset(s).");
    }

    public override void OnInit()
    {
        // Core has no model-type registration call and clears its sets on any model-path save; CoreBridge keeps the type registered.
        CoreBridge.RegisterModelType("LLM", "llm");
        // Removable backend pack: supplies the actual LLM providers behind ILLMProvider.
        // Delete this line + the Backends/ folder to fall back to a native Swarm LLM API.
        Backends.LLMBackendPack.Register();
        // Skills and commands that ship with the extension (none yet; the folder is looked at if it appears).
        PromptAssetService.BundledRoot = Path.Combine(FilePath, "Assets", "prompt-assets");
        RegisterBuiltInTools();
        LLMAssistantAPI.Register();
        PromptTagHandler.RegisterAll();
        MigrationService.RunIfNeeded();
        OrphanedFileGC.Start();
        Logs.Info("[LLMAssistant] Initialized.");
    }

    /// <summary>Registers all built-in tool handlers with the ToolRegistryService.</summary>
    private static void RegisterBuiltInTools()
    {
        ToolRegistryService.RegisterHandler(new GenerateImageTool());
        ToolRegistryService.RegisterHandler(new CreateImagePresetTool());
        ToolRegistryService.RegisterHandler(new CaptionImageTool());
        ToolRegistryService.RegisterHandler(new FuseImageDescriptionsTool());
        ToolRegistryService.RegisterHandler(new BatchCaptionFolderTool());
        ToolRegistryService.RegisterHandler(new WebSearchTool());
        ToolRegistryService.RegisterHandler(new FileReadTool());
        ToolRegistryService.RegisterHandler(new FileWriteTool());
        ToolRegistryService.RegisterHandler(new HttpRequestTool());
        ToolRegistryService.RegisterHandler(new ShellExecTool());
        ToolRegistryService.RegisterHandler(new MemoryWriteTool());
        ToolRegistryService.RegisterHandler(new MemoryReadTool());
        ToolRegistryService.RegisterHandler(new SwarmDocsTool());
        // The code tools: off by default, behind llm_code_mode, and working only inside a chat's workspace.
        ToolRegistryService.RegisterHandler(new CodeReadTool());
        ToolRegistryService.RegisterHandler(new CodeWriteTool());
        ToolRegistryService.RegisterHandler(new CodeEditTool());
        ToolRegistryService.RegisterHandler(new CodeGlobTool());
        ToolRegistryService.RegisterHandler(new CodeGrepTool());
        ToolRegistryService.RegisterHandler(new CodeBashTool());
        ToolRegistryService.RegisterHandler(new CodeTodoTool());
        // Skills: the tool that loads one. A chat is given it when the user has a skill the model may load.
        ToolRegistryService.RegisterHandler(new SkillTool());
        // MCP: one handler for every tool an MCP server offers, and the source that lists them. The servers connect in the background, so a slow
        // or broken one never holds up start-up; its tools appear once it is ready.
        ToolRegistryService.RegisterHandler(new McpToolHandler());
        ToolSources.Register(new McpToolSource(McpServerManager.Instance));
        _ = Task.Run(async () =>
        {
            try
            {
                await McpServerManager.Instance.SyncAsync();
            }
            catch (Exception ex)
            {
                Logs.Warning($"[LLMAssistant] Starting MCP servers failed: {ex.Message}");
            }
        });
        // One handler instance per device action; they share an implementation and differ only by id.
        foreach (string deviceTool in DeviceActionTool.Ids)
        {
            ToolRegistryService.RegisterHandler(new DeviceActionTool(deviceTool));
        }
    }

    public override void OnShutdown()
    {
        McpServerManager.Instance.DisposeAll();
        CoreBridge.Shutdown();
    }
}
