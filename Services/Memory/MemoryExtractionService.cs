using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>Extraction calls in flight, by user and chat (a chat id is chosen by the client, so it is never a key on its own), so Stop, a new turn, a closed socket or Clear can reach one: a reply's own
/// cancellation source is gone once the reply has ended, and the extractor runs after that. Cancelling here can never throw into the caller (a reply, a Clear): a callback that throws while the
/// source is cancelled is logged and ignored.</summary>
public static class MemoryExtractionRegistry
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Active = new();

    private static string Key(string userId, string threadId) => $"{userId}|{threadId}";

    /// <summary>Registers an extraction for a user's chat; null when one is already running for it.</summary>
    public static CancellationTokenSource Begin(string userId, string threadId)
    {
        CancellationTokenSource cts = new();
        if (!Active.TryAdd(Key(userId, threadId), cts))
        {
            cts.Dispose();
            return null;
        }
        return cts;
    }

    /// <summary>Reserves the extraction of a turn: registered as soon as the reply is over (before the title is made), so that Stop, a closed socket or a new turn that comes while the title is
    /// being written already finds it. Null when one is already running for the chat. The slot cancels only its own source, so a late close of an earlier turn cannot stop a later one.</summary>
    public static MemoryExtractionSlot Reserve(string userId, string threadId)
    {
        CancellationTokenSource cts = Begin(userId, threadId);
        return cts is null ? null : new MemoryExtractionSlot(userId, threadId, cts);
    }

    /// <summary>Reserves a turn's extraction only when the chat socket is still open (otherwise nobody is left to show the notice to, and null is returned).</summary>
    public static MemoryExtractionSlot ReserveIfOpen(bool socketOpen, string userId, string threadId) => socketOpen ? Reserve(userId, threadId) : null;

    /// <summary>Unregisters and disposes the extraction (only if it is still the one registered).</summary>
    public static void End(string userId, string threadId, CancellationTokenSource cts)
    {
        ((ICollection<KeyValuePair<string, CancellationTokenSource>>)Active).Remove(new(Key(userId, threadId), cts));
        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    /// <summary>Cancels the extraction running for a user's chat, if any (what Stop does). Returns whether there was one.</summary>
    public static bool Cancel(string userId, string threadId) => CancelKey(Key(userId, threadId));

    /// <summary>Cancels every extraction running for a user (a new turn of theirs begins, or they clear their memory). Returns whether there was one.</summary>
    public static bool CancelUser(string userId)
    {
        bool any = false;
        foreach (string key in Active.Keys.Where(k => k.StartsWith(userId + "|", StringComparison.Ordinal)).ToList())
        {
            any |= CancelKey(key);
        }
        return any;
    }

    private static bool CancelKey(string key) => Active.TryGetValue(key, out CancellationTokenSource cts) && SafeCancel(cts);

    /// <summary>Cancels a source without ever throwing: <see cref="CancellationTokenSource.Cancel()"/> runs the callbacks on this thread and rethrows what they throw. Returns whether it was cancelled.</summary>
    internal static bool SafeCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] A callback failed while a memory extraction was being cancelled: {ex.Message}");
            return true;
        }
    }
}

/// <summary>One turn's reserved extraction (see <see cref="MemoryExtractionRegistry.Reserve"/>). Disposing it unregisters it.</summary>
public sealed class MemoryExtractionSlot : IDisposable
{
    private readonly string _userId, _threadId;

    internal MemoryExtractionSlot(string userId, string threadId, CancellationTokenSource cts)
    {
        _userId = userId;
        _threadId = threadId;
        Cts = cts;
    }

    /// <summary>The source this extraction runs on.</summary>
    public CancellationTokenSource Cts { get; }

    /// <summary>Cancels this turn's extraction (and only this turn's). Never throws.</summary>
    public void Cancel() => MemoryExtractionRegistry.SafeCancel(Cts);

    /// <inheritdoc/>
    public void Dispose() => MemoryExtractionRegistry.End(_userId, _threadId, Cts);
}

