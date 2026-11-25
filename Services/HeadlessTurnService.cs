using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>A turn run by one request that waits for the answer, for scripts and other programs: a message in, the finished reply (and what its tools did) out as JSON. It is the same turn as in
/// the chat, built by the same <see cref="TurnSetupService"/> (so skills, tools, project instructions and summaries behave identically) and kept in a chat, so a caller can carry on a
/// conversation by passing the <c>threadId</c> back and the user can open it in the interface afterwards. It differs in three ways, all for a caller nobody is watching:
/// <list type="bullet">
/// <item><b>Nobody can approve.</b> A call that would need the user's approval is refused with that reason, so a script never hangs on a prompt. The default mode is Ask (reads run, anything that
/// changes something is refused); <c>auto</c> lets everything run and must be asked for, and it is not saved onto the chat, so the interface does not later run in Auto because a script did.</item>
/// <item><b>A dry run</b> (<c>dryRun: true</c>) builds the request exactly as a real turn would and reports on it (the model, the tools offered, the size of the prompt, whether the chat is due to be
/// summarized) without calling the model, saving anything or changing the chat.</item>
/// <item><b>One reply at a time per chat.</b> A chat with a reply in flight is refused rather than interleaved; a session still working in the background is stopped first, as for a message typed in
/// the interface.</item>
/// </list></summary>
public static class HeadlessTurnService
{
    /// <summary>The model's reasoning, which is not part of the answer.</summary>
    private static readonly Regex ThinkBlock = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled);

    /// <summary>The most characters of one tool result returned in a reply; a longer one is cut (the whole result is in the chat).</summary>
    public const int MaxResultChars = 20_000;

    /// <summary>Runs (or, with <c>dryRun</c>, previews) a turn. Never throws: a failure is <c>{success: false, error}</c>, with the <c>threadId</c> when a chat was made or used, because a reply
    /// that failed part-way has already saved what it had.</summary>
    public static async Task<JObject> RunAsync(Session session, JObject request, CancellationToken ct)
    {
        string message = request["message"]?.ToString();
        if (string.IsNullOrWhiteSpace(message))
        {
            return Fail("message is required.");
        }
        User user = session.User;
        bool dryRun = ChatEndpoints.ReadOptionalBool(request, "dryRun") == true;
        string approvalMode = ApprovalPolicy.IsValid((request["approvalMode"]?.ToString() ?? "").Trim().ToLowerInvariant()) ? ApprovalPolicy.Normalize(request["approvalMode"]?.ToString()) : ApprovalModes.Ask;
        JObject settings = SettingsService.GetMergedSettings(user);
        // Settled before a chat is made, so a request naming a document that is not there leaves nothing behind.
        (JArray documents, string documentError) = ChatDocuments.Resolve(user, request[ChatDocuments.Field], DocumentCaps.From(settings));
        if (documentError is not null)
        {
            return Fail(documentError);
        }
        string threadId = request["threadId"]?.ToString()?.Trim();
        JObject thread = null;
        bool created = false;
        string model = null;
        try
        {
            // The chat: the one named, or (for a real turn) a new one. A dry run with no chat is made against an empty one that is never saved.
            if (!string.IsNullOrEmpty(threadId))
            {
                thread = ThreadStorageService.GetThread(user, threadId);
                if (thread is null)
                {
                    return Fail($"Chat '{threadId}' not found.");
                }
            }
            else
            {
                string assistantId = request["assistantId"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(assistantId))
                {
                    assistantId = AssistantService.GetActiveAssistantId(settings, user);
                }
                else if ((settings["assistants"] as JObject)?[assistantId] is null)
                {
                    return Fail($"There is no assistant '{assistantId}'.");
                }
                if (dryRun)
                {
                    thread = new JObject { ["id"] = "", ["assistantId"] = assistantId, ["messages"] = new JArray() };
                }
                else
                {
                    // The model is settled before the chat is made, so a request that has none to use leaves nothing behind.
                    model = await ResolveModelAsync(session, request["model"]?.ToString(), null, settings);
                    if (model is null)
                    {
                        return Fail(NoModelMessage);
                    }
                    thread = ThreadStorageService.CreateThread(user, assistantId, TitleOf(message));
                    // The title is the message's own words; a generated one would need a socket and a second model call.
                    thread = ThreadStorageService.Mutate(user, thread["id"].ToString(), t =>
                    {
                        ThreadStorageService.MarkTitleClaimed(t);
                        return true;
                    });
                    threadId = thread["id"].ToString();
                    created = true;
                }
            }
            // Never looser than what the chat itself was saved with: a script cannot turn a chat the user keeps in Plan into one that changes things.
            if (thread["approvalMode"]?.ToString() is { Length: > 0 } saved)
            {
                approvalMode = ApprovalPolicy.Stricter(saved, approvalMode);
            }
            model = model ?? await ResolveModelAsync(session, request["model"]?.ToString(), thread, settings);
            if (model is null)
            {
                return Fail(NoModelMessage);
            }
            JObject options = (JObject)request.DeepClone();
            options["model"] = model;
            if (dryRun)
            {
                return await DryRunAsync(session, thread, WithPendingMessage(thread, message, documents), options, approvalMode, ct);
            }
            // A reply in flight (someone is using the chat in the interface) is not interleaved with; a session working in the background is stopped first, as for a typed message.
            if (BackgroundTurnService.IsLive(user.UserID, threadId))
            {
                await BackgroundTurnService.StopAsync(user, threadId);
            }
            else if (GenerationCancellationRegistry.IsActive(threadId))
            {
                return Fail("A reply is already being written for this chat. Wait for it to finish, or stop it first.", threadId);
            }
            SessionManager.DeliverReports(user, threadId);
            JObject userMessage = new() { ["role"] = Roles.User, ["content"] = message };
            if (documents is not null)
            {
                userMessage[ChatDocuments.Field] = documents;
            }
            if (ThreadStorageService.AppendMessage(user, threadId, userMessage) is null)
            {
                return Fail($"Chat '{threadId}' not found.");
            }
            thread = ThreadStorageService.GetThread(user, threadId);
            return await RunTurnAsync(session, thread, threadId, options, approvalMode, created, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Warning($"[LLMAssistant] A headless turn failed: {ex.Message}");
            return Fail(ex.Message, threadId);
        }
    }

    /// <summary>What a request is told when no backend offers a model.</summary>
    private const string NoModelMessage = "No LLM model is available. Add a backend under Server > Backends and make sure it advertises at least one model.";

    /// <summary>The model to use: the one asked for, else the chat's (for a session), else the user's preferred one, else the first any backend offers. Null when there is none.</summary>
    private static async Task<string> ResolveModelAsync(Session session, string asked, JObject thread, JObject settings)
    {
        foreach (string candidate in new[] { asked, thread?[SessionFields.Model]?.ToString(), settings["preferredModel"]?.ToString() })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }
        return (await LLMModelLookup.GetFirstAvailableAsync(session))?.Id;
    }

    /// <summary>A short title for a chat made by a request: the start of its first message, on one line.</summary>
    public static string TitleOf(string message)
    {
        string line = Regex.Replace(message ?? "", @"\s+", " ").Trim();
        if (line.Length > 60)
        {
            line = line[..60].TrimEnd() + "…";
        }
        return line.Length == 0 ? "Request" : line;
    }

    /// <summary>A copy of the chat with the message added as the next user message, as it would be when the real turn runs. Nothing is saved, and the chat itself is not touched.</summary>
    public static JObject WithPendingMessage(JObject thread, string message, JArray documents = null)
    {
        JObject copy = (JObject)thread.DeepClone();
        ThreadStorageService.EnsureTreeShape(copy);
        JArray messages = copy["messages"] as JArray ?? [];
        string leaf = copy["activeLeafId"]?.ToString();
        string id = "pending-" + Guid.NewGuid().ToString("N");
        JObject pending = new() { ["id"] = id, ["role"] = Roles.User, ["content"] = message, ["parentId"] = string.IsNullOrEmpty(leaf) ? JValue.CreateNull() : new JValue(leaf) };
        if (documents is not null)
        {
            pending[ChatDocuments.Field] = documents.DeepClone();
        }
        messages.Add(pending);
        copy["messages"] = messages;
        copy["activeLeafId"] = id;
        return copy;
    }

    /// <summary>What a reply's text is: without the model's reasoning.</summary>
    public static string CleanResponse(string text) => ThinkBlock.Replace(text ?? "", "").Trim();

    /// <summary>A tool result as it goes back to the caller: whole, unless it is very long.</summary>
    public static JToken CapResult(JToken result)
    {
        string text = result?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
        return text.Length <= MaxResultChars ? result : new JObject { ["truncated"] = true, ["chars"] = text.Length, ["preview"] = text[..MaxResultChars] };
    }

    /// <summary>Builds the request for the message exactly as a real turn would, and reports on it.</summary>
    private static async Task<JObject> DryRunAsync(Session session, JObject realThread, JObject virtualThread, JObject options, string approvalMode, CancellationToken ct)
    {
        PreparedTurn prepared = await TurnSetupService.PrepareAsync(session, virtualThread["id"]?.ToString() ?? "", virtualThread, options, null, ct, dryRun: true);
        if (prepared is null)
        {
            return Fail("Cancelled.");
        }
        ExtendedLLMInput input = prepared.Input;
        // The messages the model would read after its instructions: the conversation, with the new message last.
        List<LLMMessage> conversation = [.. input.Messages.Where(m => m.Role != LLMRoles.System)];
        long characters = (input.SystemPrompt?.Length ?? 0) + conversation.Sum(m => (long)(m.Content?.Length ?? 0));
        string model = options["model"].ToString();
        LLMModelInfo info = await LLMModelLookup.GetByIdAsync(model, session);
        JArray warnings = [];
        if (info is null)
        {
            warnings.Add($"No backend offers the model '{model}'.");
        }
        if (prepared.WouldCompact)
        {
            warnings.Add("The chat is long enough that the start of it would be summarized first (a model call).");
        }
        if (prepared.ContextWindow > 0 && LlmAddendumService.EstimateTokens(characters) > prepared.ContextWindow)
        {
            warnings.Add("The request is larger than the model's window.");
        }
        if (prepared.EnabledTools.Count > 0 && approvalMode != ApprovalModes.Auto)
        {
            warnings.Add($"Approval mode is {approvalMode}: a tool call that changes something would be refused, because nobody can approve it.");
        }
        return new JObject
        {
            ["success"] = true,
            ["dryRun"] = true,
            ["model"] = model,
            ["modelFound"] = info is not null,
            ["assistantId"] = prepared.AssistantId,
            ["threadId"] = string.IsNullOrEmpty(realThread["id"]?.ToString()) ? null : realThread["id"],
            ["approvalMode"] = approvalMode,
            ["tools"] = new JArray(prepared.EnabledTools.Select(t => t["id"]?.ToString())),
            ["historyMessages"] = conversation.Count,
            ["systemPromptChars"] = input.SystemPrompt?.Length ?? 0,
            // What the chat-model prompt style added to the system prompt, and its cost (already counted in the two figures around it).
            ["promptAddendum"] = (prepared.Addendum ?? LlmAddendum.Nothing("")).ToJson(),
            ["estimatedPromptTokens"] = LlmAddendumService.EstimateTokens(characters),
            ["contextWindow"] = prepared.ContextWindow,
            ["wouldCompact"] = prepared.WouldCompact,
            ["warnings"] = warnings,
        };
    }

    /// <summary>Runs the turn on the chat (the message is already in it) and collects what happened.</summary>
    private static async Task<JObject> RunTurnAsync(Session session, JObject thread, string threadId, JObject options, string approvalMode, bool created, CancellationToken ct)
    {
        TurnScope scope = TurnScope.Begin(session, threadId, thread);
        // A caller that goes away (closes the request) stops the reply; so does Stop.
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Cts.Token, ct);
        try
        {
            PreparedTurn prepared = await TurnSetupService.PrepareAsync(session, threadId, thread, options, null, linked.Token);
            if (prepared is null)
            {
                scope.MarkIdle();
                return Fail("The reply was stopped before it began.", threadId);
            }
            CollectingSink sink = new();
            ToolTurnContext context = new() { Session = session, ThreadId = threadId, AssistantId = prepared.AssistantId, ApprovalMode = approvalMode };
            ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(prepared.Input, context, sink, linked.Token);
            if (outcome.StopReason == "error")
            {
                scope.MarkFailed(outcome.Error);
                return Fail(string.IsNullOrWhiteSpace(outcome.Error) ? "The model stopped with an error." : $"The model '{prepared.Input.Model}' stopped with an error: {outcome.Error}", threadId);
            }
            JObject reply = new()
            {
                ["success"] = true,
                ["threadId"] = threadId,
                ["response"] = CleanResponse(outcome.SpokenText ?? outcome.FullText),
                ["model"] = prepared.Input.Model,
                // A turn that ended in the ordinary way has no reason of its own from some backends; it stopped because the model finished.
                ["stopReason"] = outcome.Truncated ? "tool_round_limit" : outcome.StopReason ?? "stop",
                ["toolCalls"] = new JArray((outcome.ToolEvents ?? []).OfType<JObject>().Select(e => new JObject
                {
                    ["id"] = e["id"],
                    ["name"] = e["name"],
                    ["arguments"] = e["arguments"],
                    ["result"] = CapResult(e["result"]),
                })),
            };
            if (outcome.Truncated)
            {
                reply["truncated"] = true;
            }
            if (outcome.Usage is { Any: true })
            {
                reply["usage"] = outcome.Usage.ToJson();
            }
            if (created)
            {
                reply["created"] = true;
            }
            return reply;
        }
        catch (OperationCanceledException)
        {
            scope.MarkIdle();
            return Fail("The reply was stopped.", threadId);
        }
        catch (Exception ex)
        {
            // The runner saved what it had before it threw; the chat keeps it.
            scope.MarkFailed(ex.Message);
            Logs.Warning($"[LLMAssistant] A headless turn failed: {ex.Message}");
            return Fail(ex.Message, threadId);
        }
        finally
        {
            scope.End();
        }
    }

    /// <summary>A failed reply, with the chat when there is one (what a failed turn had produced is saved there).</summary>
    private static JObject Fail(string error, string threadId = null)
    {
        JObject reply = new() { ["success"] = false, ["error"] = error };
        if (!string.IsNullOrEmpty(threadId))
        {
            reply["threadId"] = threadId;
        }
        return reply;
    }
}
