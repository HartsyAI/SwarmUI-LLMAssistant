using Microsoft.AspNetCore.Html;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>The single registration entry point for the removable LLM backend pack.
/// <para><b>To remove the bundled backends</b> when SwarmUI ships a native LLM API: delete this
/// <c>Backends/</c> folder, remove the <c>LLMBackendPack.Register()</c> call in
/// <c>LLMAssistantExtension.OnInit</c>, and drop the HartsyInference NuGet reference from the
/// csproj. Then register a thin <c>SwarmNativeLLMProvider : ILLMProvider</c> instead. Nothing else
/// in the extension changes, since it only talks to <c>ILLMProvider</c>.</para></summary>
public static class LLMBackendPack
{
    /// <summary>Whether <see cref="Register"/> has already run, so repeat calls are a no-op.</summary>
    private static bool Registered = false;

    /// <summary>Registers the pack's backend types and per-user API key types. Idempotent.</summary>
    public static void Register()
    {
        if (Registered)
        {
            return;
        }
        Registered = true;
        RegisterApiKey(new("anthropic_api", "anthropic", "Anthropic", "https://console.anthropic.com/",
            new("To use the Anthropic Claude LLM backend, set your API key below.<br>This allows you to use Claude models for text generation.")));
        RegisterApiKey(new("openai_api", "openai", "OpenAI", "https://platform.openai.com/api-keys",
            new("To use OpenAI (or other OpenAI-compatible) models via the Remote LLM backend, set your API key below.<br>This allows you to use GPT models for text generation.")));
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add("anthropic_api");
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add("openai_api");
        // isStandard: true, so these show in the Server > Backends type row without "Show Advanced" and
        // without the "advanced users only" confirm. Type IDS must never change (Backends.fds persists
        // instances by id); display names/descriptions are free to change.
        Program.Backends.RegisterBackendType<AnthropicLLMProvider>("llmassistant-anthropic", "LLM: Anthropic Claude",
            "(LLM Assistant) Claude models through the Anthropic API. Each user adds their own API key in the User tab.", true, isStandard: true);
        Program.Backends.RegisterBackendType<RemoteOpenAILLMProvider>("llmassistant-openai", "LLM: Remote (OpenAI-Compatible)",
            "(LLM Assistant) Any OpenAI-compatible server, such as OpenAI, Ollama, LM Studio, vLLM or OpenRouter. Set Address to the server. A user's own OpenAI key from the User tab is used when they have one.", true, isStandard: true);
        Program.Backends.RegisterBackendType<HartsyLocalLLMProvider>("llmassistant-hartsy-local", "LLM: Local (HartsyInference GGUF)",
            "(LLM Assistant) Runs GGUF language models on this machine with the built-in HartsyInference engine. Nothing else to install. Put .gguf files in Models/llm.", true, isStandard: true);
        BackendCardAPI.Register();
        Logs.Info("[LLMAssistant] Registered LLM backend pack (Anthropic, Remote OpenAI, HartsyInference local).");
    }

    /// <summary>Registers an API key type, tolerating the case where core (or a reload) already has it.</summary>
    private static void RegisterApiKey(UserUpstreamApiKeys.ApiKeyInfo info)
    {
        if (!UserUpstreamApiKeys.KeysByType.ContainsKey(info.KeyType))
        {
            UserUpstreamApiKeys.Register(info);
        }
    }
}
