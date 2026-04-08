using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Routes LLM requests to running SwarmUI backends.</summary>
public static class LLMDispatcher
{
    /// <summary>Gets all currently running LLM backends.</summary>
    public static IEnumerable<AbstractLLMBackend> GetRunningBackends()
    {
        return Program.Backends.RunningBackendsOfType<AbstractLLMBackend>();
    }

    /// <summary>Gets the first available running LLM backend, or throws if none found.</summary>
    public static AbstractLLMBackend GetBackend()
    {
        AbstractLLMBackend backend = GetRunningBackends().FirstOrDefault();
        if (backend is null)
        {
            throw new SwarmReadableErrorException("No LLM backend is running. Add one in Server > Backends.");
        }
        return backend;
    }

    /// <summary>Gets a backend filtered by a specific feature tag (e.g., "local_llm", "remote_llm").</summary>
    public static AbstractLLMBackend GetBackendWithFeature(string feature)
    {
        if (string.IsNullOrEmpty(feature))
        {
            return GetBackend();
        }
        AbstractLLMBackend backend = GetRunningBackends()
            .FirstOrDefault(b => b.SupportedFeatures.Contains(feature));
        return backend ?? GetBackend();
    }

    /// <summary>Sends a message and returns the full response (non-streaming).</summary>
    public static async Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default)
    {
        AbstractLLMBackend backend = GetBackend();
        return await backend.Generate(input);
    }

    /// <summary>Sends a message with streaming chunks via callback.</summary>
    public static async Task GenerateStreaming(ExtendedLLMInput input, Action<JObject> onChunk, CancellationToken ct = default)
    {
        AbstractLLMBackend backend = GetBackend();
        await backend.GenerateLive(input, "0", onChunk);
    }

    /// <summary>Returns info about all running LLM backends.</summary>
    public static JArray GetBackendInfo()
    {
        JArray result = [];
        foreach (AbstractLLMBackend backend in GetRunningBackends())
        {
            result.Add(new JObject
            {
                ["id"] = backend.AbstractBackendData.ID,
                ["type"] = backend.HandlerTypeData.ID,
                ["name"] = string.IsNullOrEmpty(backend.Title) ? backend.HandlerTypeData.Name : backend.Title,
                ["status"] = backend.Status.ToString(),
                ["features"] = new JArray(backend.SupportedFeatures.ToArray())
            });
        }
        return result;
    }
}
