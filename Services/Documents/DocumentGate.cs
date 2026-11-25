using System.Collections.Concurrent;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>Whose turn it is to read a file: one at a time for each user, so a user with slow files only holds up their own, and a few at a time for the whole server.</summary>
/// <param name="atOnce">How many files are read at once, in all.</param>
public sealed class DocumentGate(int atOnce)
{
    private readonly SemaphoreSlim All = new(atOnce, atOnce);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> Owners = new();

    /// <summary>Waits for a turn: first the owner's own, then one of the server's. Disposing what is returned gives both back. Null when no turn came within <paramref name="wait"/>.
    /// An empty owner takes a server turn only.</summary>
    public async Task<IDisposable> EnterAsync(string owner, TimeSpan wait, CancellationToken ct)
    {
        SemaphoreSlim mine = string.IsNullOrEmpty(owner) ? null : Owners.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        DateTime deadline = DateTime.UtcNow + wait;
        if (mine is not null && !await mine.WaitAsync(wait, ct))
        {
            return null;
        }
        bool entered = false;
        try
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            entered = await All.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, ct);
        }
        finally
        {
            if (!entered)
            {
                mine?.Release();
            }
        }
        return entered ? new Turn(All, mine) : null;
    }

    private sealed class Turn(SemaphoreSlim all, SemaphoreSlim mine) : IDisposable
    {
        private int Done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref Done, 1) == 0)
            {
                all.Release();
                mine?.Release();
            }
        }
    }
}
