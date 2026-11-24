using System.Collections.Concurrent;

namespace Hartsy.Extensions.LLMAssistant.Services.ImageTools;

/// <summary>A small per-user cache whose entries expire after a short time: the installed models are listed through core on every image tool call, which is more work than a generation request needs, and what is
/// installed does not change from one call to the next. Entries are dropped when core refreshes its model lists (see <see cref="ImageCatalogSource"/>). Nothing here is shared between users.</summary>
public sealed class UserTtlCache<T>(TimeSpan ttl, Func<DateTime> now = null)
{
    private readonly ConcurrentDictionary<string, (DateTime At, T Value)> _entries = new();
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    /// <summary>The cached value for <paramref name="userId"/>, when there is one that has not expired.</summary>
    public bool TryGet(string userId, out T value)
    {
        if (userId is not null && _entries.TryGetValue(userId, out (DateTime At, T Value) entry) && _now() - entry.At < ttl)
        {
            value = entry.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Keeps <paramref name="value"/> for <paramref name="userId"/>.</summary>
    public void Set(string userId, T value)
    {
        if (userId is not null)
        {
            _entries[userId] = (_now(), value);
        }
    }

    /// <summary>Forgets every entry.</summary>
    public void Clear() => _entries.Clear();
}
