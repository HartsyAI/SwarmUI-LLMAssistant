using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Routes LLM requests to running SwarmUI backends.
/// The extension does not manage backends — it only selects models.
/// Backend routing is handled by SwarmUI's native infrastructure.</summary>
public static class LLMDispatcher
{
    /// <summary>Gets the first available running LLM backend, or throws if none found.</summary>
    public static AbstractLLMBackend GetBackend()
    {
        AbstractLLMBackend backend = Program.Backends.RunningBackendsOfType<AbstractLLMBackend>().FirstOrDefault();
        if (backend is null)
        {
            throw new SwarmReadableErrorException("No LLM backend is running. Add one in Server > Backends.");
        }
        return backend;
    }

    /// <summary>Sends a message and returns the full response.</summary>
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
}
