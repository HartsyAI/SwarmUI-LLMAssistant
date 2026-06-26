using Microsoft.AspNetCore.Html;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.Backends;

/// <summary>The single registration entry point for the removable LLM backend pack.
/// <para><b>To remove the bundled backends</b> when SwarmUI ships a native LLM API: delete this
/// <c>Backends/</c> folder, remove the <c>LLMBackendPack.Register()</c> call in
/// <c>LLMAssistantExtension.OnInit</c>, and drop the HartsyInference NuGet reference from the
/// csproj. Then register a thin <c>SwarmNativeLLMProvider : ILLMProvider</c> instead. Nothing else
/// in the extension changes — it only talks to <c>ILLMProvider</c>.</para></summary>
public static class LLMBackendPack
{
    private static bool _registered = false;

    /// <summary>Registers the pack's backend types and per-user API key types. Idempotent.</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;
        // Per-user API key types used by the remote backends (surfaced in the User tab).
        RegisterApiKey(new("anthropic_api", "anthropic", "Anthropic",
            "https://console.anthropic.com/",
            new("To use the Anthropic Claude LLM backend, set your API key below.<br>This allows you to use Claude models for text generation.")));
        RegisterApiKey(new("openai_api", "openai", "OpenAI",
            "https://platform.openai.com/api-keys",
            new("To use OpenAI (or other OpenAI-compatible) models via the Remote LLM backend, set your API key below.<br>This allows you to use GPT models for text generation.")));
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add("anthropic_api");
        BasicAPIFeatures.AcceptedAPIKeyTypes.Add("openai_api");
        // Backend types — appear under Server > Backends with config UI + lifecycle. Each Init()
        // self-registers its instance into LLMProviderRegistry (see LLMProviderBackend).
        Program.Backends.RegisterBackendType<AnthropicLLMProvider>(
            "llmassistant-anthropic", "Anthropic Claude (LLM)",
            "(LLM Assistant) Anthropic Messages API — Claude models, via your per-user Anthropic key.", true);
        Program.Backends.RegisterBackendType<RemoteOpenAILLMProvider>(
            "llmassistant-openai", "Remote LLM — OpenAI API (LLM)",
            "(LLM Assistant) Any OpenAI-compatible endpoint (OpenAI, Ollama, LM Studio, vLLM, OpenRouter, …).", true);
        Program.Backends.RegisterBackendType<HartsyLocalLLMProvider>(
            "llmassistant-hartsy-local", "Local LLM — HartsyInference (LLM)",
            "(LLM Assistant) Fully-native pure-C# local GGUF inference (Qwen2/Qwen3/Llama) — no llama.cpp, no external process.", true);
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
