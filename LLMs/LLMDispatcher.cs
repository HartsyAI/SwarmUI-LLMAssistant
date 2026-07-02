using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Routes LLM requests to a registered <see cref="ILLMProvider"/>.
/// <para>When the request names a model, the dispatcher asks each provider's
/// <see cref="ILLMProvider.ListModels"/> which one owns it. Otherwise it uses the first
/// registered provider. Providers are supplied by the removable <c>Backends/</c> pack — with none
/// registered, every call reports "no LLM backend running".</para></summary>
public static class LLMDispatcher
{
    /// <summary>Gets the best provider for the given input. If a model is specified, finds the
    /// provider that advertises it; otherwise returns the first registered provider.</summary>
    public static async Task<ILLMProvider> GetProvider(ExtendedLLMInput input = null)
    {
        List<ILLMProvider> providers = LLMProviderRegistry.All.ToList();
        if (providers.Count == 0)
        {
            throw new SwarmReadableErrorException("No LLM backend is running. Add one in Server > Backends.");
        }
        // A pinned backend id (compare-mode device selection) must be honored even with one provider —
        // it may not be the one that owns the model, and the caller explicitly chose it.
        int backendId = input?.BackendId ?? -1;
        if ((providers.Count == 1 && backendId < 0) || string.IsNullOrEmpty(input?.Model))
        {
            return providers[0];
        }
        // Find the owner via the cached roster lookup (never calls ListModels live here, so an unreachable
        // remote backend can't stall the request). When a backend id is pinned, route to that exact instance.
        ILLMProvider owner = await LLMModelLookup.GetOwningProviderAsync(input.Model, backendId);
        return owner ?? providers[0];
    }

    /// <summary>Sends a message and returns the full response.</summary>
    public static async Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default)
    {
        ILLMProvider provider = await GetProvider(input);
        return await provider.Generate(input);
    }

    /// <summary>Sends a message with streaming chunks via callback. <paramref name="ct"/> is
    /// forwarded to the provider so HTTP requests and async-enumerable streams can be cancelled
    /// when the user hits Stop or the WebSocket closes.</summary>
    public static async Task GenerateStreaming(ExtendedLLMInput input, Action<JObject> onChunk, CancellationToken ct = default)
    {
        ILLMProvider provider = await GetProvider(input);
        await provider.GenerateLive(input, "0", onChunk, ct);
    }

    /// <summary>Counts tokens for the given text. Returns the count plus whether it's exact and the
    /// source label. Tries each provider's tokenizer (first exact wins); falls back to a chars/4
    /// heuristic when no provider can tokenize cheaply.</summary>
    public static (int Count, bool Exact, string Source) CountTokens(string text)
    {
        text ??= "";
        foreach (ILLMProvider provider in LLMProviderRegistry.All)
        {
            try
            {
                int? exact = provider.CountTokens(text);
                if (exact.HasValue)
                {
                    return (exact.Value, true, provider.Id);
                }
            }
            catch
            {
                // Ignore and try the next provider / heuristic.
            }
        }
        return (Math.Max(0, (int)Math.Ceiling(text.Length / 4.0)), false, "heuristic");
    }
}
