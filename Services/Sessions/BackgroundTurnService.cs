using System.Collections.Concurrent;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Sessions;

/// <summary>Runs a chat's turns with no socket attached, for sessions the assistant started. It is the one owner of "a turn is running for chat X" in the background: a
/// message to a session that is working waits in an inbox and goes in when the turn ends, a stop reaches the turn through the same registry the Stop button uses, and how many
/// run at once is bounded per user (the rest wait in a queue).
/// <para>Turns do not outlive the server: what was running when it stopped is marked <c>interrupted</c> the next time the chat list is read
/// (<see cref="SessionManager.Reconcile"/>), because the live table here is empty after a restart.</para></summary>
public static class BackgroundTurnService
{
    /// <summary>One session's place in the service, from the moment it is accepted until it has no more to do.</summary>
    private sealed class Entry
    {
        /// <summary>The user and chat it belongs to.</summary>
        public string UserId, ThreadId;

        /// <summary>The request the work is done for (re-checked, and replaced when it has expired, when a turn starts).</summary>
        public Session Session;

        /// <summary>The limits in force when it was accepted.</summary>
        public SessionLimits Limits;

        /// <summary>Messages for the session that are not in its chat yet. The first turn starts from the first; a later one is a message sent while it was working.</summary>
        public readonly ConcurrentQueue<string> Inbox = new();

        /// <summary>Completes when the entry has no more to do.</summary>
        public Task Completion = Task.CompletedTask;

        /// <summary>Whether it has left the queue and is working.</summary>
        public bool Started;

        /// <summary>Whether it is inside a wait for other sessions, which does not count against the running limit (or a session waiting on one that is queued could never go on).</summary>
        public bool Waiting;

        /// <summary>Set by a stop, so a turn still setting up is cancelled as soon as it can be.</summary>
        public volatile bool StopRequested;
    }

    /// <summary>Guards the tables below.</summary>
    private static readonly object Gate = new();

    /// <summary>Every session being worked on or waiting its turn, by user and chat.</summary>
    private static readonly Dictionary<string, Entry> Live = [];

    /// <summary>Sessions waiting for a free place, oldest first, by user.</summary>
    private static readonly Dictionary<string, Queue<Entry>> Queues = [];

    /// <summary>The key a user's chat is stored under.</summary>
    private static string Key(string userId, string threadId) => $"{userId}|{threadId}";

    /// <summary>Whether a turn is running, or waiting to, for a chat.</summary>
    public static bool IsLive(string userId, string threadId)
    {
        lock (Gate)
        {
            return Live.ContainsKey(Key(userId, threadId));
        }
    }

    /// <summary>How many of a user's sessions are working (waiting for the user does not count: it is not using the model) and how many are waiting for a place.</summary>
    public static (int Working, int Queued) CountFor(string userId)
    {
        lock (Gate)
        {
            List<Entry> mine = [.. Live.Values.Where(e => e.UserId == userId)];
            return (mine.Count(e => e.Started && !e.Waiting), mine.Count(e => !e.Started));
        }
    }

    /// <summary>Hands a message to a session that is working, to go in when its turn ends. False when nothing is running for it (start one with <see cref="Start"/>).</summary>
    public static bool TryEnqueue(string userId, string threadId, string message)
    {
        lock (Gate)
        {
            if (!Live.TryGetValue(Key(userId, threadId), out Entry entry))
            {
                return false;
            }
            entry.Inbox.Enqueue(message);
            return true;
        }
    }

    /// <summary>Starts a session on its first (or next) message. Returns the status it is in now, <c>running</c> or <c>queued</c>, or null when a turn is already running for it
    /// (use <see cref="TryEnqueue"/>).</summary>
    public static string Start(Session session, string threadId, string message, SessionLimits limits)
    {
        string userId = session.User.UserID;
        Entry entry = new() { UserId = userId, ThreadId = threadId, Session = session, Limits = limits };
        entry.Inbox.Enqueue(message);
        bool queued;
        lock (Gate)
        {
            if (Live.ContainsKey(Key(userId, threadId)))
            {
                return null;
            }
            Live[Key(userId, threadId)] = entry;
            queued = RunningCount(userId) >= limits.MaxRunningPerUser;
            if (queued)
            {
                if (!Queues.TryGetValue(userId, out Queue<Entry> queue))
                {
                    Queues[userId] = queue = new();
                }
                queue.Enqueue(entry);
            }
            else
            {
                Launch(entry);
            }
        }
        string status = queued ? SessionStatuses.Queued : SessionStatuses.Running;
        SessionManager.SetStatus(session.User, threadId, status);
        return status;
    }

