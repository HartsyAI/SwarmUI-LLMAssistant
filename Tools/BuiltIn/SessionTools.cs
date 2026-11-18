using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>What every session tool shares: the calling chat is the only thing a tool can reach. A tool works on the sessions the calling chat started, found by id and checked
/// against it, so a model cannot read or steer a chat it did not start, and nothing here works outside a saved chat.</summary>
public abstract class SessionToolBase : ToolHandler
{
    /// <summary>The calling user, chat and limits, or a failed result when the call is not from a saved chat.</summary>
    protected static (User User, JObject Parent, SessionLimits Limits, JObject Fail) Caller(ToolExecutionContext ctx)
    {
        User user = ctx.Session?.User;
        if (user is null || string.IsNullOrEmpty(ctx.ThreadId))
        {
            return (null, null, null, Failed("Sessions can only be used from a saved chat."));
        }
        JObject parent = ThreadStorageService.GetThread(user, ctx.ThreadId);
        return parent is null
            ? (null, null, null, Failed("This chat could not be found."))
            : (user, parent, SessionLimits.From(SettingsService.GetMergedSettings(user)), null);
    }

    /// <summary>A failed result.</summary>
    protected static JObject Failed(string error) => new() { ["success"] = false, ["error"] = error };

    /// <summary>The session a call names (its <c>id</c>), only when the calling chat started it; or the failed result to give back.</summary>
    protected static (JObject Child, JObject Fail) Child(User user, JObject parent, ToolExecutionContext ctx)
    {
        string id = ctx.Args["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id))
        {
            return (null, Failed("id is required."));
        }
        JObject child = SessionManager.OwnedChild(user, parent["id"].ToString(), id);
        return child is null ? (null, Failed($"There is no session '{id.Trim()}' started by this chat. Use session_list to see its sessions.")) : (child, null);
    }

    /// <summary>A text argument, trimmed, or null when it is missing or blank.</summary>
    protected static string Text(ToolExecutionContext ctx, string name)
    {
        string value = ctx.Args[name]?.ToString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

/// <summary>Starts a session: a chat of its own that works on one job in the background and reports back.</summary>
public sealed class SessionStartTool : SessionToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.SessionStart;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Exec;

    /// <inheritdoc/>
    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, JObject parent, SessionLimits limits, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return fail;
        }
        string title = Text(ctx, "title");
        string prompt = Text(ctx, "prompt");
        if (title is null || prompt is null)
        {
            return Failed("title and prompt are required.");
        }
        if (prompt.Length > SessionLimits.MaxPromptChars)
        {
            return Failed($"The prompt is too long ({prompt.Length} characters; at most {SessionLimits.MaxPromptChars}).");
        }
        string model = Text(ctx, "model") ?? ctx.ModelId;
        if (string.IsNullOrEmpty(model) || await LLMModelLookup.GetByIdAsync(model, ctx.Session) is null)
        {
            return Failed($"There is no model '{model}'. Leave model out to use this chat's own.");
        }
        List<string> tools = ctx.Args["tools"] is JArray list ? [.. list.Select(t => t.ToString().Trim())] : null;
        (JObject child, string error) = SessionManager.CreateChild(user, parent, ctx.ModelId, new SessionManager.SpawnRequest(title, Text(ctx, "assistantId"), model, Text(ctx, "approvalMode"), tools),
            limits, SettingsService.GetMergedSettings(user));
        if (child is null)
        {
            return Failed(error);
        }
        string childId = child["id"].ToString();
        if (!string.IsNullOrEmpty(ctx.CallId))
        {
            ThreadStorageService.Mutate(user, childId, t =>
            {
                t[SessionFields.SpawnedByCall] = ctx.CallId;
                return true;
            });
        }
        string status = BackgroundTurnService.Start(ctx.Session, childId, prompt, limits);
        return new JObject
        {
            ["success"] = true,
            ["id"] = childId,
            ["title"] = child["title"],
            ["status"] = status,
            ["note"] = "The session is working in the background. Use session_wait to wait for its report, or session_read to look at it.",
        };
    }
}

