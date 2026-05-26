using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.LLMs;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Routes LLM requests to running SwarmUI backends.
/// When the request specifies a model, the dispatcher queries each backend's
/// <see cref="AbstractLLMBackend.ListModels"/> to find which one owns it.
/// Falls back to the first running backend if no model is specified or no match is found.</summary>
public static class LLMDispatcher
{
    /// <summary>Gets the best backend for the given input. If a model is specified, finds the
    /// backend that advertises that model. Otherwise returns the first running LLM backend.</summary>
    public static async Task<AbstractLLMBackend> GetBackend(ExtendedLLMInput input = null)
    {
        List<AbstractLLMBackend> backends = Program.Backends.RunningBackendsOfType<AbstractLLMBackend>().ToList();
        if (backends.Count == 0)
        {
            throw new SwarmReadableErrorException("No LLM backend is running. Add one in Server > Backends.");
        }
        if (backends.Count == 1 || string.IsNullOrEmpty(input?.Model))
        {
            return backends[0];
        }
        // Multiple backends running and a model is specified — find the owner
        foreach (AbstractLLMBackend backend in backends)
        {
            try
            {
                List<LLMModelInfo> models = await backend.ListModels();
                if (models.Any(m => m.Id == input.Model))
                {
                    return backend;
                }
            }
            catch
            {
                // Skip backends that fail to list models
            }
        }
        // No backend claims this model — fall back to first (the backend's DefaultModel or
        // its own logic will handle the unknown model name gracefully)
        return backends[0];
    }

    /// <summary>Sends a message and returns the full response.</summary>
    public static async Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default)
    {
        AbstractLLMBackend backend = await GetBackend(input);
        return await backend.Generate(input);
    }

    /// <summary>Sends a message with streaming chunks via callback. <paramref name="ct"/> is
    /// forwarded to the backend so HTTP requests and async-enumerable streams can be cancelled
    /// when the user hits Stop or the WebSocket closes.</summary>
    public static async Task GenerateStreaming(ExtendedLLMInput input, Action<JObject> onChunk, CancellationToken ct = default)
    {
        AbstractLLMBackend backend = await GetBackend(input);
        await backend.GenerateLive(input, "0", onChunk, ct);
    }
}
