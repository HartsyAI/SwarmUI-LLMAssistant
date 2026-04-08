namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>LRU cache with request deduplication for LLM prompt responses.</summary>
public class PromptCacheService
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _lruList = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly object _lock = new();

    public PromptCacheService(int maxEntries = 1000)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>Gets a cached response or creates one via the factory. Deduplicates concurrent identical requests.</summary>
    public async Task<string> GetOrCreate(string prompt, string instructionId, Func<Task<string>> factory, int timeoutMs = 90000)
    {
        string key = NormalizeKey(prompt, instructionId);
        TaskCompletionSource<string> tcs;
        bool isCreator = false;
        lock (_lock)
        {
            // Check cache
            if (_cache.TryGetValue(key, out LinkedListNode<CacheEntry> node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return node.Value.Response;
            }
            // Check if another request is already pending for this key
            if (_pending.TryGetValue(key, out tcs))
            {
                // Wait for the other request
            }
            else
            {
                tcs = new TaskCompletionSource<string>();
                _pending[key] = tcs;
                isCreator = true;
            }
        }
        if (!isCreator)
        {
            // Wait for the creator to finish
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
            lock (_lock)
            {
                // Add to cache
                CacheEntry entry = new() { Key = key, Response = result };
                LinkedListNode<CacheEntry> node = _lruList.AddFirst(entry);
                _cache[key] = node;
                // Evict if over capacity
                while (_cache.Count > _maxEntries)
                {
                    LinkedListNode<CacheEntry> last = _lruList.Last;
                    _cache.Remove(last.Value.Key);
                    _lruList.RemoveLast();
                }
                _pending.Remove(key);
            }
            tcs.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _pending.Remove(key);
            }
            tcs.TrySetException(ex);
            throw;
        }
    }

    /// <summary>Clears the entire cache.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
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
