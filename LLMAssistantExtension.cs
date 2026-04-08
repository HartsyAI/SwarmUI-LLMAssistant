using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Extensions.LLMAssistant.T2I;
using SwarmUI.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant;

/// <summary>LLM Assistant extension for SwarmUI. Provides chat UI, threads, instructions, and T2I integration
/// on top of SwarmUI's native LLM backends (LlamaSharp, SimpleRemoteLLM).</summary>
public class LLMAssistantExtension : Extension
{
    public static new readonly string Version = "1.0.0";

    public override void OnPreInit()
    {
        Logs.Info($"[LLMAssistant] Version {Version} loading...");
        // JS libraries
        ScriptFiles.Add("Assets/lib/marked.min.js");
        ScriptFiles.Add("Assets/lib/highlight.min.js");
        ScriptFiles.Add("Assets/lib/purify.min.js");
        ScriptFiles.Add("Assets/lib/katex.min.js");
        ScriptFiles.Add("Assets/lib/auto-render.min.js");
        ScriptFiles.Add("Assets/lib/mermaid.min.js");
        // Extension JS
        ScriptFiles.Add("Assets/llm-core.js");
        ScriptFiles.Add("Assets/llm-markdown.js");
        ScriptFiles.Add("Assets/llm-chat.js");
        ScriptFiles.Add("Assets/llm-vision.js");
        ScriptFiles.Add("Assets/llm-settings.js");
        ScriptFiles.Add("Assets/llm-prompt-buttons.js");
        ScriptFiles.Add("Assets/llm-threads.js");
        // CSS
        StyleSheetFiles.Add("Assets/lib/github-dark.min.css");
        StyleSheetFiles.Add("Assets/lib/katex.min.css");
        StyleSheetFiles.Add("Assets/llm-core.css");
        StyleSheetFiles.Add("Assets/llm-chat.css");
        StyleSheetFiles.Add("Assets/llm-vision.css");
        StyleSheetFiles.Add("Assets/llm-settings.css");
        StyleSheetFiles.Add("Assets/llm-threads.css");
    }

    public override void OnInit()
    {
        // Register API endpoints and permissions
        LLMAssistantAPI.Register();
        // Register T2I parameters and prompt tag handlers
        PromptTagHandler.RegisterAll();
        // Migrate from MagicPrompt if settings exist
        MigrationService.RunIfNeeded();
        Logs.Info($"[LLMAssistant] Initialized. No backends registered — uses Swarm's native LLM backends.");
    }
}
