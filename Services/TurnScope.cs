using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>What surrounds one reply to a chat, whoever asked for it (a chat socket, or a request that waits for the answer): the chat is registered as having a reply in flight (so Stop reaches
/// it, and a second reply cannot start beside it), and a chat that is a session shows as working while it runs and reports to its parent when it ends. One place for it, so the callers cannot
/// drift apart on what a reply's start and end do to the chat.</summary>
public sealed class TurnScope : IDisposable
{
    /// <summary>The user.</summary>
    private readonly User _user;

    /// <summary>The chat.</summary>
    private readonly string _threadId;

    /// <summary>Whether the chat is a session (it then has a status to keep).</summary>
    private readonly bool _isSession;

    /// <summary>Whether the scope has ended.</summary>
    private bool _ended;

    /// <summary>Cancelled by Stop (the registry's source for the chat). Every wait inside the reply should honour it.</summary>
    public CancellationTokenSource Cts { get; }

    /// <summary>How a session should be left when the scope ends without being stopped: <c>done</c> (the default) or <c>failed</c>.</summary>
    public string Outcome = SessionStatuses.Done;

    /// <summary>The reason shown on a session that failed.</summary>
    public string FailureReason;

    private TurnScope(User user, string threadId, bool isSession, CancellationTokenSource cts)
    {
        _user = user;
        _threadId = threadId;
        _isSession = isSession;
        Cts = cts;
    }

    /// <summary>Registers the reply, and marks a session working. Registered before any setup so a Stop clicked during it has somewhere to land.</summary>
    public static TurnScope Begin(Session session, string threadId, JObject thread)
    {
        // A reply of this user's begins: an extraction of theirs still running from an earlier turn is stopped, so it never waits ahead of this reply on a backend with one slot.
        Memory.MemoryExtractionRegistry.CancelUser(session.User.UserID);
        CancellationTokenSource cts = GenerationCancellationRegistry.Begin(threadId);
        bool isSession = SessionManager.IsSession(thread);
        if (isSession)
        {
            SessionManager.SetStatus(session.User, threadId, SessionStatuses.Running);
        }
        return new TurnScope(session.User, threadId, isSession, cts);
    }

    /// <summary>Marks the reply as stopped before it began (setup was stopped): a session is left idle.</summary>
    public void MarkIdle() => Outcome = SessionStatuses.Idle;

    /// <summary>Marks the reply as failed.</summary>
    public void MarkFailed(string reason = null)
    {
        Outcome = SessionStatuses.Failed;
        FailureReason = reason;
    }

    /// <summary>Ends the reply: unregisters it, and leaves a session as it ended (idle when it was stopped). Returns whether the user asked to stop, which is only knowable before the
    /// source is disposed: a Stop makes a reply return normally after saving what it had, so this is the one signal left that the user asked for it. Safe to call more than once.</summary>
    public bool End()
    {
        bool stopped = Cts.IsCancellationRequested;
        if (_ended)
        {
            return stopped;
        }
        _ended = true;
        GenerationCancellationRegistry.End(_threadId, Cts);
        if (_isSession)
        {
            SessionManager.FinishTurn(_user, _threadId, stopped ? SessionStatuses.Idle : Outcome, FailureReason);
        }
        return stopped;
    }

    /// <inheritdoc/>
    public void Dispose() => End();
}