/// <summary>Sends a message to a session. A session that is working takes it when its turn ends; an idle or finished one starts a new turn from it.</summary>
public sealed class SessionSendTool : SessionToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.SessionSend;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Exec;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, JObject parent, SessionLimits limits, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return Task.FromResult(fail);
        }
        (JObject child, JObject missing) = Child(user, parent, ctx);
        if (child is null)
        {
            return Task.FromResult(missing);
        }
        string message = Text(ctx, "message");
        if (message is null)
        {
            return Task.FromResult(Failed("message is required."));
        }
        if (message.Length > SessionLimits.MaxPromptChars)
        {
            return Task.FromResult(Failed($"The message is too long (at most {SessionLimits.MaxPromptChars} characters)."));
        }
        string childId = child["id"].ToString();
        if (SessionManager.StatusOf(child) == SessionStatuses.Archived)
        {
            return Task.FromResult(Failed("That session is archived."));
        }
        // A working session takes the message when its turn ends; one that is not working starts a new turn from it. Starting can find the session just taken up by
        // another call, in which case the message waits like the first kind.
        JObject working = new() { ["success"] = true, ["id"] = childId, ["status"] = SessionManager.StatusOf(child), ["note"] = "The session is working. It will read your message when its current turn ends." };
        if (BackgroundTurnService.TryEnqueue(user.UserID, childId, message))
        {
            return Task.FromResult(working);
        }
        string status = BackgroundTurnService.Start(ctx.Session, childId, message, limits);
        if (status is null)
        {
            return Task.FromResult(BackgroundTurnService.TryEnqueue(user.UserID, childId, message) ? working : Failed("The session could not take the message; try again."));
        }
        return Task.FromResult(new JObject { ["success"] = true, ["id"] = childId, ["status"] = status });
    }
}

/// <summary>Lists the sessions the calling chat started, with how each is doing.</summary>
public sealed class SessionListTool : SessionToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.SessionList;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, JObject parent, _, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return Task.FromResult(fail);
        }
        SessionManager.Reconcile(user, id => BackgroundTurnService.IsLive(user.UserID, id) || GenerationCancellationRegistry.IsActive(id));
        JArray sessions = [.. SessionManager.ChildrenOf(user, parent["id"].ToString())
            .Select(row => ThreadStorageService.GetThread(user, row["id"].ToString()))
            .Where(t => t is not null)
            .Select(SessionManager.Describe)];
        return Task.FromResult(new JObject { ["success"] = true, ["sessions"] = sessions });
    }
}

/// <summary>Reads a session: how it is doing, its report, and the end of its conversation.</summary>
public sealed class SessionReadTool : SessionToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.SessionRead;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, JObject parent, _, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return Task.FromResult(fail);
        }
        (JObject child, JObject missing) = Child(user, parent, ctx);
        if (child is null)
        {
            return Task.FromResult(missing);
        }
        int last = ctx.Args["last"]?.Type == JTokenType.Integer ? ctx.Args["last"].Value<int>() : 6;
        JObject result = SessionManager.Describe(child);
        result["success"] = true;
        result["recent"] = SessionManager.LastMessages(child, last);
        // The model has the report now, so it is not given it again at the next turn.
        SessionManager.ClearNotice(user, parent["id"].ToString(), child["id"].ToString());
        return Task.FromResult(result);
    }
}

/// <summary>Waits for a session to finish, up to a limit, and returns its report. Returns early when the session is waiting for the user, since nothing but the user can clear that.</summary>
public sealed class SessionWaitTool : SessionToolBase
{
    /// <summary>How often the session's state is looked at while waiting.</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(400);

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.SessionWait;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <summary>A wait is meant to outlast the usual limit: it ends at its own, up to <see cref="SessionLimits.MaxWaitSeconds"/>, plus a little.</summary>
    public override TimeSpan TimeoutFor(JObject toolDef) => TimeSpan.FromSeconds(SessionLimits.MaxWaitSeconds + 30);

