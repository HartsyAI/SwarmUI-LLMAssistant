using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;

/// <summary>One user's cached chat text for the search. A chat is read from storage again only when its row in the thread index says it changed (<c>updatedAt</c> or title),
/// so typing in the search box never re-reads every chat, and a chat saved by any code path is picked up. Rows that left the index are dropped.
/// <para>Holds at most <see cref="MaxUserChars"/> characters of message text; chats after that (newest first, the order of the index) are searched by title only.</para></summary>
public sealed class ThreadSearchIndex
{
    /// <summary>Most characters of message text one user's index holds. About 80 MB in memory (UTF-16).</summary>
    public const int MaxUserChars = 40_000_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, (string Stamp, SearchDoc Doc)> _entries = [];

    /// <summary>One cache per user id, so one user's text is never searched for another.</summary>
    private static readonly ConcurrentDictionary<string, ThreadSearchIndex> Users = new();

    /// <summary>The cache for this user.</summary>
    public static ThreadSearchIndex For(User user)
    {
        return Users.GetOrAdd(user.UserID, _ => new ThreadSearchIndex());
    }

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
    /// <paramref name="load"/> reads one chat blob (null if it cannot); it is called only for chats that are new or changed.</summary>
    public List<SearchDoc> Refresh(JArray index, Func<string, JObject> load, Func<JObject, bool> include = null)
    {
        List<SearchDoc> docs = [];
        lock (_lock)
        {
            HashSet<string> present = [];
            long used = 0;
            foreach (JToken token in index)
            {
                if (token is JObject row && row["id"]?.ToString() is string id && !string.IsNullOrEmpty(id))
                {
                    present.Add(id);
                }
            }
            foreach (string gone in _entries.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _entries.Remove(gone);
            }
            HashSet<string> seen = [];
            foreach (JToken token in index)
            {
                if (token is not JObject row || row["id"]?.ToString() is not string id || string.IsNullOrEmpty(id) || !seen.Add(id))
                {
                    continue;
                }
                string title = row["title"]?.ToString() ?? "";
                string updatedAt = row["updatedAt"]?.ToString() ?? "";
                string stamp = updatedAt + "\u0001" + title;
                int budget = (int)Math.Max(0, Math.Min(ThreadSearch.MaxChatChars, MaxUserChars - used));
                // A chat left title-only for lack of room is read again once there is room.
                if (!_entries.TryGetValue(id, out var entry) || entry.Stamp != stamp || (entry.Doc.TitleOnly && budget > 0))
                {
                    JObject thread = budget > 0 ? load(id) : null;
                    entry = (stamp, ThreadSearch.BuildDoc(id, title, updatedAt, thread, budget));
                    _entries[id] = entry;
                }
                used += entry.Doc.Chars;
                if (include is null || include(row))
                {
                    docs.Add(entry.Doc);
                }
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
