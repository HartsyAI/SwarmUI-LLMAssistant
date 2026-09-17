using System.Collections.Concurrent;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>In-memory registry of in-flight chat/compare generations, keyed by thread id, so the Stop
/// button can actually cancel server-side generation instead of only detaching the client. Ephemeral
/// and per-process: nothing here is persisted, and there is exactly one entry per thread at a time
/// (a compare-mode turn's N lanes all share one entry, so one Stop cancels every lane).</summary>
public static class GenerationCancellationRegistry
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Active = new();

    /// <summary>Registers a new in-flight generation for a thread. Cancels (but does not dispose) any
    /// stale entry already registered for the same thread: the turn that owns the stale source is still
    /// unwinding and holds its own linked tokens derived from it, so disposing here would race an
    /// in-flight read of <c>linked.Token</c> and throw <see cref="ObjectDisposedException"/> deep inside
    /// that turn's own cleanup. The stale turn's own <see cref="End"/> call disposes it once it's actually
    /// done. Call <see cref="End"/> from a <c>finally</c> block once the generation this token guards has
    /// fully finished.
    ///
    /// Uses <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate"/> rather than a separate
    /// remove-then-write so two concurrent <see cref="Begin"/> calls for the same thread (two tabs, a
    /// retried request) can't both read "nothing here yet" and each write their own entry: whichever
    /// call loses the race gets its stale value handed to it via the update factory and cancelled there,
    /// so no in-flight source is ever orphaned uncancelled. The factory can run more than once under
    /// contention and its result can be discarded if another thread's write wins; never dispose the
    /// stale source here; only <see cref="End"/> disposes.</summary>
    public static CancellationTokenSource Begin(string threadId)
    {
        CancellationTokenSource cts = new();
        Active.AddOrUpdate(threadId, cts, (_, stale) =>
        {
            stale.Cancel();
            return cts;
        });
        return cts;
    }

    /// <summary>Unregisters a thread's entry once its generation has finished (success, error, or
    /// cancellation), and disposes the source. Only removes the entry if it's still the exact source
    /// this call was given: a newer <see cref="Begin"/> for the same thread (the user sent another
    /// message immediately, or cancelled and replaced it) must keep its own entry intact.
    ///
    /// The check-and-remove is done as one atomic operation via the dictionary's
    /// <see cref="ICollection{T}"/> view: a plain <c>TryGetValue</c> followed by a separate
    /// <c>TryRemove</c> has a window where a concurrent <see cref="Begin"/> can replace the entry between
    /// the two calls, and the unconditional <c>TryRemove</c> would then delete the NEW turn's source
    /// instead of this stale one, leaving that new turn unfindable and unstoppable. Disposal is
    /// idempotent since <see cref="Cancel"/> may have already removed this same entry from <see cref="Active"/>.</summary>
    public static void End(string threadId, CancellationTokenSource cts)
    {
        ((ICollection<KeyValuePair<string, CancellationTokenSource>>)Active).Remove(new(threadId, cts));
        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by a racing End() call; nothing left to clean up.
        }
    }

    /// <summary>Cancels and removes the in-flight generation for a thread, if any. Returns true if
    /// something was actually cancelled, false if nothing was in flight for that thread. Removes the
    /// entry immediately (rather than leaving it for the generation's own <see cref="End"/> call) so a
    /// second Stop click, or a new message sent right after, doesn't see a stale cancelled-but-present
    /// entry.</summary>
    public static bool Cancel(string threadId)
    {
        if (!string.IsNullOrEmpty(threadId) && Active.TryRemove(threadId, out CancellationTokenSource cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }
}
