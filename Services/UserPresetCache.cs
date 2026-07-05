using System.Collections.Concurrent;
using SwarmUI.Accounts;
using SwarmUI.Text2Image;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Tiny per-user TTL cache around <see cref="User.GetAllPresets"/>.
/// <para>Why this exists: <c>GenerateImageTool.EnrichForUser</c> runs on every chat WS request to
/// inject the user's saved presets into the LLM's tool description. The underlying
/// <c>User.GetAllPresets()</c> takes <c>SessionHandlerSource.DBLock</c> on each call and re-reads
/// the LiteDB collection — fine for a one-off UI call, but not for a hot path that fires once per
/// user message. Presets change infrequently; a few minutes of staleness is acceptable.</para>
/// <para>Invalidation: best-effort. Callers that mutate presets should call
/// <see cref="Invalidate"/>; otherwise stale entries age out via TTL.</para></summary>
public static class UserPresetCache
{
    /// <summary>How long a cached preset list stays valid before re-querying.
    /// Tuned to "long enough to dedupe per-conversation reads, short enough that a user who
    /// just saved a preset on the Generate tab will see it in chat within ~one back-and-forth".</summary>
    private static readonly TimeSpan TTL = TimeSpan.FromMinutes(5);

    private record Entry(DateTime Expires, List<T2IPreset> Presets);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new();

    /// <summary>Returns the user's saved presets, possibly from cache. Empty list if user is null
    /// or the underlying lookup fails. Never throws.</summary>
    public static List<T2IPreset> GetPresets(User user)
    {
        if (user is null)
        {
            return [];
        }
        if (Cache.TryGetValue(user.UserID, out Entry entry) && entry.Expires > DateTime.UtcNow)
        {
            return entry.Presets;
        }
        List<T2IPreset> fresh;
        try
        {
            fresh = user.GetAllPresets() ?? [];
        }
        catch
        {
            fresh = [];
        }
        Cache[user.UserID] = new Entry(DateTime.UtcNow + TTL, fresh);
        return fresh;
    }

    /// <summary>Looks up a single preset by name from cache (then falls back to a fresh DB read
    /// if not found, since cache could be stale on adds). Returns null if the user has no preset
    /// with that name.</summary>
    public static T2IPreset GetPreset(User user, string name)
    {
        if (user is null || string.IsNullOrEmpty(name))
        {
            return null;
        }
        T2IPreset cached = GetPresets(user).FirstOrDefault(p => string.Equals(p.Title, name, StringComparison.OrdinalIgnoreCase));
        if (cached is not null)
        {
            return cached;
        }
        // Cache miss — could be a brand-new preset added since we cached. Bypass cache once.
        try
        {
            return user.GetPreset(name);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops the cached entry for one user. Call after the user adds/edits/deletes a preset
    /// so the next read picks up the change immediately rather than waiting for TTL.</summary>
    public static void Invalidate(User user)
    {
        if (user is not null)
        {
            Cache.TryRemove(user.UserID, out _);
        }
    }
}
