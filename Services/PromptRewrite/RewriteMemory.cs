using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;

/// <summary>Remembers, for a short while and per user, the prompts this extension itself produced by rewriting, so a prompt that was already rewritten (the Enhance Prompt button, or a chat tool that passes one on) is not rewritten
/// a second time when it is generated from. Only a hash of the text is kept.</summary>
public static class RewriteGuard
{
    /// <summary>How long a produced prompt is remembered.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>The most prompts remembered per user.</summary>
    public const int MaxPerUser = 64;

    /// <summary>The most users tracked at once.</summary>
    public const int MaxUsers = 500;

    /// <summary>The remembered prompt hashes with the time each was made, newest last, by user.</summary>
    private static readonly ConcurrentDictionary<string, List<(string Hash, DateTime At)>> ByUser = new();

    /// <summary>Remembers that <paramref name="text"/> was produced for <paramref name="userId"/>.</summary>
    public static void Remember(string userId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        if (ByUser.Count >= MaxUsers && !ByUser.ContainsKey(Key(userId)))
        {
            Prune(DateTime.UtcNow);
        }
        List<(string Hash, DateTime At)> list = ByUser.GetOrAdd(Key(userId), _ => []);
        lock (list)
        {
            list.RemoveAll(e => e.Hash == Hash(text));
            list.Add((Hash(text), DateTime.UtcNow));
            while (list.Count > MaxPerUser)
            {
                list.RemoveAt(0);
            }
        }
    }

    /// <summary>Whether <paramref name="text"/> was produced for <paramref name="userId"/> recently.</summary>
    public static bool WasProduced(string userId, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !ByUser.TryGetValue(Key(userId), out List<(string Hash, DateTime At)> list))
        {
            return false;
        }
        string hash = Hash(text);
        DateTime oldest = DateTime.UtcNow - Lifetime;
        lock (list)
        {
            return list.Any(e => e.Hash == hash && e.At >= oldest);
        }
    }

    /// <summary>Forgets everything (for tests).</summary>
    internal static void Clear() => ByUser.Clear();

    /// <summary>Drops expired entries and users with none left.</summary>
    private static void Prune(DateTime now)
    {
        foreach (KeyValuePair<string, List<(string Hash, DateTime At)>> pair in ByUser)
        {
            lock (pair.Value)
            {
                pair.Value.RemoveAll(e => now - e.At > Lifetime);
                if (pair.Value.Count == 0)
                {
                    ByUser.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    /// <summary>The key for a user (no user, the server's own view, is one shared key).</summary>
    private static string Key(string userId) => userId ?? "<server>";

    /// <summary>A short hash of the prompt with its ends trimmed.</summary>
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim())))[..16];
}

/// <summary>Carries what a generation's rewrite did from the engine hook back to the caller that started it (the <c>generate_image</c> tool), through an id the caller put in the request's extra metadata. Entries expire.</summary>
public static class RewriteReports
{
    /// <summary>How long a report waits to be taken.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>The most reports kept.</summary>
    public const int MaxReports = 500;

    /// <summary>The reports by id.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, RewriteResult Result)> Reports = new();

    /// <summary>Keeps <paramref name="result"/> under <paramref name="id"/>.</summary>
    public static void Put(string id, RewriteResult result)
    {
        if (string.IsNullOrEmpty(id) || result is null)
        {
            return;
        }
        DateTime now = DateTime.UtcNow;
        if (Reports.Count >= MaxReports)
        {
            foreach (KeyValuePair<string, (DateTime At, RewriteResult Result)> old in Reports.Where(r => now - r.Value.At > Lifetime).ToList())
            {
                Reports.TryRemove(old.Key, out _);
            }
            foreach (KeyValuePair<string, (DateTime At, RewriteResult Result)> old in Reports.OrderBy(r => r.Value.At).Take(Math.Max(0, Reports.Count - MaxReports + 1)).ToList())
            {
                Reports.TryRemove(old.Key, out _);
            }
        }
        Reports[id] = (now, result);
    }

    /// <summary>Takes (and removes) the report under <paramref name="id"/>, or null when there is none or it expired.</summary>
    public static RewriteResult Take(string id)
    {
        if (string.IsNullOrEmpty(id) || !Reports.TryRemove(id, out (DateTime At, RewriteResult Result) entry))
        {
            return null;
        }
        return DateTime.UtcNow - entry.At > Lifetime ? null : entry.Result;
    }

    /// <summary>Forgets everything (for tests).</summary>
    internal static void Clear() => Reports.Clear();
}

/// <summary>The model of the chat behind each <c>generate_image</c> call that is running, under the call's report id, for the generation hook to write the rewrite with. It is kept here and not in the generation's metadata,
/// so no saved image carries it and no request can set it. The tool removes its entry when the call ends.</summary>
public static class RewriteChatModels
{
    /// <summary>The models by report id.</summary>
    private static readonly ConcurrentDictionary<string, string> ByReport = new();

    /// <summary>Keeps <paramref name="model"/> for the call with report id <paramref name="id"/>; nothing is kept for an empty one.</summary>
    public static void Put(string id, string model)
    {
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(model))
        {
            ByReport[id] = model;
        }
    }

    /// <summary>The model kept under <paramref name="id"/>, or null. It stays: every image of the call asks.</summary>
    public static string For(string id) => id is not null && ByReport.TryGetValue(id, out string model) ? model : null;

    /// <summary>Forgets the entry under <paramref name="id"/>.</summary>
    public static void Remove(string id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            ByReport.TryRemove(id, out _);
        }
    }
}
