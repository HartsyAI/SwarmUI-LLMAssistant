using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Readiness;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>For scripts and for the settings screen: is the setup ready, and run one turn (or preview it) with a single request. Both work as the calling user and need only <c>llm_chat</c>; what a
/// turn may do is still limited by the tool permissions and by the approval mode, and nothing here shows more than the user could see elsewhere.</summary>
public static class HeadlessEndpoints
{
    /// <summary>Checks the whole setup and says what is wrong and what to do about it. Only reads: it connects nothing, loads no model and changes nothing. With <paramref name="deep"/> it also asks
    /// the backends for their model lists again and tries the knowledge library's embedding model, which are requests to other servers. Reply: <c>{success, overall (ok | warn | fail), deep, checkedAt,
    /// checks[{id, title, status (ok | warn | fail | info), detail, fix?}]}</c>.</summary>
    public static async Task<JObject> LLMAssistantReadiness(Session session, bool deep = false)
    {
        try
        {
            return await ReadinessService.ReportAsync(session, deep, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Runs one turn and waits for the whole reply: <c>{message}</c> in, the reply and what the tools did out. Request: <c>{message (required), threadId?, assistantId?, model?,
    /// approvalMode?, dryRun?, temperature?, maxTokens?, seed?, loops?, enableThinking?, instructionId?, forceToolId?, documents?}</c> (<c>documents</c> lists ids from
    /// <see cref="DocumentEndpoints.LLMAssistantUploadChatDocument"/>).
    /// <para>With a <c>threadId</c> it carries on that chat; without one it makes a chat (titled from the message) and returns its id, so the next call can continue it and the user can open it in
    /// the interface. <c>approvalMode</c> is <c>ask</c> (the default: tools that only read run, anything that changes something is refused, because nobody can approve it), <c>plan</c> or <c>auto</c>,
    /// and is never looser than the chat's own saved mode; it is not saved onto the chat. A chat that already has a reply being written is refused; one with a session working in the
    /// background has that stopped first.</para>
    /// <para>Reply: <c>{success, threadId, response, model, stopReason, toolCalls[{id, name, arguments, result}], usage?, truncated?, created?}</c>, or <c>{success: false, error, threadId?}</c> (the
    /// <c>threadId</c> is there when a chat was made or used: what a failed turn produced is saved in it).</para>
    /// <para>With <c>dryRun: true</c> nothing is called or saved: the request is built exactly as a real turn would build it and reported on: <c>{success, dryRun, model, modelFound, assistantId, threadId?,
    /// approvalMode, tools[], historyMessages, systemPromptChars, promptAddendum{applied, styleId, scope, via, reason, chars, estimatedTokens}, estimatedPromptTokens, contextWindow, wouldCompact, warnings[]}</c>. <c>promptAddendum</c> is what the
    /// chat-model prompt style added to the system prompt for this model (<c>applied: false</c> with the reason when nothing was); its tokens are already in the two figures around it.</para></summary>
    public static async Task<JObject> LLMAssistantRunTurn(Session session, JObject rawInput)
        => await HeadlessTurnService.RunAsync(session, rawInput, CancellationToken.None);
}
