using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>Routes LLM requests to a registered <see cref="ILLMProvider"/>.
/// <para>A request that names no model is given the default one first (<see cref="LLMModelLookup.ResolveEffectiveModelIdAsync"/>, the same a chat turn gets), because a local backend cannot answer without a
/// name. The dispatcher then asks each provider's <see cref="ILLMProvider.ListModels"/> which one owns the model, and falls back to the first registered provider. Providers are supplied by the removable
/// <c>Backends/</c> pack; with none registered, every call reports "no LLM backend running".</para></summary>
public static class LLMDispatcher
{
    /// <summary>Gets the best provider for the given input, first naming the default model on an input that names none: the provider that advertises the model, otherwise the first registered
    /// provider.</summary>
    public static async Task<ILLMProvider> GetProvider(ExtendedLLMInput input = null)
    {
        List<ILLMProvider> providers = LLMProviderRegistry.All.ToList();
        if (providers.Count == 0)
        {
            throw new SwarmReadableErrorException("No LLM backend is running. Add one in Server > Backends.");
        }
        if (input is not null && string.IsNullOrWhiteSpace(input.Model))
        {
            // Still empty when no backend says or lists one; a server that picks its own model can then do so.
            input.Model = await LLMModelLookup.ResolveEffectiveModelIdAsync(input.Model, input.RequestSession);
        }
        int backendId = input?.BackendId ?? -1;
        if ((providers.Count == 1 && backendId < 0) || string.IsNullOrEmpty(input?.Model))
        {
            return providers[0];
        }
        ILLMProvider owner = await LLMModelLookup.GetOwningProviderAsync(input.Model, backendId, input.RequestSession);
        return owner ?? providers[0];
    }

    /// <summary>Sends a message and returns the full response. <paramref name="ct"/> is forwarded to
    /// the provider so a Stop or a closed socket cancels the request.</summary>
    public static async Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default)
    {
        ILLMProvider provider = await GetProvider(input);
        return await provider.Generate(input, ct);
    }

    /// <summary>Sends a message and returns the answer, the reasoning apart from it, and the finish reason
    /// (<see cref="ILLMProvider.GenerateDetailed"/>).</summary>
    public static async Task<LLMGenerateResult> GenerateDetailed(ExtendedLLMInput input, CancellationToken ct = default)
    {
        ILLMProvider provider = await GetProvider(input);
        return await provider.GenerateDetailed(input, ct);
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
            }
        }
        return (Math.Max(0, (int)Math.Ceiling(text.Length / 4.0)), false, "heuristic");
    }
}
