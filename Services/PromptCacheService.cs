namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>LRU + TTL cache with request deduplication for LLM prompt responses.
/// <para>Keys include the calling user, model, and assistant — the response depends on all of them
/// (persona/system prompt, the user's memory profile via <c>{{userProfile}}</c>, model choice), so a
/// prompt-text-only key served one user's response (seeded with their personal profile facts) to
/// another user, and kept serving a stale persona after the caller switched assistants.</para></summary>
public class PromptCacheService
{
    private readonly int MaxEntries;
    /// <summary>Entries older than this are treated as misses. The cache exists to dedupe a batch
    /// (seconds apart) and quick regenerate loops — not to pin yesterday's response forever.</summary>
    private static readonly TimeSpan TTL = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new();
    private readonly LinkedList<CacheEntry> LruList = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> Pending = new();
    private readonly object Lock = new();

    public PromptCacheService(int maxEntries = 1000)
    {
        MaxEntries = maxEntries;
    }

    /// <summary>Gets a cached response or creates one via the factory. Deduplicates concurrent identical
    /// requests. <paramref name="userId"/>, <paramref name="model"/>, and <paramref name="assistantId"/>
    /// are part of the key — see the class doc for why they must be.</summary>
    public async Task<string> GetOrCreate(string userId, string model, string assistantId, string prompt, string instructionId, Func<Task<string>> factory, int timeoutMs = 90000)
    {
        string key = BuildKey(userId, model, assistantId, prompt, instructionId);
        TaskCompletionSource<string> tcs;
        bool isCreator = false;
        lock (Lock)
        {
            if (Cache.TryGetValue(key, out LinkedListNode<CacheEntry> node))
            {
                if (DateTime.UtcNow - node.Value.CreatedAt > TTL)
                {
                    // Expired — evict and fall through to the create path.
                    LruList.Remove(node);
                    Cache.Remove(key);
                }
                else
                {
                    LruList.Remove(node);
                    LruList.AddFirst(node);
                    return node.Value.Response;
                }
            }
            if (Pending.TryGetValue(key, out tcs))
            {
                // Another request for this key is already in flight — wait for it below.
            }
            else
            {
                tcs = new TaskCompletionSource<string>();
                Pending[key] = tcs;
                isCreator = true;
            }
        }
        if (!isCreator)
        {
            using CancellationTokenSource cts = new(timeoutMs);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out waiting for cached response after {timeoutMs}ms.");
            }
        }
        // We are the creator — run the factory
        try
        {
            string result = await factory();
            lock (Lock)
            {
                CacheEntry entry = new() { Key = key, Response = result, CreatedAt = DateTime.UtcNow };
                LinkedListNode<CacheEntry> node = LruList.AddFirst(entry);
                Cache[key] = node;
                while (Cache.Count > MaxEntries)
                {
                    LinkedListNode<CacheEntry> last = LruList.Last;
                    Cache.Remove(last.Value.Key);
                    LruList.RemoveLast();
                }
                Pending.Remove(key);
            }
            tcs.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            lock (Lock)
            {
                Pending.Remove(key);
            }
            tcs.TrySetException(ex);
            throw;
        }
    }

    /// <summary>Clears the entire cache.</summary>
    public void Clear()
    {
        lock (Lock)
        {
            Cache.Clear();
            LruList.Clear();
        }
    }

    private static string BuildKey(string userId, string model, string assistantId, string prompt, string instructionId)
    {
        // Trim-only on the prompt: batch dedup only needs exact repeats of the same prompt string.
        // (The old lowercase + strip-ALL-spaces normalization was aggressive enough to collide
        // genuinely different prompts.) '\u001F' (unit separator) can't appear in the components.
        return string.Join('\u001F',
            userId ?? "<anon>",
            model ?? "",
            assistantId ?? "",
            instructionId?.Trim().ToLowerInvariant() ?? "",
            prompt?.Trim() ?? "");
    }

    private class CacheEntry
    {
        public string Key;
        public string Response;
        public DateTime CreatedAt;
    }
}
