using FreneticUtilities.FreneticExtensions;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Write-through cache over <see cref="User.GetGenericData"/> so hot paths skip the LiteDB read (and its global DBLock).
/// <para>Only for small keys this extension alone writes, never thread blobs (unbounded) or API keys (written by core).</para>
/// <para>A user deleted and recreated with the same ID in one process would see stale entries until restart.</para></summary>
public static class GenericDataCache
{
    /// <summary>Cached raw values keyed like core's generic-data IDs. A null value caches "not found".</summary>
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>Serializes miss-population against writes so a slow read can't overwrite a newer save.</summary>
    private static readonly object Lock = new();

    /// <summary>Cache key for one user's generic-data entry.</summary>
    private static string Key(User user, string dataName, string name) => $"{user.UserID}///{dataName}///{name.ToLowerFast()}";

    /// <summary>Returns the generic-data value, or null if not found. Reads the DB at most once per key.</summary>
    public static string Get(User user, string dataName, string name)
    {
        string key = Key(user, dataName, name);
        if (Cache.TryGetValue(key, out string cached))
        {
            return cached;
        }
        lock (Lock)
        {
            if (Cache.TryGetValue(key, out cached))
            {
                return cached;
            }
            string data = user.GetGenericData(dataName, name);
            Cache[key] = data;
            return data;
        }
    }

    /// <summary>Saves the generic-data value to the DB and the cache.</summary>
    public static void Save(User user, string dataName, string name, string data)
    {
        lock (Lock)
        {
            user.SaveGenericData(dataName, name, data);
            Cache[Key(user, dataName, name)] = data;
        }
    }

    /// <summary>Deletes the generic-data value from the DB and caches it as not found. Returns whether the DB had it.</summary>
    public static bool Delete(User user, string dataName, string name)
    {
        lock (Lock)
        {
            bool deleted = user.DeleteGenericData(dataName, name);
            Cache[Key(user, dataName, name)] = null;
            return deleted;
        }
    }
}
