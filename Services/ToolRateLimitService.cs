using System.Collections.Concurrent;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Per-user-per-tool sliding-window rate limit, guarding against a misbehaving LLM
/// agentic loop hammering external services or burning API quota. Storage is in-memory only
/// (<see cref="ConcurrentDictionary{TKey, TValue}"/> of ring buffers) — it protects a single
/// running process, not across restarts. Limits are tunable via the per-tool
/// <c>rateLimitPerHour</c> field in the user's <c>ToolConfigService</c> config, falling back to
/// <see cref="DefaultLimits"/>; 0 or negative means no limit.</summary>
public static class ToolRateLimitService
{
    /// <summary>Default per-hour limits per handler ID. Tuned conservatively — generous enough
    /// for normal interactive use, tight enough to stop an agentic loop from drilling a hole.
    /// Handlers not in this dict are not rate-limited by default.</summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultLimits = new Dictionary<string, int>
    {
        [ToolConstants.HttpRequest] = 60,
        [ToolConstants.WebSearch]   = 30,
        [ToolConstants.ShellExec]   = 20,
        [ToolConstants.GenerateImage] = 100,
        [ToolConstants.BatchCaptionFolder] = 5,  // Iterates a folder of images; one call is many model invocations.
    };

    /// <summary>Window over which the limit applies.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Maximum ring-buffer size per (user, tool). Sized to the largest plausible limit
    /// so we never drop a recent timestamp on a slot rotation; oversized slightly for headroom.</summary>
    private const int MaxRingSize = 256;

    /// <summary>Per-(user, handler) ring buffers of recent invocation timestamps.</summary>
    private static readonly ConcurrentDictionary<string, Ring> Rings = new();

    /// <summary>Records an invocation and returns whether it's allowed under the limit. Caller
    /// should call this AFTER the permission check, BEFORE invoking the handler — a rejected call
    /// must not produce side effects. <paramref name="handlerId"/> identifies the handler (not the
    /// custom tool alias). Pass <c>null</c> / unknown for handlers that should always pass through.</summary>
    public static (bool allowed, int limit, int currentCount, TimeSpan retryAfter) TryAcquire(User user, string handlerId)
    {
        if (string.IsNullOrEmpty(handlerId) || user is null)
        {
            return (true, -1, 0, TimeSpan.Zero);
        }
        int limit = ResolveLimit(user, handlerId);
        if (limit <= 0)
        {
            // 0 or negative = disabled by config or no default. No limit applied.
            return (true, -1, 0, TimeSpan.Zero);
        }
        string key = $"{user.UserID} {handlerId}";
        Ring ring = Rings.GetOrAdd(key, _ => new Ring(MaxRingSize));
        return ring.TryAcquire(limit, Window);
    }

    /// <summary>Resolves the effective per-hour limit for a (user, handler). Priority:
    /// 1) user-set override on their tool config, 2) <see cref="DefaultLimits"/>, 3) -1 (no limit).</summary>
    private static int ResolveLimit(User user, string handlerId)
    {
        try
        {
            // ToolConfigService keys are by tool ID. For our built-in tools the ID == handler ID,
            // which is what we want. Custom tool aliases bypass rate limiting (they're already
            // gated by their handler's underlying limit anyway, since rate limits are keyed on
            // handlerId at the dispatcher level).
            Newtonsoft.Json.Linq.JObject cfg = ToolConfigService.GetConfig(handlerId, user);
            if (cfg?["rateLimitPerHour"] is Newtonsoft.Json.Linq.JValue v && v.Value is not null)
            {
                if (int.TryParse(v.Value.ToString(), out int parsed)) { return parsed; }
            }
        }
        catch { /* fall through to defaults on any config read error */ }
        return DefaultLimits.TryGetValue(handlerId, out int dflt) ? dflt : -1;
    }

    /// <summary>For tests / admin tooling. Clears the in-memory state.</summary>
    public static void ResetAll() => Rings.Clear();

    /// <summary>Lock-free-ish ring of timestamps. Adding when full overwrites the oldest. Reads
    /// are advisory (no snapshot semantics) — fine because we always re-evaluate against the
    /// configured limit just before allowing the next call.</summary>
    private sealed class Ring
    {
        private readonly long[] Ticks;
        private int Index;
        private readonly object Lock = new();

        public Ring(int size) { Ticks = new long[size]; }

        public (bool allowed, int limit, int currentCount, TimeSpan retryAfter) TryAcquire(int limit, TimeSpan window)
        {
            long now = DateTime.UtcNow.Ticks;
            long cutoff = now - window.Ticks;
            lock (Lock)
            {
                // Count entries still in window.
                int active = 0;
                long oldestInWindow = long.MaxValue;
                for (int i = 0; i < Ticks.Length; i++)
                {
                    long t = Ticks[i];
                    if (t > cutoff)
                    {
                        active++;
                        if (t < oldestInWindow) { oldestInWindow = t; }
                    }
                }
                if (active >= limit)
                {
                    TimeSpan retryAfter = oldestInWindow == long.MaxValue
                        ? TimeSpan.Zero
                        : TimeSpan.FromTicks((oldestInWindow + window.Ticks) - now);
                    if (retryAfter < TimeSpan.Zero) { retryAfter = TimeSpan.Zero; }
                    return (false, limit, active, retryAfter);
                }
                Ticks[Index] = now;
                Index = (Index + 1) % Ticks.Length;
                return (true, limit, active + 1, TimeSpan.Zero);
            }
        }
    }
}