    /// <summary>How many sessions of a user are working (not queued, not waiting on others). Call with <see cref="Gate"/> held.</summary>
    private static int RunningCount(string userId) => Live.Values.Count(e => e.UserId == userId && e.Started && !e.Waiting);

    /// <summary>Starts the entry's work. Call with <see cref="Gate"/> held.</summary>
    private static void Launch(Entry entry)
    {
        entry.Started = true;
        entry.Completion = Task.Run(() => RunAsync(entry));
    }

    /// <summary>Starts queued sessions of a user while there are places for them.</summary>
    private static void Pump(string userId)
    {
        lock (Gate)
        {
            while (Queues.TryGetValue(userId, out Queue<Entry> queue) && queue.Count > 0 && RunningCount(userId) < queue.Peek().Limits.MaxRunningPerUser)
            {
                Launch(queue.Dequeue());
            }
        }
    }

    /// <summary>Marks the chat as waiting for other sessions (or not), so that wait does not hold one of the user's places. Does nothing for a chat that is not being worked on in
    /// the background (the user's own chat).</summary>
    public static void SetWaiting(string userId, string threadId, bool waiting)
    {
        lock (Gate)
        {
            if (Live.TryGetValue(Key(userId, threadId), out Entry entry))
            {
                entry.Waiting = waiting;
            }
        }
        if (waiting)
        {
            Pump(userId);
        }
    }

    /// <summary>Stops whatever is running for a chat: a queued session leaves the queue, a working one is cancelled and given up to <paramref name="waitMs"/> to save what it had.
    /// Messages waiting in its inbox are dropped. Returns whether there was anything to stop.</summary>
    public static async Task<bool> StopAsync(User user, string threadId, int waitMs = 10_000)
    {
        Entry entry;
        bool wasQueued = false;
        lock (Gate)
        {
            if (!Live.TryGetValue(Key(user.UserID, threadId), out entry))
            {
                return false;
            }
            entry.StopRequested = true;
            while (entry.Inbox.TryDequeue(out _)) { }
            if (!entry.Started)
            {
                wasQueued = true;
                Live.Remove(Key(user.UserID, threadId));
                Queues[user.UserID] = new Queue<Entry>(Queues[user.UserID].Where(e => e != entry));
            }
        }
        if (wasQueued)
        {
            SessionManager.SetStatus(user, threadId, SessionStatuses.Idle);
            return true;
        }
        GenerationCancellationRegistry.Cancel(threadId);
        await Task.WhenAny(entry.Completion, Task.Delay(waitMs));
        return true;
    }