/// <summary>What one extraction did.</summary>
/// <param name="Status"><c>saved</c>, <c>nothing</c> (the model found nothing worth saving, or every operation was refused or already there), <c>failed</c> (the call or its answer was unusable: nothing saved,
/// the model is left alone for a while), <c>cancelled</c>, or <c>skipped</c>.</param>
/// <param name="Changes">What was saved (<c>saved</c> only).</param>
/// <param name="Detail">Why, for the log and for tests.</param>
public sealed record ExtractionOutcome(string Status, MemoryChangeSet Changes, string Detail);

/// <summary>The automatic after-turn memory extractor: once a chat reply has been delivered, asks a model to propose add, update and delete operations on the user's notes, checks every one in code, and
/// applies them as one undoable change. It is off unless the user turns it on in Settings, and it never gets in the way of a reply: it runs after the reply, is cut off by a timeout and by Stop, and any
/// failure saves nothing.
/// <para><b>Spend.</b> The extractor makes an unattended model call, so it is bounded: at most one extraction per user message (a regenerated reply is not extracted again), at most
/// <see cref="MaxPerHour"/> per user per hour, at most one at a time per user, a small input (<see cref="MemoryTranscriptBuilder.MaxTotalChars"/> characters and a few notes) and a small answer
/// (<see cref="MaxTokens"/>), at most two calls for one extraction (the second only when the first answer was not the JSON asked for), a cheap check that skips short, impersonal and secret-looking turns
/// before any model is called, and a back-off after a failure. There is no central spend limit in the extension to defer to; these are the extractor's own.</para>
/// <para><b>Injection.</b> What it reads is decided by <see cref="MemoryTranscriptBuilder"/> and what it may write by <see cref="MemoryOpParser"/> and <see cref="MemoryStore.ApplyExtraction"/>.</para></summary>
public static class MemoryExtractionService
{
    /// <summary>The most extractions one user may run in an hour.</summary>
    public const int MaxPerHour = 20;

    /// <summary>The most tokens an answer may have (three short operations fit well within it).</summary>
    public const int MaxTokens = 600;

    /// <summary>How long one call may take before it is given up on.</summary>
    internal static TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a whole extraction (both calls, when the first answer was unusable) may take.</summary>
    internal static TimeSpan OverallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long a model that failed is left alone (per user and model).</summary>
    internal static TimeSpan Backoff = TimeSpan.FromMinutes(5);

    /// <summary>The hour over which <see cref="MaxPerHour"/> counts.</summary>
    internal static TimeSpan RateWindow = TimeSpan.FromHours(1);

    /// <summary>Makes one model call; replaced in tests.</summary>
    public delegate Task<LLMGenerateResult> Generator(ExtendedLLMInput input, CancellationToken ct);

    private static readonly ConcurrentDictionary<string, DateTime> BackoffUntil = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastWarned = new();
    private static readonly ConcurrentDictionary<string, DateTime> Claimed = new();
    private static readonly ConcurrentDictionary<string, bool> Running = new();
    private static readonly ConcurrentDictionary<string, List<DateTime>> Calls = new();

    /// <summary>The most remembered user messages (the oldest are forgotten first).</summary>
    private const int MaxClaimed = 2000;

