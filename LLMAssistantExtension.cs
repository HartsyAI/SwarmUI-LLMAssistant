using System.IO;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Extensions.LLMAssistant.T2I;
using SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;
using SwarmUI.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant;

/// <summary>LLM Assistant extension for SwarmUI. Provides chat UI, threads, instructions, and T2I integration
/// using SwarmUI's native LLM model registry and backends.</summary>
public class LLMAssistantExtension : Extension
{
    /// <summary>Current version of the LLM Assistant extension.</summary>
    public static new readonly string Version = "1.0.0";

    public override void OnPreInit()
    {
        Logs.Info($"[LLMAssistant] Version {Version} loading...");
        // Extension JS (CDN libs loaded dynamically by utils.js)
        ScriptFiles.Add("Assets/utils.js");
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
        // Static asset files (images) — served at ExtensionFile/LLMAssistantExtension/Assets/<name>.
        // The SwarmUI logo doubles as Swarmie's avatar on the welcome hero and assistant cards.
        OtherAssets.Add("Assets/swarmui-logo.jpg");
        // Vendored front-end libraries (marked / highlight.js / DOMPurify / KaTeX + fonts / Mermaid).
        // Shipped locally so the extension has ZERO runtime external dependencies — no CDN, no internet.
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
            Logs.Error("[LLMAssistant] Assets/vendor is missing — markdown/math/diagram rendering will be unavailable. Run the vendoring step (see Assets/vendor/FETCH.md).");
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
            // Extension-relative, forward-slashed (URL form) — eg "Assets/vendor/marked/marked.min.js".
            OtherAssets.Add(Path.GetRelativePath(FilePath, file).Replace('\\', '/'));
            count++;
        }
        Logs.Info($"[LLMAssistant] Registered {count} vendored library asset(s).");
    }

    public override void OnInit()
    {
        RegisterLLMModelType();
        // Removable backend pack — supplies the actual LLM providers behind ILLMProvider.
        // Delete this line + the Backends/ folder to fall back to a native Swarm LLM API.
        Backends.LLMBackendPack.Register();
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
    }

    /// <summary>Registers the "LLM" model type in SwarmUI's model registry so LLM models
    /// are discoverable just like image models (Stable-Diffusion, LoRA, etc.).</summary>
    private static void RegisterLLMModelType()
    {
        if (Program.T2IModelSets.ContainsKey("LLM"))
        {
            return;
        }
        T2IModelHandler handler = new() { ModelType = "LLM" };
        List<string> paths = [];
        foreach (string root in Program.ServerSettings.Paths.ActualModelRoots)
        {
            string llmPath = Path.Combine(root, "llm");
            Directory.CreateDirectory(llmPath);
            paths.Add(llmPath);
        }
        if (paths.Count > 0)
        {
            handler.FolderPaths = [.. paths];
            handler.DownloadFolderPath = paths[0];
        }
        Program.T2IModelSets["LLM"] = handler;
        Logs.Info($"[LLMAssistant] Registered LLM model type with {paths.Count} folder(s).");
    }
}
