using System.Collections.Concurrent;
using Hartsy.Extensions.LLMAssistant.Services;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

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

    /// <summary>Per-provider model roster cache, keyed by <see cref="ILLMProvider.Id"/>, plus <c>|</c> and the user id for a
    /// provider whose models vary by user.</summary>
    private static readonly ConcurrentDictionary<string, ProviderEntry> ProviderCache = new();

    /// <summary>Returns the provider's models as <paramref name="session"/>'s user sees them (with their own key, on a provider
    /// whose models vary by user), cached. Never throws — a provider that fails to list (eg an unreachable remote endpoint)
    /// yields an empty list, so it can't block the hot path.</summary>
    private static async Task<List<LLMModelInfo>> GetModelsCached(ILLMProvider provider, Session session)
    {
        bool perUser = provider.ModelsVaryByUser && session?.User is not null;
        string key = perUser ? $"{provider.Id}|{session.User.UserID}" : provider.Id;
        if (ProviderCache.TryGetValue(key, out ProviderEntry entry) && entry.Expires > DateTime.UtcNow)
        {
            return entry.Models;
        }
        List<LLMModelInfo> models;
        using CancellationTokenSource cts = new(ListTimeout);
        try
        {
            models = await (perUser ? provider.ListModelsFor(session, cts.Token) : provider.ListModels(cts.Token)) ?? [];
        }
        catch
        {
            models = [];
        }
        ProviderCache[key] = new ProviderEntry(DateTime.UtcNow + TTL, models);
        return models;
    }

    /// <summary>Every provider and every model they offer to <paramref name="session"/>'s user. With <paramref name="refresh"/> each provider is asked for its models again (bounded by the usual
    /// timeout) instead of using the cached roster; the cache itself is left as it is, because it is shared and one user's check must not make everyone's next request wait on a slow endpoint.
    /// For the readiness check, which must say what is there without waiting on a dead endpoint for long.</summary>
    public static async Task<(int Providers, List<LLMModelInfo> Models)> ListAllAsync(Session session, bool refresh)
    {
        List<LLMModelInfo> all = [];
        List<ILLMProvider> providers = [.. LLMProviderRegistry.All];
        foreach (ILLMProvider provider in providers)
        {
            if (!refresh)
            {
                all.AddRange(await GetModelsCached(provider, session));
                continue;
            }
            using CancellationTokenSource cts = new(ListTimeout);
            try
            {
                all.AddRange(await (provider.ModelsVaryByUser && session?.User is not null ? provider.ListModelsFor(session, cts.Token) : provider.ListModels(cts.Token)) ?? []);
            }
            catch
            {
                // A provider that does not answer offers nothing this time, which is what the check is there to show.
            }
        }
        return (providers.Count, all);
    }

    /// <summary>The first model any registered provider advertises, or null when none does. For callers
    /// that cannot name a model because there is no one to pick one — a voice satellite sends an
    /// utterance and nothing else. Uses the same cached rosters as the lookups above, so it costs
    /// nothing on the hot path. Pass the caller's <paramref name="session"/> so a provider whose models
    /// depend on each user's own key (Claude) lists that user's models.</summary>
    public static async Task<LLMModelInfo> GetFirstAvailableAsync(Session session = null)
    {
        foreach (ILLMProvider provider in LLMProviderRegistry.All)
        {
            List<LLMModelInfo> models = await GetModelsCached(provider, session);
            if (models.Count > 0)
            {
                return models[0];
            }
        }
        return null;
    }

    /// <summary>The id of the model that will answer a request that names <paramref name="model"/> (as given, or empty for none): the same one when it is named; otherwise the default one for
    /// <paramref name="session"/>'s user (see <see cref="DefaultModelIdAsync"/>, with the <c>preferredModel</c> of their settings and the model they last picked in the chat). Empty when there is none. Read-only: uses
    /// the cached rosters and changes nothing.</summary>
    public static async Task<string> ResolveEffectiveModelIdAsync(string model, Session session = null)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model.Trim();
        }
        string preferred = null, picked = null;
        if (session?.User is not null)
        {
            preferred = SettingsService.GetMergedSettings(session.User)["preferredModel"]?.ToString();
            // The chat's model picker keeps its choice in the user's own session state.
            picked = SessionStateService.Get(session.User)["currentModel"]?.ToString();
        }
        return await DefaultModelIdAsync(preferred, session, picked);
    }

    /// <summary>The default model: <paramref name="preferred"/>, else <paramref name="picked"/>, when a provider offers it to <paramref name="session"/>'s user; else the model the dispatcher's provider answers with
    /// by default (the first registered provider, as <see cref="LLMDispatcher.GetProvider"/> chooses); else the first model any provider lists. Empty when there is none.</summary>
    internal static async Task<string> DefaultModelIdAsync(string preferred, Session session = null, string picked = null)
    {
        foreach (string choice in new[] { preferred, picked })
        {
            if (!string.IsNullOrWhiteSpace(choice) && await GetByIdAsync(choice.Trim(), session) is LLMModelInfo offered)
            {
                return offered.Id;
            }
        }
        ILLMProvider first = LLMProviderRegistry.All.FirstOrDefault();
        string defaultId = first?.DefaultModelId;
        if (!string.IsNullOrWhiteSpace(defaultId))
        {
            return defaultId.Trim();
        }
        return (await GetFirstAvailableAsync(session))?.Id ?? "";
    }

    /// <summary>Returns the <see cref="LLMModelInfo"/> for the given model id, or null if no registered
    /// provider advertises it to <paramref name="session"/>'s user (null: the server's own view). Tolerates a null/empty id (returns null).</summary>
    public static async Task<LLMModelInfo> GetByIdAsync(string modelId, Session session = null)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        foreach (ILLMProvider provider in LLMProviderRegistry.All)
        {
            List<LLMModelInfo> models = await GetModelsCached(provider, session);
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
    /// backend id, falls back to the first provider that owns the model at all. -1 = any backend. Models are listed as
    /// <paramref name="session"/>'s user sees them (null: the server's own view).</summary>
    public static async Task<ILLMProvider> GetOwningProviderAsync(string modelId, int backendId, Session session = null)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        ILLMProvider anyOwner = null;
        foreach (ILLMProvider provider in LLMProviderRegistry.All)
        {
            List<LLMModelInfo> models = await GetModelsCached(provider, session);
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