    /// <summary>A short, impersonal or thank-you message: nothing to remember.</summary>
    private static readonly Regex Pleasantry = new(@"^\W*(thanks?|thank you|thx|ok(ay)?|yes|yep|yeah|no|nope|sure|cool|great|nice|got it|perfect|hi|hello|hey|lol|good (morning|night|evening)|bye)\b[\s\W]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Something that may be about the user: a first-person word, or a word of liking, disliking or remembering.</summary>
    private static readonly Regex Personal = new(@"\b(i|i'm|im|i've|ive|i'd|i'll|my|mine|myself|me|we|we're|our|call me|remember|prefer\w*|favou?rite|always|never|hate|love|like)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Whether a user message is too slight to be worth a model call: very short, a pleasantry, fewer than three words, or with nothing about the user in it.</summary>
    public static bool IsTrivial(string text)
    {
        string t = (text ?? "").Trim();
        try
        {
            return t.Length < 8 || t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length < 3 || Pleasantry.IsMatch(t) || !Personal.IsMatch(t);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>Forgets every back-off, count and remembered turn (for tests).</summary>
    internal static void ResetForTests()
    {
        BackoffUntil.Clear();
        LastWarned.Clear();
        Claimed.Clear();
        Running.Clear();
        Calls.Clear();
    }

    /// <summary>What a model that failed is remembered under: the user and the model.</summary>
    internal static string BackoffKey(string userId, string model) => $"{userId}|{model}";

    /// <summary>Whether the user's model is being left alone after a failure.</summary>
    internal static bool IsBackingOff(string userId, string model) => BackoffUntil.TryGetValue(BackoffKey(userId, model), out DateTime until) && until > DateTime.UtcNow;

    /// <summary>Counts a call against the user's hour; false when they are at the limit.</summary>
    private static DateTime? TryAcquire(string userId)
    {
        List<DateTime> calls = Calls.GetOrAdd(userId, _ => []);
        lock (calls)
        {
            DateTime now = DateTime.UtcNow;
            calls.RemoveAll(t => now - t > RateWindow);
            if (calls.Count >= MaxPerHour)
            {
                return null;
            }
            calls.Add(now);
            return now;
        }
    }

    /// <summary>Gives back an hourly slot taken by <see cref="TryAcquire"/> (the extraction was cancelled before it saved anything).</summary>
    private static void Refund(string userId, DateTime stamp)
    {
        if (Calls.TryGetValue(userId, out List<DateTime> calls))
        {
            lock (calls)
            {
                calls.Remove(stamp);
            }
        }
    }

    /// <summary>Marks a user message as extracted; false when it already was (a regenerated reply answers the same message).</summary>
    private static string ClaimKey(string userId, string threadId, string messageId, string text)
        => $"{userId}|{threadId}|{(string.IsNullOrEmpty(messageId) ? MemoryStore.TextHash(text) : messageId)}";

    private static bool Claim(string key)
    {
        if (Claimed.Count > MaxClaimed)
        {
            foreach (string old in Claimed.OrderBy(kv => kv.Value).Take(MaxClaimed / 4).Select(kv => kv.Key))
            {
                Claimed.TryRemove(old, out _);
            }
        }
        return Claimed.TryAdd(key, DateTime.UtcNow);
    }

    /// <summary>Logs a warning once per back-off window.</summary>
    private static void WarnOnce(string key, string message)
    {
        DateTime now = DateTime.UtcNow;
        if (!LastWarned.TryGetValue(key, out DateTime last) || now - last >= Backoff)
        {
            LastWarned[key] = now;
            Logs.Warning($"[LLMAssistant] {message}");
        }
    }

    /// <summary>The chat reply has been delivered: runs the extraction if the user has turned it on, and tells <paramref name="notify"/> what it changed. Never throws and never waits on anything the
    /// reply needed; the caller awaits it only so the chat socket stays open to carry the notice. Everything cheap that can say no is checked first: the master switch, the user's memory
    /// permission, a session chat, the chat's and the assistant's own opt-outs.</summary>
    public static async Task RunAfterTurnAsync(Session session, string threadId, string chatModel, Func<JObject, Task> notify, MemoryExtractionSlot slot = null, Generator generate = null)
    {
        // The slot reserved by the caller (before the title was made) is used and disposed here. Without one (the socket was already closed, or one is already running) nothing runs: it is never
        // reserved here, so an extraction cannot start for a chat nobody is left to show a notice to.
        if (slot is null)
        {
            return;
        }
        try
        {
            User user = session?.User;
            if (user is null || string.IsNullOrEmpty(threadId))
            {
                return;
            }
            // The cheapest check first: with the master switch off (the default) a reply costs nothing more than this.
            MemoryExtractionSettings config = MemoryExtractionSettings.ForUser(user);
            if (!config.Enabled || !user.HasPermission(LLMAssistantAPI.PermToolMemory))
            {
                return;
            }
            JObject settings = SettingsService.GetMergedSettings(user);
            JObject thread = ThreadStorageService.GetThread(user, threadId);
            if (thread is null || SessionManager.IsSession(thread) || (thread["memoryExtraction"]?.Type == JTokenType.Boolean && !thread["memoryExtraction"].Value<bool>()))
            {
                return;
            }
            string assistantId = thread["assistantId"]?.ToString();
            if (string.IsNullOrEmpty(assistantId))
            {
                assistantId = AssistantService.GetActiveAssistantId(settings, user);
            }
            if (!AssistantResolver.Resolve(assistantId, user, settings).MemoryExtraction)
            {
                return;
            }
            if (slot is null || slot.Cts.IsCancellationRequested)
            {
                return;
            }
            ExtractionOutcome outcome = await ExtractAsync(MemoryService.StoreFor(user), user.UserID, threadId, thread, config, chatModel, session, generate ?? LLMDispatcher.GenerateDetailed, slot.Cts.Token);
            Logs.Debug($"[LLMAssistant] Memory extraction for chat {threadId}: {outcome.Status} ({outcome.Detail}).");
            if (outcome.Changes is not null && notify is not null)
            {
                await notify(new JObject { ["memoryChanges"] = outcome.Changes.ToClientJson() });
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: nothing was saved.
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Memory extraction for chat {threadId} failed: {ex.Message}");
        }
        finally
        {
            slot?.Dispose();
        }
    }

    /// <summary>The extraction itself, for a store and chat that are known (the part tests drive). Never throws except <see cref="OperationCanceledException"/> before the model is called.</summary>
    internal static async Task<ExtractionOutcome> ExtractAsync(MemoryStore store, string userId, string threadId, JObject thread, MemoryExtractionSettings config, string chatModel, Session session, Generator generate, CancellationToken ct)
    {
        MemoryTranscript transcript = MemoryTranscriptBuilder.Build(thread);
        if (transcript is null)
        {
            return Skipped("nothing to read (no finished reply to a message the user wrote)");
        }
        if (transcript.CalledMemoryWrite)
        {
            return Skipped("the model saved a note itself this turn");
        }
        if (IsTrivial(transcript.LatestUserText))
        {
            return Skipped("a trivial message");
        }
        MemoryNeverSave never = new(config.NeverSave);
        // Before any model sees it: a turn that mentions a secret or something the user listed is not sent to the extractor at all (it may be another backend than the chat's).
        if (transcript.Lines.Any(l => never.Matches(l.Text)))
        {
            return Skipped("the turn mentions something that is never saved");
        }
        string model = string.IsNullOrWhiteSpace(config.Model) ? chatModel ?? "" : config.Model.Trim();
        if (IsBackingOff(userId, model))
        {
            return Skipped("the model is being left alone after a failure");
        }
        if (store.Status() != MemoryLoadStatus.Ok)
        {
            return Skipped("the stored notes are not in a state that is written automatically");
        }
        // One at a time per user, checked first so a message that is turned away for it is not used up (nor counted against the hour).
        if (!Running.TryAdd(userId, true))
        {
            return Skipped("another extraction is running for this user");
        }
        try
        {
            string claimKey = ClaimKey(userId, threadId, transcript.LatestUserId, transcript.LatestUserText);
            if (!Claim(claimKey))
            {
                return Skipped("this message was already extracted");
            }
            DateTime? acquired = TryAcquire(userId);
            if (acquired is null)
            {
                Claimed.TryRemove(claimKey, out _);
                return Skipped($"{MaxPerHour} extractions in the last hour");
            }
            // A cancelled extraction (Stop, a new turn, a closed socket, Clear) saved nothing, so it does not use the message up or count against the hour.
            ExtractionOutcome Cancelled(string why)
            {
                Claimed.TryRemove(claimKey, out _);
                Refund(userId, acquired.Value);
                return new ExtractionOutcome("cancelled", null, why);
            }
            if (ct.IsCancellationRequested)
            {
                return Cancelled("stopped");
            }
            // Memory cleared while the model is working must not be written to afterwards: the epoch is read before the notes are, and checked again under the lock when saving.
            int epoch = store.Epoch();
            MemoryExtractionPrompt prompt = MemoryExtractionPrompt.Build(store.Load(), transcript, never);
            string userMessage = prompt.User;
            MemoryParseResult parsed = null;
            // The whole extraction, retry included, has one deadline.
            using CancellationTokenSource overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overall.CancelAfter(OverallTimeout);
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                string answer;
                try
                {
                    answer = await CallAsync(prompt.System, userMessage, model, session, generate, overall.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return Cancelled("stopped");
                }
                catch (Exception ex)
                {
                    return Fail(userId, model, ex is OperationCanceledException ? "the model took too long" : ex.Message);
                }
                if (ct.IsCancellationRequested)
                {
                    return Cancelled("stopped");
                }
                parsed = MemoryOpParser.Parse(answer, prompt.Shown, transcript, never);
                if (parsed.Error is null)
                {
                    break;
                }
                userMessage = prompt.User + $"\n\nYour previous answer could not be used: {parsed.Error} Reply again with only the JSON object, in the shape described.";
            }
            if (parsed.Error is not null)
            {
                return Fail(userId, model, $"the answer was not usable twice ({parsed.Error})");
            }
            if (parsed.Ops.Count == 0)
            {
                return new ExtractionOutcome("nothing", null, parsed.Rejected > 0 ? $"{parsed.Rejected} operations were refused" : "nothing worth saving");
            }
            MemoryApplyResult applied = store.ApplyExtraction(parsed.Ops, threadId, () => ct.IsCancellationRequested, epoch);
            if (applied.Refused is "cancelled" or "cleared")
            {
                return Cancelled(applied.Refused == "cleared" ? "memory was cleared meanwhile" : "stopped before saving");
            }
            return applied.Changes is null
                ? new ExtractionOutcome("nothing", null, applied.Refused.Length > 0 ? $"not saved: {applied.Refused}" : $"{applied.Skipped} operations had nothing to do")
                : new ExtractionOutcome("saved", applied.Changes, $"{applied.Changes.Changes.Count} changes");
        }
        finally
        {
            Running.TryRemove(userId, out _);
        }
    }

    /// <summary>One model call: no tools, no conversation (so it never disturbs the chat's reused prompt), thinking off, a small answer, and a time limit.</summary>
    private static async Task<string> CallAsync(string system, string user, string model, Session session, Generator generate, CancellationToken ct)
    {
        ExtendedLLMInput input = ExtendedLLMInput.Create(user, system, string.IsNullOrWhiteSpace(model) ? null : model);
        input.MaxTokens = MaxTokens;
        input.Temperature = 0.1;
        input.RequestSession = session;
        input.ConversationId = null;
        input.EnableThinking = false;
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CallTimeout);
        LLMGenerateResult result = await generate(input, limit.Token);
        return result?.Response ?? "";
    }

    private static ExtractionOutcome Skipped(string why) => new("skipped", null, why);

    /// <summary>A failed extraction: nothing was saved, and this user's model is left alone for a while (said once in the log).</summary>
    private static ExtractionOutcome Fail(string userId, string model, string why)
    {
        string key = BackoffKey(userId, model);
        BackoffUntil[key] = DateTime.UtcNow + Backoff;
        WarnOnce(key, $"The memory extractor could not use model '{model}' for {userId} ({why}); nothing was saved, and it is left alone for {Backoff.TotalMinutes:0} minutes.");
        return new ExtractionOutcome("failed", null, why);
    }
}