    /// <inheritdoc/>
    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, JObject parent, _, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return fail;
        }
        (JObject child, JObject missing) = Child(user, parent, ctx);
        if (child is null)
        {
            return missing;
        }
        int seconds = ctx.Args["timeoutSeconds"]?.Type == JTokenType.Integer ? Math.Clamp(ctx.Args["timeoutSeconds"].Value<int>(), 1, SessionLimits.MaxWaitSeconds) : 120;
        string childId = child["id"].ToString();
        string parentId = parent["id"].ToString();
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        // While this chat waits it is not using one of the user's places, or a session that is itself a session could hold the place its own child needs.
        BackgroundTurnService.SetWaiting(user.UserID, parentId, true);
        try
        {
            while (true)
            {
                child = ThreadStorageService.GetThread(user, childId);
                if (child is null)
                {
                    return Failed("The session no longer exists.");
                }
                string status = SessionManager.StatusOf(child);
                bool stuck = SessionStatuses.IsActive(status) && !BackgroundTurnService.IsLive(user.UserID, childId) && !GenerationCancellationRegistry.IsActive(childId);
                if (stuck)
                {
                    SessionManager.FinishTurn(user, childId, SessionStatuses.Interrupted, "The server stopped before the session finished.");
                    continue;
                }
                if (!SessionStatuses.IsActive(status) || status == SessionStatuses.AwaitingApproval || DateTime.UtcNow >= until)
                {
                    JObject result = SessionManager.Describe(child);
                    result["success"] = true;
                    if (status == SessionStatuses.AwaitingApproval)
                    {
                        result["note"] = "The session is waiting for the user to allow one of its tool calls, which only the user can do (by opening the session). Carry on with something else, or stop it with session_stop.";
                    }
                    else if (SessionStatuses.IsActive(status))
                    {
                        result["note"] = $"The session was still working after {seconds} seconds. Wait again, or read it with session_read.";
                    }
                    else
                    {
                        SessionManager.ClearNotice(user, parentId, childId);
                    }
                    return result;
                }
                await Task.Delay(PollEvery, ctx.Ct);
            }
        }
        finally
        {
            BackgroundTurnService.SetWaiting(user.UserID, parentId, false);
        }
    }
}

/// <summary>Stops a session that is working (what it had is kept, and its status becomes idle).</summary>
public sealed class SessionStopTool : SessionToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.SessionStop;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Write;

    /// <inheritdoc/>
    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, JObject parent, _, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return fail;
        }
        (JObject child, JObject missing) = Child(user, parent, ctx);
        if (child is null)
        {
            return missing;
        }
        string childId = child["id"].ToString();
        bool stopped = await BackgroundTurnService.StopAsync(user, childId);
        ToolApprovals.Pending.CancelScope(user.UserID, childId);
        JObject now = ThreadStorageService.GetThread(user, childId);
        return new JObject { ["success"] = true, ["id"] = childId, ["wasRunning"] = stopped, ["status"] = SessionManager.StatusOf(now) };
    }
}

/// <summary>Puts a session away: stops it, hides it from the chat list, and frees its place among the chat's sessions. It stays on disk.</summary>
public sealed class SessionArchiveTool : SessionToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.SessionArchive;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Write;

    /// <inheritdoc/>
    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, JObject parent, _, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return fail;
        }
        (JObject child, JObject missing) = Child(user, parent, ctx);
        if (child is null)
        {
            return missing;
        }
        string childId = child["id"].ToString();
        await BackgroundTurnService.StopAsync(user, childId);
        ToolApprovals.Pending.CancelScope(user.UserID, childId);
        SessionManager.Archive(user, childId);
        SessionManager.ClearNotice(user, parent["id"].ToString(), childId);
        return new JObject { ["success"] = true, ["id"] = childId, ["status"] = SessionStatuses.Archived };
    }
}
