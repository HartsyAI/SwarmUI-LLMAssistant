using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>What a turn is made of, ready to run: the request, the tools it offers and the assistant it is for.</summary>
/// <param name="Input">The request to send, with its history, system prompt, parameters and tools.</param>
/// <param name="EnabledTools">The tools the request offers (empty when tool calling is off).</param>
/// <param name="AssistantId">The assistant the chat belongs to.</param>
/// <param name="Thread">The chat as it was read for this turn (after any summarizing).</param>
/// <param name="WouldCompact">For a dry run: whether the reply would summarize the start of the chat first (which a dry run does not do).</param>
/// <param name="ContextWindow">The model's window in tokens, or 0 when it is not known.</param>
/// <param name="Addendum">What the chat-model prompt style added to the system prompt (see <see cref="LlmAddendumService"/>), or null.</param>
public sealed record PreparedTurn(ExtendedLLMInput Input, List<JObject> EnabledTools, string AssistantId, JObject Thread, bool WouldCompact = false, int ContextWindow = 0, LlmAddendum Addendum = null);

/// <summary>Builds the request for one reply in a chat: the instructions, the tools, the history and the parameters. One builder for a reply on a chat socket and for a
/// session working in the background, so the two never differ in what a model is told or offered (skills, code tools, project instructions, summaries).</summary>
public static class TurnSetupService
{
    /// <summary>Builds the request for the next reply in <paramref name="thread"/>. <paramref name="rawInput"/> carries the caller's choices
    /// (<c>model</c>, <c>instructionId</c>, <c>forceToolId</c>, <c>temperature</c>, <c>maxTokens</c>, <c>seed</c>, <c>loops</c>, <c>enableThinking</c>,
    /// <c>maxContextMessages</c>), all optional. <paramref name="notify"/> receives the progress frames of a summary made first (null: none are sent).
    /// Returns null when <paramref name="ct"/> was cancelled during setup. With <paramref name="dryRun"/> nothing is changed and no model is called: a conversation that is due to be
    /// summarized is not (the result says it would be), so the request is built exactly as it would be, from the chat as it is.</summary>
    public static async Task<PreparedTurn> PrepareAsync(Session session, string threadId, JObject thread, JObject rawInput, Func<JObject, Task> notify, CancellationToken ct, bool dryRun = false)
    {
        string model = rawInput["model"]?.ToString();
        string instructionId = rawInput["instructionId"]?.ToString();
        string forceToolId = rawInput["forceToolId"]?.ToString();
        double temperature = rawInput["temperature"]?.Value<double>() ?? -1;
        int maxTokens = rawInput["maxTokens"]?.Value<int>() ?? -1;
        long seed = rawInput["seed"]?.Value<long>() ?? -1;
        int loops = ChatEndpoints.ReadPositiveInt(rawInput, "loops");
        // The composer's Thinking toggle: true or false, or absent for the model's template default.
        bool? enableThinking = ChatEndpoints.ReadOptionalBool(rawInput, "enableThinking");
        // Assistant is locked to the thread (set at creation). Per-message override would make history
        // confusing; assistant identity is part of the thread's identity.
        string assistantId = thread["assistantId"]?.ToString();
        JObject settings = SettingsService.GetMergedSettings(session.User);
        if (string.IsNullOrEmpty(assistantId))
        {
            assistantId = AssistantService.GetActiveAssistantId(settings, session.User);
        }
        // Resolve model facts once so per-model instruction variants can pick the right text.
        LLMModelInfo modelInfo = await LLMModelLookup.GetByIdAsync(model, session);
        string systemPrompt = ChatEndpoints.ResolveInstructionForRequest(instructionId, assistantId, settings, session.User, modelInfo);
        // Attach the tools enabled for this assistant, unless tool calling is switched off for this thread or assistant (see EffectiveToolsEnabled).
        bool toolsOn = ChatEndpoints.EffectiveToolsEnabled(thread, assistantId, settings, session.User);
        // The chat-model style for the model answering this turn is added right after the instruction, before anything else is appended (see LlmAddendumService).
        systemPrompt = LlmAddendumService.Apply(systemPrompt, session.User, settings, assistantId, instructionId, model, modelInfo, toolsOn, out LlmAddendum addendum);
        List<JObject> enabledTools = toolsOn
            ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
            : [];
        // A chat in code mode brings the code tools along (see CodeModeService).
        enabledTools = CodeModeService.WithCodeTools(enabledTools, thread, settings, session.User);
        // A chat with skills to offer gets the skill tool and a one-line-per-skill index (see PromptAssetService).
        enabledTools = PromptAssetService.WithSkillTool(enabledTools, thread, settings, session.User, toolsOn);
        // A session the assistant started can use only the tools it was given, whatever its assistant lists, and is told what it is for.
        enabledTools = SessionManager.NarrowTools(enabledTools, thread);
        systemPrompt = SessionManager.AppendSessionNote(systemPrompt, thread);
        systemPrompt = PromptAssetService.AppendIndex(systemPrompt, enabledTools, session.User, thread);
        // A chat with a code tool also follows the workspace's own AGENTS.md (or CLAUDE.md).
        systemPrompt = ProjectInstructionsService.Append(systemPrompt, session.User, thread, enabledTools);
        // A conversation near the model's window is summarized first, so this request fits.
        bool wouldCompact = false;
        if (dryRun)
        {
            wouldCompact = ChatEndpoints.CompactionDue(thread, settings, modelInfo, systemPrompt);
        }
        else
        {
            thread = await ChatEndpoints.AutoCompactIfDueAsync(notify, session, threadId, thread, model, modelInfo, settings, systemPrompt, ct);
        }
        // Build LLM input from the active branch (truncated to maxContextMessages).
        List<ChatMessageData> history = ChatEndpoints.BuildHistoryFromThread(thread, settings, rawInput);
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory(history, systemPrompt, model);
        input.RequestSession = session;
        input.ConversationId = ChatEndpoints.ThreadConversationId(threadId);
        input.EnableThinking = enableThinking;
        JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
        ChatEndpoints.ApplyParameters(input, resolvedParams, temperature, maxTokens, seed, loops);
        ChatEndpoints.AttachTools(input, enabledTools, session, assistantId, forceToolId);
        return ct.IsCancellationRequested ? null : new PreparedTurn(input, enabledTools, assistantId, thread, wouldCompact, ContextWindowService.Resolve(modelInfo, settings), addendum);
    }
}
