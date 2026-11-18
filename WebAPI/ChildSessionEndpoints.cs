using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>What the chat list needs to manage the sessions the assistant started. Starting, messaging and reading them is the assistant's own (the <c>session_*</c> tools); the user
/// stops a session with the Stop button (<c>LLMAssistantStopGeneration</c>), answers a call it is waiting on with <c>LLMAssistantResolveToolApproval</c>, and talks to it by opening it.</summary>
public static class ChildSessionEndpoints
{
    /// <summary>Puts a session away: stops it, keeps it on disk and hides it from the chat list. Archiving a chat that is not a session is refused, so this is not a second way to
    /// hide any chat.</summary>
    public static async Task<JObject> LLMAssistantArchiveSession(Session session, string threadId)
    {
        JObject thread = string.IsNullOrWhiteSpace(threadId) ? null : ThreadStorageService.GetThread(session.User, threadId);
        if (!SessionManager.IsSession(thread))
        {
            return new JObject { ["success"] = false, ["error"] = "That chat is not a session." };
        }
        await BackgroundTurnService.StopAsync(session.User, threadId);
        ToolApprovals.Pending.CancelScope(session.User.UserID, threadId);
        SessionManager.Archive(session.User, threadId);
        SessionManager.ClearNotice(session.User, thread[SessionFields.Parent].ToString(), threadId);
        return new JObject { ["success"] = true };
    }
}
