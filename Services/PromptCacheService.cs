namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>LRU cache with request deduplication for LLM prompt responses.</summary>
public class PromptCacheService
{
    private readonly int MaxEntries;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new();
    private readonly LinkedList<CacheEntry> LruList = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> Pending = new();
    private readonly object Lock = new();

    public PromptCacheService(int maxEntries = 1000)
    {
        MaxEntries = maxEntries;
    }

    /// <summary>Gets a cached response or creates one via the factory. Deduplicates concurrent identical requests.</summary>
    public async Task<string> GetOrCreate(string prompt, string instructionId, Func<Task<string>> factory, int timeoutMs = 90000)
    {
        string key = NormalizeKey(prompt, instructionId);
        TaskCompletionSource<string> tcs;
        bool isCreator = false;
        lock (Lock)
        {
            if (Cache.TryGetValue(key, out LinkedListNode<CacheEntry> node))
            {
                LruList.Remove(node);
                LruList.AddFirst(node);
                return node.Value.Response;
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
                CacheEntry entry = new() { Key = key, Response = result };
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

    private static string NormalizeKey(string prompt, string instructionId)
    {
        string normalized = prompt?.Trim().ToLowerInvariant().Replace(" ", "") ?? "";
        string instrKey = instructionId?.Trim().ToLowerInvariant() ?? "";
        return $"{normalized}||{instrKey}";
    }

    private class CacheEntry
    {
        public string Key;
        public string Response;
    }
}
