using System.Collections.Concurrent;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>TTL cache around walking the registered <see cref="ILLMProvider"/>s to resolve a model id.
/// <para>Why this exists: <see cref="LLMModelMatcher"/>'s richer kinds (Family/Provider/Tag) need the full
/// <see cref="LLMModelInfo"/>, and the dispatcher needs to know which provider owns a model — but the only
/// source is each provider's <c>ListModels()</c>, which hits the network for remote providers. Calling that
/// on every chat request is unacceptable on a hot path, so the per-provider roster is cached.</para>
/// <para>Mirrors the <see cref="Services.UserPresetCache"/> pattern: per-key TTL, no invalidation API
/// (model rosters change infrequently; the model dropdown endpoint lists uncached for freshness).</para></summary>
public static class LLMModelLookup
{
    /// <summary>How long a provider's cached model roster stays valid before it's re-listed.</summary>
    private static readonly TimeSpan TTL = TimeSpan.FromMinutes(5);
    /// <summary>Bound on a single provider's listing, so a cache-miss on the hot path (eg picking the
    /// owning provider for a send) can't stall on an unresponsive remote endpoint.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);
    /// <summary>One cached provider roster: the models it advertised and when that expires.</summary>
    private record ProviderEntry(DateTime Expires, List<LLMModelInfo> Models);

    /// <summary>Per-provider model roster cache, keyed by <see cref="ILLMProvider.Id"/>.</summary>
    private static readonly ConcurrentDictionary<string, ProviderEntry> ProviderCache = new();

    /// <summary>Returns the provider's models, cached. Never throws — a provider that fails to list
    /// (eg an unreachable remote endpoint) yields an empty list, so it can't block the hot path.</summary>
    private static async Task<List<LLMModelInfo>> GetModelsCached(ILLMProvider provider)
    {
        if (ProviderCache.TryGetValue(provider.Id, out ProviderEntry entry) && entry.Expires > DateTime.UtcNow)
        {
            return entry.Models;
        }
        List<LLMModelInfo> models;
        using CancellationTokenSource cts = new(ListTimeout);
        try
        {
            models = await provider.ListModels(cts.Token) ?? [];
        }
        catch
        {
            models = [];
        }
        ProviderCache[provider.Id] = new ProviderEntry(DateTime.UtcNow + TTL, models);
        return models;
    }

    /// <summary>Returns the <see cref="LLMModelInfo"/> for the given model id, or null if no registered
    /// provider advertises it. Tolerates a null/empty id (returns null).</summary>
    public static async Task<LLMModelInfo> GetByIdAsync(string modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        foreach (ILLMProvider provider in LLMProviderRegistry.All)
        {
            List<LLMModelInfo> models = await GetModelsCached(provider);
            LLMModelInfo match = models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>Returns the provider that advertises the given model id, or null. Cached, so this is safe
    /// to call on every generation request without re-hitting remote endpoints.</summary>
    public static async Task<ILLMProvider> GetOwningProviderAsync(string modelId) => await GetOwningProviderAsync(modelId, -1);

    /// <summary>Returns the provider that advertises the given model id on the given backend instance.
    /// When <paramref name="backendId"/> is &gt;= 0, only a provider advertising the model with that exact
    /// backend id matches (used to route a compare lane to a chosen GPU/device); if none matches the
    /// backend id, falls back to the first provider that owns the model at all. -1 = any backend.</summary>
    public static async Task<ILLMProvider> GetOwningProviderAsync(string modelId, int backendId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        ILLMProvider anyOwner = null;
        foreach (ILLMProvider provider in LLMProviderRegistry.All)
        {
            List<LLMModelInfo> models = await GetModelsCached(provider);
            bool ownsModel = false;
            foreach (LLMModelInfo m in models)
            {
                if (!string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ownsModel = true;
                if (backendId >= 0 && m.BackendId == backendId)
                {
                    return provider;
                }
            }
            anyOwner ??= ownsModel ? provider : null;
        }
        return anyOwner;
    }
}
