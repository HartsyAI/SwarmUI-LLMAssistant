using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Model listing and backend info endpoints.</summary>
public static class ModelEndpoints
{
    /// <summary>Returns info about running LLM backends.</summary>
    public static async Task<JObject> LLMAssistantGetBackends(Session session)
    {
        return new JObject
        {
            ["success"] = true,
            ["backends"] = LLMDispatcher.GetBackendInfo()
        };
    }

    /// <summary>Queries running backends for available models.</summary>
    public static async Task<JObject> LLMAssistantGetModels(Session session)
    {
        // For now, return the backend info. Model listing depends on backend type
        // and SwarmUI's native backends don't expose a model list API yet.
        // LlamaSharp loads models from file paths; SimpleRemoteLLM doesn't have ListModels.
        // Future: when Swarm adds model registry for LLMs, query it here.
        JArray backends = LLMDispatcher.GetBackendInfo();
        return new JObject
        {
            ["success"] = true,
            ["backends"] = backends,
            ["models"] = new JArray() // Placeholder until Swarm has LLM model registry
        };
    }
}
