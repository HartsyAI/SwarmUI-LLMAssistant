using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Model listing endpoints for LLM models.
/// Delegates to the registered <see cref="ILLMProvider"/>s (supplied by the removable backend pack)
/// so all available LLM models are included.</summary>
public static class ModelEndpoints
{
    /// <summary>Per-provider listing bound for the interactive dropdown — a single unresponsive backend
    /// shouldn't hold up the whole model list (which it would under its full connection timeout).</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Returns available LLM models from all registered LLM providers. Each provider is queried
    /// in parallel under a bounded timeout; whatever resolves is returned, and any provider that timed
    /// out or errored is reported in <c>warnings</c> so the UI can degrade gracefully instead of hanging.</summary>
    public static async Task<JObject> LLMAssistantGetModels(Session session)
    {
        List<ILLMProvider> providers = LLMProviderRegistry.All.ToList();
        (ILLMProvider Provider, List<LLMModelInfo> Models, string Error)[] results =
            await Task.WhenAll(providers.Select(ListWithTimeout));
        JArray models = [];
        JArray warnings = [];
        foreach ((ILLMProvider provider, List<LLMModelInfo> providerModels, string error) in results)
        {
            if (error is not null)
            {
                warnings.Add($"{provider.DisplayName}: {error}");
                continue;
            }
            foreach (LLMModelInfo model in providerModels)
            {
                JObject entry = model.ToJson();
                // Extension compat: add "title" as alias for "name" for the UI dropdown
                entry["title"] = entry["name"];
                models.Add(entry);
            }
        }
        return new JObject
        {
            ["success"] = true,
            ["models"] = models,
            ["warnings"] = warnings
        };
    }

    /// <summary>Lists one provider's models under <see cref="ListTimeout"/>, turning a timeout/error into a
    /// human-readable message instead of a thrown exception so one bad backend can't fail the whole call.</summary>
    private static async Task<(ILLMProvider, List<LLMModelInfo>, string)> ListWithTimeout(ILLMProvider provider)
    {
        using CancellationTokenSource cts = new(ListTimeout);
        try
        {
            return (provider, await provider.ListModels(cts.Token) ?? [], null);
        }
        catch (OperationCanceledException)
        {
            return (provider, null, $"did not respond within {ListTimeout.TotalSeconds:0}s (backend unreachable?)");
        }
        catch (Exception ex)
        {
            return (provider, null, ex.Message);
        }
    }
}