    /// <summary>Does the entry's work: runs a turn for each batch of messages in its inbox until there are none, records how each turn ended, and then leaves the table.</summary>
    private static async Task RunAsync(Entry entry)
    {
        User user = entry.Session.User;
        try
        {
            while (true)
            {
                List<string> batch = [];
                while (entry.Inbox.TryDequeue(out string message))
                {
                    batch.Add(message);
                }
                if (batch.Count > 0)
                {
                    foreach (string message in batch)
                    {
                        // Marked: these come from the model that started or messaged the session (or a schedule), not from the user typing, so they never count as the user asking for something (see MemoryService.IsWrittenByUser).
                        ThreadStorageService.AppendMessage(user, entry.ThreadId, new JObject { ["role"] = Roles.User, ["content"] = message, ["kind"] = "session_message" });
                    }
                    SessionManager.SetStatus(user, entry.ThreadId, SessionStatuses.Running);
                    (string status, string error) = await RunTurnAsync(entry);
                    SessionManager.FinishTurn(user, entry.ThreadId, status, error);
                }
                lock (Gate)
                {
                    // Nothing came in while the turn ended: this is the last look, and leaving the table is in the same step, so a message cannot slip in between.
                    if (entry.Inbox.IsEmpty || entry.StopRequested)
                    {
                        Live.Remove(Key(entry.UserId, entry.ThreadId));
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Session {entry.ThreadId} stopped on an error: {ex.Message}");
            SessionManager.FinishTurn(user, entry.ThreadId, SessionStatuses.Failed, ex.Message);
            lock (Gate)
            {
                Live.Remove(Key(entry.UserId, entry.ThreadId));
            }
        }
        finally
        {
            Pump(entry.UserId);
        }
    }

    /// <summary>Runs one turn on the chat as it is now, and says how it ended: <c>done</c>, <c>failed</c> (with why) or <c>idle</c> (stopped).</summary>
    private static async Task<(string Status, string Error)> RunTurnAsync(Entry entry)
    {
        User user = entry.Session.User;
        CancellationTokenSource cts = GenerationCancellationRegistry.Begin(entry.ThreadId);
        try
        {
            if (entry.StopRequested)
            {
                return (SessionStatuses.Idle, null);
            }
            JObject thread = ThreadStorageService.GetThread(user, entry.ThreadId);
            if (thread is null)
            {
                return (SessionStatuses.Failed, "The session's chat is gone.");
            }
            Session session = LiveSession(entry);
            JObject request = new() { ["model"] = thread[SessionFields.Model]?.ToString() };
            PreparedTurn prepared = await TurnSetupService.PrepareAsync(session, entry.ThreadId, thread, request, null, cts.Token);
            if (prepared is null || entry.StopRequested)
            {
                return (SessionStatuses.Idle, null);
            }
            ToolTurnContext context = new()
            {
                Session = session,
                ThreadId = entry.ThreadId,
                AssistantId = prepared.AssistantId,
                ApprovalMode = ApprovalPolicy.Normalize(thread["approvalMode"]?.ToString()),
            };
            using BackgroundSink sink = new(user, entry.ThreadId, entry.Limits);
            ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(prepared.Input, context, sink, cts.Token);
            if (cts.IsCancellationRequested || entry.StopRequested)
            {
                return (SessionStatuses.Idle, null);
            }
            return outcome.StopReason == "error" ? (SessionStatuses.Failed, outcome.Error) : (SessionStatuses.Done, null);
        }
        catch (OperationCanceledException)
        {
            return (SessionStatuses.Idle, null);
        }
        catch (Exception ex)
        {
            return (SessionStatuses.Failed, ex.Message);
        }
        finally
        {
            GenerationCancellationRegistry.End(entry.ThreadId, cts);
        }
    }

    /// <summary>The session the work runs under: the one it was started from while that still exists, else a new one for the same user (a turn can outlast the browser session that
    /// began it).</summary>
    private static Session LiveSession(Entry entry)
    {
        Session session = entry.Session;
        if (!Program.Sessions.Sessions.ContainsKey(session.ID))
        {
            try
            {
                session = entry.Session = Program.Sessions.CreateSession("llmassistant-session", session.User.UserID, persist: false);
            }
            catch (Exception ex)
            {
                Logs.Warning($"[LLMAssistant] Could not open a session for {entry.ThreadId}, so it uses the one it began with: {ex.Message}");
            }
        }
        return session;
    }
}

/// <summary>Where a background turn's events go: nowhere on screen. A tool call that needs the user is recorded on the chat (and its status shows it is waiting), so the user finds it
/// by opening the session; it is refused if nobody answers in time.</summary>
public sealed class BackgroundSink(User user, string threadId, SessionLimits limits) : IToolTurnSink, IDisposable
{
    /// <summary>Cancels the wait for the answer to the call that is being asked about.</summary>
    private CancellationTokenSource _timeout;

    /// <inheritdoc/>
    public bool IsOpen => true;

    /// <inheritdoc/>
    public bool CanApprove => true;

    /// <inheritdoc/>
    public Task Chunk(string text) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Iteration(int round) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Reasoning(string text) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ReasoningEnd() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Status(string phase) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ToolCall(JObject call) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ToolApproval(JObject request)
    {
        JObject pending = (JObject)request.DeepClone();
        pending["askedAt"] = DateTime.UtcNow.ToString("o");
        SessionManager.SetPendingApproval(user, threadId, pending);
        // A session waiting for the user is not using the model, so it does not hold one of the user's places (or unattended runs waiting for an answer could use them all up).
        BackgroundTurnService.SetWaiting(user.UserID, threadId, true);
        // Nobody may ever open the session: refuse the call after a while rather than hold the session forever.
        string approvalId = request["approvalId"]?.ToString();
        CancellationTokenSource timeout = new();
        CancellationTokenSource previous = Interlocked.Exchange(ref _timeout, timeout);
        previous?.Cancel();
        string owner = user.UserID;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(limits.ApprovalTimeoutSeconds), timeout.Token);
                ToolApprovals.Pending.TryResolve(owner, approvalId, "deny");
            }
            catch (OperationCanceledException)
            {
                // Answered, or the turn ended first.
            }
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ToolResult(JObject result) => Answered();

    /// <inheritdoc/>
    public Task ToolNotRun(JObject result) => Answered();

    /// <inheritdoc/>
    public Task Done(ToolTurnOutcome outcome) => Answered();

    /// <summary>The call that was waiting has an answer (or the turn ended): the session is working again.</summary>
    private Task Answered()
    {
        CancellationTokenSource timeout = Interlocked.Exchange(ref _timeout, null);
        if (timeout is not null)
        {
            timeout.Cancel();
            SessionManager.SetPendingApproval(user, threadId, null);
            BackgroundTurnService.SetWaiting(user.UserID, threadId, false);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose() => Interlocked.Exchange(ref _timeout, null)?.Cancel();
}
