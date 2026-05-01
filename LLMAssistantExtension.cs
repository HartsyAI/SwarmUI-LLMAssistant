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
    public static new readonly string Version = "1.0.0";

    public override void OnPreInit()
    {
        Logs.Info($"[LLMAssistant] Version {Version} loading...");
        // Extension JS (CDN libs loaded dynamically by utils.js)
        ScriptFiles.Add("Assets/utils.js");
        ScriptFiles.Add("Assets/assets.js");
        ScriptFiles.Add("Assets/chat.js");
        ScriptFiles.Add("Assets/threads.js");
        ScriptFiles.Add("Assets/tools.js");
        ScriptFiles.Add("Assets/llmassistant.js");
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
        // Static asset files (images) — served at ExtensionFile/LLMAssistantExtension/Assets/<name>.
        // The SwarmUI logo doubles as Swarmie's avatar on the welcome hero and assistant cards.
        OtherAssets.Add("Assets/swarmui-logo.jpg");
    }

    public override void OnInit()
    {
        RegisterLLMModelType();
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
