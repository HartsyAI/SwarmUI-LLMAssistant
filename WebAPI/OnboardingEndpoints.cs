using Hartsy.Extensions.LLMAssistant.Services.Onboarding;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>The first-run guide and the starter set. Everything is the calling user's own: there is no shared state and no way to name another user.</summary>
public static class OnboardingEndpoints
{
    /// <summary>Whether the guide should show, and what it shows. Request: <c>{ checks? (default true), refresh? }</c>. Reply: <c>{success, status (new | pending | skipped | completed), show}</c> and,
    /// with <c>checks</c>, <c>canChat</c>, <c>checks[{id (backend | model | tools | embeddings), title, status (ok | warn | fail | info), detail, fix?}]</c>, <c>setup{page, hash, canView, canAdd}</c> and
    /// <c>starter{items[{kind, id, title, installed}], total, installed, missing}</c>. Changes nothing. The only requests it can cause are the backends' own model lists, as the chat's model
    /// list does; <c>refresh</c> asks for those again.</summary>
    public static async Task<JObject> LLMAssistantGetOnboarding(Session session, bool checks = true, bool refresh = false)
        => await OnboardingService.ReportAsync(session, checks, refresh, CancellationToken.None);

    /// <summary>Request: <c>{ action }</c>: <c>skip</c> or <c>complete</c> closes the guide, <c>run</c> shows it again, <c>reset</c> makes the user new to it (it then shows only while they have no
    /// chats). Doing one twice changes nothing, and none touches the starter items. Reply: <c>{success, status, show}</c>.</summary>
    public static Task<JObject> LLMAssistantSetOnboarding(Session session, string action)
        => Task.FromResult(OnboardingService.Apply(session.User, action));

    /// <summary>Adds the starter assistants, prompt snippets and example chats the user does not have yet, as their own items. Nothing existing is changed. Reply:
    /// <c>{success, added[{kind, id, title}], skipped[{kind, id, title, code (exists | permission | error), reason}], starter{}}</c>. Assistants and snippets need <c>llm_settings</c>, chats
    /// <c>llm_threads</c>; what the user may not make is reported as skipped.</summary>
    public static Task<JObject> LLMAssistantInstallStarterSet(Session session)
        => Task.FromResult(OnboardingService.InstallStarterSet(session.User));
}
