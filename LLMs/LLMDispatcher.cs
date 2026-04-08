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

    /// <summary>Gets a specific running LLM backend by its ID, falling back to the first available.</summary>
    public static AbstractLLMBackend GetBackendById(int backendId)
    {
        if (backendId >= 0)
        {
            AbstractLLMBackend match = GetRunningBackends()
                .FirstOrDefault(b => b.AbstractBackendData.ID == backendId);
            if (match is not null)
            {
                return match;
            }
        }
        return GetBackend();
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
    public static async Task<string> Generate(ExtendedLLMInput input, int backendId = -1, CancellationToken ct = default)
    {
        AbstractLLMBackend backend = backendId >= 0 ? GetBackendById(backendId) : GetBackend();
        return await backend.Generate(input);
    }

    /// <summary>Sends a message with streaming chunks via callback.</summary>
    public static async Task GenerateStreaming(ExtendedLLMInput input, Action<JObject> onChunk, int backendId = -1, CancellationToken ct = default)
    {
        AbstractLLMBackend backend = backendId >= 0 ? GetBackendById(backendId) : GetBackend();
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
