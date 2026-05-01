using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.LLMs;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Model listing endpoints for LLM models.
/// Delegates to the core <see cref="AbstractLLMBackend.ListModels"/> so all
/// running LLM backends (Ollama, Anthropic, LlamaSharp, etc.) are included.</summary>
public static class ModelEndpoints
{
    /// <summary>Returns available LLM models from all running LLM backends.</summary>
    public static async Task<JObject> LLMAssistantGetModels(Session session)
    {
        List<AbstractLLMBackend> backends = Program.Backends.RunningBackendsOfType<AbstractLLMBackend>().ToList();
        List<Task<List<LLMModelInfo>>> tasks = [];
        foreach (AbstractLLMBackend backend in backends)
        {
            tasks.Add(backend.ListModels());
        }
        List<LLMModelInfo>[] results = await Task.WhenAll(tasks);
        JArray models = [];
        foreach (List<LLMModelInfo> backendModels in results)
        {
            foreach (LLMModelInfo model in backendModels)
            {
                JObject entry = LLMAPI.ModelInfoToJson(model);
                // Extension compat: add "title" as alias for "name" for the UI dropdown
                entry["title"] = entry["name"];
                models.Add(entry);
            }
        }
        return new JObject
        {
            ["success"] = true,
            ["models"] = models
        };
    }
}
