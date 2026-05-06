using System.Collections.Concurrent;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Tiny TTL cache around walking running LLM backends to find a model by id.
/// <para>Why this exists: <see cref="LLMModelMatcher"/>'s richer kinds (Family/Provider/Tag) need
/// the full <see cref="LLMModelInfo"/>, but the only way to get one is to call each backend's
/// <c>ListModels()</c>. Doing that on every chat request would hit the network for remote
/// providers — totally unacceptable on a hot path. Cache and move on.</para>
/// <para>Mirrors the <see cref="Services.UserPresetCache"/> pattern: per-key TTL, no
/// invalidation API needed because model rosters change infrequently.</para></summary>
public static class LLMModelLookup
{
    private static readonly TimeSpan TTL = TimeSpan.FromMinutes(5);
    private record Entry(DateTime Expires, LLMModelInfo Info);

    private static readonly ConcurrentDictionary<string, Entry> _cache = new();

    /// <summary>Returns the <see cref="LLMModelInfo"/> for the given model id, or null if no
    /// running backend advertises it. Tolerates a null/empty id (returns null). Never throws —
    /// backends that fail to list models are skipped.</summary>
    public static async Task<LLMModelInfo> GetByIdAsync(string modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        if (_cache.TryGetValue(modelId, out Entry entry) && entry.Expires > DateTime.UtcNow)
        {
            return entry.Info;
        }
        LLMModelInfo found = null;
        foreach (AbstractLLMBackend backend in Program.Backends.RunningBackendsOfType<AbstractLLMBackend>())
        {
            try
            {
                List<LLMModelInfo> models = await backend.ListModels();
                LLMModelInfo match = models?.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    found = match;
                    break;
                }
            }
            catch
            {
                // Backend failed to list models — skip and try the next.
            }
        }
        // Cache even nulls (avoids repeatedly hammering backends for an unknown model id).
        _cache[modelId] = new Entry(DateTime.UtcNow + TTL, found);
        return found;
    }
}
