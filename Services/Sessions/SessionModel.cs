using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Sessions;

/// <summary>Where a session (a chat the assistant started and directs) is in its life. A chat that was not started by another has none of these.</summary>
public static class SessionStatuses
{
    /// <summary>Waiting for the global run limit: it starts when another session finishes.</summary>
    public const string Queued = "queued";
    /// <summary>A turn is being worked on.</summary>
    public const string Running = "running";
    /// <summary>A tool call is waiting for the user to allow or refuse it.</summary>
    public const string AwaitingApproval = "awaiting_approval";
    /// <summary>Not running: made but never started, or stopped by the user.</summary>
    public const string Idle = "idle";
    /// <summary>The last turn finished; its text is the session's report.</summary>
    public const string Done = "done";
    /// <summary>The last turn ended on an error.</summary>
    public const string Failed = "failed";
    /// <summary>The server stopped while it was running (turns do not resume).</summary>
    public const string Interrupted = "interrupted";
    /// <summary>Put away: stopped, kept on disk, and hidden from the chat list.</summary>
    public const string Archived = "archived";

    /// <summary>Whether a turn is, or is about to be, in progress.</summary>
    public static bool IsActive(string status) => status is Queued or Running or AwaitingApproval;

    /// <summary>Whether <paramref name="status"/> is one of the known statuses.</summary>
    public static bool IsValid(string status) => status is Queued or Running or AwaitingApproval or Idle or Done or Failed or Interrupted or Archived;
}

/// <summary>The field names a session adds to a chat's stored data and to its index row.</summary>
public static class SessionFields
{
    /// <summary>The chat that started this one. Present on a session only.</summary>
    public const string Parent = "parentThreadId";
    /// <summary>The tool call (in the parent's turn) that started it.</summary>
    public const string SpawnedByCall = "spawnedByCallId";
    /// <summary>How many chats up the line goes: 1 for a session started by a chat the user opened.</summary>
    public const string Depth = "depth";
    /// <summary>A <see cref="SessionStatuses"/> value.</summary>
    public const string Status = "sessionStatus";
    /// <summary>The text of the last finished turn, cut to <see cref="SessionLimits.MaxReportChars"/>.</summary>
    public const string Report = "lastReport";
    /// <summary>The model the session runs on.</summary>
    public const string Model = "sessionModel";
    /// <summary>The tool ids the session may use, fixed when it was made and never wider than its parent's. A chat without this field is not limited.</summary>
    public const string ToolIds = "sessionToolIds";
    /// <summary>The tool call waiting for the user's answer: <c>{approvalId, id, name, arguments, risk, mode, askedAt}</c>. Present only while <see cref="SessionStatuses.AwaitingApproval"/>.</summary>
    public const string PendingApproval = "pendingApproval";
    /// <summary>On a parent: reports from sessions that finished, not yet shown to the model. <c>[{childId, title, status, report, at}]</c>.</summary>
    public const string Notices = "sessionNotices";
}

/// <summary>The limits on how much a chat can start. Read from the merged settings' <c>sessions</c> object, so an admin can change them; each is clamped.</summary>
public sealed class SessionLimits
{
    /// <summary>How deep sessions may nest: 1 lets a chat start sessions, but those cannot start more.</summary>
    public int MaxDepth = 1;
    /// <summary>The most sessions (not archived) one chat may have started.</summary>
    public int MaxChildrenPerParent = 8;
    /// <summary>The most sessions one user may have working at once; more wait in a queue. A session that is only waiting on others does not count.</summary>
    public int MaxRunningPerUser = 3;
    /// <summary>How long a call waits for the user's answer in a session nobody has open before it is refused.</summary>
    public int ApprovalTimeoutSeconds = 900;

    /// <summary>The most characters of a session's report kept and sent to its parent.</summary>
    public const int MaxReportChars = 4000;
    /// <summary>The most characters of a session's title.</summary>
    public const int MaxTitleChars = 80;
    /// <summary>The most characters of the first message to a session, and of a later one.</summary>
    public const int MaxPromptChars = 20_000;
    /// <summary>The longest a session_wait may block, in seconds.</summary>
    public const int MaxWaitSeconds = 600;

    /// <summary>The limits from settings, or the defaults for anything missing or out of range.</summary>
    public static SessionLimits From(JObject settings)
    {
        SessionLimits limits = new();
        if (settings?["sessions"] is JObject s)
        {
            limits.MaxDepth = Clamp(s["maxDepth"], limits.MaxDepth, 1, 3);
            limits.MaxChildrenPerParent = Clamp(s["maxChildrenPerParent"], limits.MaxChildrenPerParent, 1, 32);
            limits.MaxRunningPerUser = Clamp(s["maxRunningPerUser"], limits.MaxRunningPerUser, 1, 8);
            limits.ApprovalTimeoutSeconds = Clamp(s["approvalTimeoutSeconds"], limits.ApprovalTimeoutSeconds, 10, 86_400);
        }
        return limits;
    }

    /// <summary>An integer setting inside a range, or the fallback when it is absent or not an integer.</summary>
    private static int Clamp(JToken value, int fallback, int min, int max)
        => value is { Type: JTokenType.Integer } ? Math.Clamp(value.Value<int>(), min, max) : fallback;
}
