using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;

/// <summary>One user's cached chat text for the search. A chat is read from storage again only when its row in the thread index says it changed (<c>updatedAt</c> or title),
/// so typing in the search box never re-reads every chat, and a chat saved by any code path is picked up. Rows that left the index are dropped.
/// <para>Memory is bounded three ways: a user's index holds at most <see cref="MaxUserChars"/> characters of message text (chats after that, newest first, are searched by title only),
/// each chat and message is capped (see <see cref="ThreadSearch"/>), and at most <see cref="MaxCachedUsers"/> users' indexes are kept: the one used longest ago is dropped and rebuilt on that
/// user's next search.</para>
/// <para>Reading chats from storage happens outside any lock, so reading <see cref="Count"/> or <see cref="Partial"/> never waits for a build; two searches at once may both build, and the
/// second to finish simply writes the same result.</para></summary>
public sealed class ThreadSearchIndex
{
    /// <summary>Most characters of message text one user's index holds. About 40 MB in memory (UTF-16).</summary>
    public const int MaxUserChars = 20_000_000;
    /// <summary>Most users whose indexes are kept in memory at once.</summary>
    public const int MaxCachedUsers = 8;

    private readonly object _lock = new();
    private readonly Dictionary<string, (string Stamp, SearchDoc Doc)> _entries = [];
    /// <summary>When this index was last used (a tick count), for dropping the one used longest ago.</summary>
    internal long LastUsed;

    /// <summary>One cache per user id, so one user's text is never searched for another.</summary>
    private static readonly ConcurrentDictionary<string, ThreadSearchIndex> Users = new();

    /// <summary>The cache for this user. Making room first: when more than <see cref="MaxCachedUsers"/> are held, the ones used longest ago are dropped.</summary>
    public static ThreadSearchIndex For(User user)
    {
        ThreadSearchIndex index = Users.GetOrAdd(user.UserID, _ => new ThreadSearchIndex());
        index.LastUsed = Environment.TickCount64;
        EvictOldest(Users, user.UserID);
        return index;
    }

    /// <summary>Drops the least recently used indexes until at most <see cref="MaxCachedUsers"/> are left; never the one for <paramref name="keep"/>.</summary>
    internal static void EvictOldest(ConcurrentDictionary<string, ThreadSearchIndex> users, string keep)
    {
        while (users.Count > MaxCachedUsers)
        {
            string oldest = users.Where(p => p.Key != keep).OrderBy(p => p.Value.LastUsed).Select(p => p.Key).FirstOrDefault();
            if (oldest is null || !users.TryRemove(oldest, out _))
            {
                return;
            }
        }
    }

    /// <summary>How many users' indexes are held (for tests).</summary>
    public static int CachedUsers => Users.Count;

    /// <summary>Drops every user's index (for tests).</summary>
    public static void ClearAll() => Users.Clear();

    /// <summary>How many chats are held (for tests).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Brings the cache in line with <paramref name="index"/> (the thread index rows, newest first) and returns the documents for the rows <paramref name="include"/> accepts.
    /// <paramref name="load"/> reads one chat blob (null if it cannot); it is called only for chats that are new or changed, and never under a lock.</summary>
    public List<SearchDoc> Refresh(JArray index, Func<string, JObject> load, Func<JObject, bool> include = null)
    {
        Dictionary<string, (string Stamp, SearchDoc Doc)> known;
        lock (_lock)
        {
            known = new(_entries);
        }
        List<SearchDoc> docs = [];
        Dictionary<string, (string Stamp, SearchDoc Doc)> rebuilt = [];
        HashSet<string> present = [];
        long used = 0;
        foreach (JToken token in index)
        {
            if (token is not JObject row || row["id"]?.ToString() is not string id || string.IsNullOrEmpty(id) || !present.Add(id))
            {
                continue;
            }
            string title = row["title"]?.ToString() ?? "";
            string updatedAt = row["updatedAt"]?.ToString() ?? "";
            string stamp = updatedAt + "\u0001" + title;
            int budget = (int)Math.Max(0, Math.Min(ThreadSearch.MaxChatChars, MaxUserChars - used));
            // A chat left short of text for lack of room in the index is read again once there is more room.
            bool needsRoom = known.TryGetValue(id, out var entry) && entry.Doc.BudgetLimited && budget > entry.Doc.BuiltBudget;
            if (!known.ContainsKey(id) || entry.Stamp != stamp || needsRoom)
            {
                JObject thread = budget > 0 ? load(id) : null;
                entry = (stamp, ThreadSearch.BuildDoc(id, title, updatedAt, thread, budget));
                rebuilt[id] = entry;
            }
            used += entry.Doc.Chars;
            if (include is null || include(row))
            {
                docs.Add(entry.Doc);
            }
        }
        lock (_lock)
        {
            foreach (string gone in _entries.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _entries.Remove(gone);
            }
            foreach ((string id, var entry) in rebuilt)
            {
                _entries[id] = entry;
            }
        }
        return docs;
    }

    /// <summary>True when any chat was indexed by title only because the index was full.</summary>
    public bool Partial
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Any(e => e.Doc.TitleOnly);
            }
        }
    }
}
