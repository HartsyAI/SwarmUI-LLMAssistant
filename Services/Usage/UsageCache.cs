using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Usage;

/// <summary>The replies of each chat as last read, kept while the chat has not changed, so that totals asked for after every reply do not parse and measure
/// every chat of the week again.</summary>
public sealed class UsageCache
{
    /// <summary>The most chats held before the cache starts over.</summary>
    private const int MaxChats = 4000;

    /// <summary>Each chat's replies and the stamp they were read under.</summary>
    private readonly ConcurrentDictionary<string, (string Stamp, IReadOnlyList<ReplyUsage> Replies)> chats = new(StringComparer.Ordinal);

    /// <summary>The replies of the chat <paramref name="key"/>. <paramref name="load"/> is called only when the chat has not been read under <paramref name="stamp"/>;
    /// an empty stamp is never remembered. A chat that no longer loads has none.</summary>
    public IReadOnlyList<ReplyUsage> Replies(string key, string stamp, Func<JObject> load)
    {
        if (!string.IsNullOrEmpty(stamp) && chats.TryGetValue(key, out (string Stamp, IReadOnlyList<ReplyUsage> Replies) held) && held.Stamp == stamp)
        {
            return held.Replies;
        }
        JObject thread = load();
        if (thread is null)
        {
            chats.TryRemove(key, out _);
            return [];
        }
        List<ReplyUsage> replies = UsageReader.Replies(thread);
        if (!string.IsNullOrEmpty(stamp))
        {
            if (chats.Count >= MaxChats)
            {
                chats.Clear();
            }
            chats[key] = (stamp, replies);
        }
        return replies;
    }
}
