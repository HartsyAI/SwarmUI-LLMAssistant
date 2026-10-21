using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using HartsyInference.Engine.Requests;
using HartsyInference.Tools;
using ToolDefinition = HartsyInference.Engine.Requests.ToolDefinition;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Who a turn is for and where it is saved. The three injection points (<see cref="ResolveProvider"/>, <see cref="Dispatch"/>,
/// <see cref="Persist"/>) default to the live host and exist so the runner can be driven in tests.</summary>
public sealed class ToolTurnContext
{
    /// <summary>The caller, for tool permissions and the thread.</summary>
    public Session Session;

    /// <summary>The thread the reply belongs to; null for a turn that is not saved (voice).</summary>
    public string ThreadId;

    /// <summary>The assistant whose tool allowlist applies.</summary>
    public string AssistantId;

    /// <summary>The client's id for the reply message, when it chose one.</summary>
    public string ClientMessageId;

    /// <summary>Compare-mode column index; -1 for a single reply.</summary>
    public int Lane = -1;

    /// <summary>The user turn a compare reply hangs off, so lanes are siblings.</summary>
    public string ParentMessageId;

    /// <summary>Tags the reply as part of a compare group.</summary>
    public string CompareGroupId;

    /// <summary>The compute device the model ran on, stored in the reply's meta.</summary>
    public string DeviceLabel;

    /// <summary>Whether the reply becomes the thread's active leaf (only lane 0 does in compare mode).</summary>
    public bool SetActiveLeaf = true;

    /// <summary>Model rounds allowed for one turn.</summary>
    public int MaxRounds = ToolLoop.DefaultMaxRounds;

    /// <summary>Provider lookup for the request (defaults to <see cref="LLMDispatcher.GetProvider"/>).</summary>
    public Func<ExtendedLLMInput, Task<ILLMProvider>> ResolveProvider;

    /// <summary>Runs one tool call and returns its result object (defaults to <see cref="ToolExecutorService.ExecuteTool"/>).</summary>
    public Func<NativeToolCall, CancellationToken, Task<JObject>> Dispatch;

    /// <summary>Saves the finished reply (defaults to the thread store; a turn without a thread saves nothing).</summary>
    public Action<JObject> Persist;
}

/// <summary>What a finished turn produced: its visible text, why it ended, whether the round limit cut it, and its tool events.</summary>
/// <remarks><see cref="SpokenText"/> is the visible text alone (no reasoning), for speech; <see cref="Rounds"/> is each round's
/// <c>{text, toolCallIds}</c>, in order; <see cref="RoundLimit"/> is the loop's limit detail (<c>max_rounds=8</c>) when it cut the turn;
/// <see cref="Error"/> is the provider's message when the turn stopped on an error.</remarks>
public sealed record ToolTurnOutcome(string FullText, string StopReason, bool Truncated, string Reason, JArray ToolEvents, string SpokenText = null, JArray Rounds = null,
    string RoundLimit = null, string Error = null)
{
    /// <summary>Whether the engine stopped on an error before any text or tool call: its message is then the whole reply.</summary>
    public bool IsEngineFailure => StopReason == "error" && FullText.Length == 0 && ToolEvents.Count == 0 && !string.IsNullOrEmpty(Error);
}

/// <summary>Where a turn's events go: a chat socket, a voice socket, or a collector. <see cref="IsOpen"/> stops forwarding when the
/// client has gone; the turn still finishes and saves.</summary>
public interface IToolTurnSink
{
    /// <summary>False once the client is gone; nothing more is forwarded.</summary>
    bool IsOpen { get; }

    /// <summary>Visible text.</summary>
    Task Chunk(string text);

    /// <summary>A new model round begins (round 2 and later).</summary>
    Task Iteration(int round);

    /// <summary>Reasoning text, as the model's think block streams it.</summary>
    Task Reasoning(string text);

    /// <summary>The think block ended (before visible text, a call, or the turn's end).</summary>
    Task ReasoningEnd();

    /// <summary>A backend status phase (for example <c>queued</c>).</summary>
    Task Status(string phase);

    /// <summary>A tool call the model made: <c>{id, name, arguments}</c>.</summary>
    Task ToolCall(JObject call);

    /// <summary>A tool's result: <c>{id, name, result}</c>.</summary>
    Task ToolResult(JObject result);

    /// <summary>A call the turn never ran (the round limit, an error or a stop), with its stand-in result: <c>{id, name, result}</c>.</summary>
    Task ToolNotRun(JObject result);

    /// <summary>The turn ended.</summary>
    Task Done(ToolTurnOutcome outcome);
}

/// <summary>The one chat loop: streams each model round through the provider, runs the tool calls it makes through the engine's
/// <see cref="ToolLoop"/>, forwards events to a sink and saves the reply. A turn with no tools is a single round with the same events.</summary>
public static class ToolTurnRunner
{
    /// <summary>Tool markup a model may still write as text; stripped from the saved reply's <c>content</c>.</summary>
    private static readonly Regex ToolTagRegex = new(@"<tool_call>[\s\S]*?</tool_call>|<tool_result\b[^>]*>[\s\S]*?</tool_result>", RegexOptions.Compiled);

    /// <summary>Runs one turn and returns its outcome. Sends the sink's events as they happen and saves the reply, including a partial
    /// reply when the turn is stopped.</summary>
    public static async Task<ToolTurnOutcome> RunAsync(ExtendedLLMInput input, ToolTurnContext ctx, IToolTurnSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(sink);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel, ct);
        DateTime startTime = DateTime.UtcNow;
        ILLMProvider provider = await (ctx.ResolveProvider ?? LLMDispatcher.GetProvider)(input);
        ToolTurnContext resolved = WithDefaults(ctx, input.Model);
        StringBuilder fullText = new();
        StringBuilder spokenText = new();
        ThinkSpanFilter speech = new();
        StringBuilder roundText = new();
        List<string> roundCallIds = [];
        JArray toolEvents = [];
        JArray rounds = [];
        string stopReason = null;
        bool truncated = false;
        string roundLimit = null;
        string error = null;
        Exception failure = null;
        bool pendingIteration = false;
        bool inReasoning = false;
        int round = 1;
        bool runsTools = input.Tools is { Count: > 0 };
        ReasoningReplay replay = new();
        async Task AppendText(string text)
        {
            fullText.Append(text);
            roundText.Append(text);
            spokenText.Append(speech.Feed(text));
            await sink.Chunk(text ?? "");
        }
        void CloseRound()
        {
            if (roundText.Length > 0 || roundCallIds.Count > 0)
            {
                rounds.Add(new JObject { ["text"] = roundText.ToString(), ["toolCallIds"] = new JArray(roundCallIds) });
            }
            roundText.Clear();
            roundCallIds.Clear();
        }
        try
        {
            IAsyncEnumerable<TextChunk> chunks = runsTools
                ? ToolLoop.Create(MakeRoundStream(input, provider, replay), SeedRequest(input), new HostToolDispatcher(resolved, input), new ToolLoopOptions { MaxRounds = resolved.MaxRounds }).RunAsync(linked.Token)
                : provider.StreamAsync(input, linked.Token);
            await foreach (TextChunk chunk in chunks.WithCancellation(linked.Token))
            {
                if (chunk.Kind is TextChunkKind.Chunk or TextChunkKind.Reasoning or TextChunkKind.NativeToolCall && pendingIteration)
                {
                    pendingIteration = false;
                    CloseRound();
                    round++;
                    await sink.Iteration(round);
                }
                if (chunk.Kind != TextChunkKind.Reasoning && inReasoning)
                {
                    inReasoning = false;
                    fullText.Append("</think>");
                    roundText.Append("</think>");
                    await sink.ReasoningEnd();
                }
                switch (chunk.Kind)
                {
                    case TextChunkKind.Chunk:
                        await AppendText(chunk.Text);
                        break;
                    case TextChunkKind.Result when !runsTools && fullText.Length == 0 && !string.IsNullOrEmpty(chunk.Text):
                        // A reply that streamed no text, reasoning included, is shown and saved from its final result; the tool loop does this for its own rounds.
                        await AppendText(chunk.Text);
                        break;
                    case TextChunkKind.Reasoning:
                        if (!inReasoning)
                        {
                            inReasoning = true;
                            fullText.Append("<think>");
                            roundText.Append("<think>");
                        }
                        fullText.Append(chunk.Text);
                        roundText.Append(chunk.Text);
                        replay.Add(chunk.Text ?? "");
                        await sink.Reasoning(chunk.Text ?? "");
                        break;
                    case TextChunkKind.NativeToolCall when chunk.ToolCall is { } call:
                        roundCallIds.Add(call.Id);
                        toolEvents.Add(new JObject { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = LLMMessageMapping.ArgumentsOrRaw(new JValue(call.Arguments)), ["result"] = null });
                        await sink.ToolCall(new JObject { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = LLMMessageMapping.ArgumentsObject(new JValue(call.Arguments)) });
                        break;
                    case TextChunkKind.ToolResult when chunk.ToolCall is { } done:
                        JObject result = ParseObject(chunk.Text);
                        foreach (JObject entry in toolEvents.OfType<JObject>().Where(e => e["id"]?.ToString() == done.Id))
                        {
                            entry["result"] = result;
                        }
                        await sink.ToolResult(new JObject { ["id"] = done.Id, ["name"] = done.Name, ["result"] = result });
                        pendingIteration = true;
                        break;
                    case TextChunkKind.Status when chunk.Status is { Phase: ToolLoop.RoundLimitPhase }:
                        truncated = true;
                        roundLimit = chunk.Text is { } limit && limit.StartsWith(ToolLoop.RoundLimitPrefix, StringComparison.Ordinal)
                            ? limit[ToolLoop.RoundLimitPrefix.Length..]
                            : chunk.Text;
                        break;
                    case TextChunkKind.Status when chunk.Status is { } status:
                        await sink.Status(status.Phase);
                        break;
                    case TextChunkKind.StopReason:
                        stopReason = ToStopReason(chunk.Stop);
                        if (chunk.Stop == StopReason.Error)
                        {
                            error = chunk.Text;
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // A Stop (or the socket closing) landed mid-turn: keep what was produced rather than dropping it.
            stopReason ??= "cancelled";
        }
        catch (Exception ex)
        {
            // A failure mid-turn keeps what ran: it is saved below, then rethrown for the caller to report.
            failure = ex;
            stopReason = "error";
            error = ex.Message;
            Logs.Error($"[LLMAssistant] Tool turn failed: {ex.Message}");
        }
        if (inReasoning)
        {
            inReasoning = false;
            fullText.Append("</think>");
            roundText.Append("</think>");
            await sink.ReasoningEnd();
        }
        CloseRound();
        // A call the loop never ran (the round limit's last call, or a stop) gets a result: the client closes its bubble, and a later
        // turn never sees an orphan tool use.
        foreach (JObject entry in toolEvents.OfType<JObject>().Where(e => e["result"] is null or { Type: JTokenType.Null }))
        {
            JObject stub = new()
            {
                ["success"] = false,
                ["error"] = failure is not null ? "The turn failed before this tool could run."
                    : truncated ? "The round limit was reached before this tool could run." : "The turn was stopped before this tool finished.",
            };
            entry["result"] = stub;
            await sink.ToolNotRun(new JObject { ["id"] = entry["id"]?.ToString(), ["name"] = entry["name"]?.ToString(), ["result"] = stub });
        }
        spokenText.Append(speech.Flush());
        ToolTurnOutcome outcome = new(fullText.ToString(), truncated ? null : stopReason, truncated, truncated ? "max_iterations" : null, toolEvents,
            spokenText.ToString(), rounds, roundLimit, error);
        if (fullText.Length > 0 || toolEvents.Count > 0)
        {
            resolved.Persist(BuildMessage(outcome, input.Model, startTime, ctx, outcome.Truncated ? "tool_round_limit" : outcome.StopReason));
        }
        if (failure is not null)
        {
            // The caller sends the error frame; a done frame after it would replace that error in the chat.
            ExceptionDispatchInfo.Throw(failure);
        }
        if (sink.IsOpen)
        {
            await sink.Done(outcome);
        }
        return outcome;
    }

    /// <summary>The message a finished turn saves. The text is scrubbed of tool markup and lone surrogates.</summary>
    public static JObject BuildMessage(ToolTurnOutcome outcome, string model, DateTime startTime, ToolTurnContext ctx, string stopReason)
    {
        string raw = StripLoneSurrogates(outcome.FullText);
        string clean = ToolTagRegex.Replace(raw ?? "", "").Trim();
        JObject meta = new()
        {
            ["model"] = model ?? "",
            ["genTime"] = Math.Round((DateTime.UtcNow - startTime).TotalSeconds, 2),
            ["truncated"] = outcome.Truncated,
            ["reason"] = outcome.Reason,
            // "length" = the provider cut the reply off at the max-tokens cap rather than a natural stop.
            ["stopReason"] = stopReason,
        };
        if (!string.IsNullOrEmpty(ctx.DeviceLabel))
        {
            meta["device"] = ctx.DeviceLabel;
        }
        if (outcome.Error is not null)
        {
            meta["error"] = outcome.Error;
        }
        JObject message = new()
        {
            ["role"] = "assistant",
            ["content"] = clean,
            ["rawContent"] = raw ?? "",
            ["toolCalls"] = outcome.ToolEvents ?? [],
            ["rounds"] = outcome.Rounds ?? [],
            ["meta"] = meta,
        };
        if (!string.IsNullOrEmpty(ctx.ClientMessageId))
        {
            message["id"] = ctx.ClientMessageId;
        }
        if (!string.IsNullOrEmpty(ctx.CompareGroupId))
        {
            message["groupId"] = ctx.CompareGroupId;
        }
        if (ctx.Lane >= 0)
        {
            message["lane"] = ctx.Lane;
        }
        return message;
    }

    /// <summary>Appends a finished reply to its thread: a compare reply hangs off the user turn that asked, a normal reply goes on the
    /// active leaf. A turn with no thread saves nothing.</summary>
    public static void SaveToThread(Session session, string threadId, ToolTurnContext ctx, JObject message)
    {
        if (session?.User is null || string.IsNullOrEmpty(threadId))
        {
            return;
        }
        try
        {
            if (!string.IsNullOrEmpty(ctx.ParentMessageId))
            {
                ThreadStorageService.AppendChild(session.User, threadId, ctx.ParentMessageId, message, ctx.SetActiveLeaf);
            }
            else
            {
                ThreadStorageService.AppendMessage(session.User, threadId, message);
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Failed to persist assistant message to thread {threadId}: {ex.Message}");
        }
    }

    /// <summary>A copy of <paramref name="ctx"/> with the live host's dispatch and save filled in where the caller set none.</summary>
    private static ToolTurnContext WithDefaults(ToolTurnContext ctx, string model) => new()
    {
        Session = ctx.Session,
        ThreadId = ctx.ThreadId,
        AssistantId = ctx.AssistantId,
        ClientMessageId = ctx.ClientMessageId,
        Lane = ctx.Lane,
        ParentMessageId = ctx.ParentMessageId,
        CompareGroupId = ctx.CompareGroupId,
        DeviceLabel = ctx.DeviceLabel,
        SetActiveLeaf = ctx.SetActiveLeaf,
        MaxRounds = ctx.MaxRounds,
        ResolveProvider = ctx.ResolveProvider,
        Dispatch = ctx.Dispatch ?? DefaultDispatch(ctx, model),
        Persist = ctx.Persist ?? (message => SaveToThread(ctx.Session, ctx.ThreadId, ctx, message)),
    };

    /// <summary>Runs a call through <see cref="ToolExecutorService"/>; a handler failure becomes the call's error result.</summary>
    private static Func<NativeToolCall, CancellationToken, Task<JObject>> DefaultDispatch(ToolTurnContext ctx, string model) => async (call, cancel) =>
    {
        try
        {
            // The model id is the turn's own model: a handler that starts a nested request (captioning) must stay on it.
            return await ToolExecutorService.ExecuteTool(call.Name, ParseObject(call.Arguments), ctx.Session, ctx.AssistantId, ctx.ThreadId, model, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Error($"[LLMAssistant] Tool {call.Name} threw: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    };

    /// <summary>The engine request the loop starts from: its messages stand for the conversation so far (the round streams use the
    /// extension's own messages), and it carries the offered tools and the forced one.</summary>
    private static TextRequest SeedRequest(ExtendedLLMInput input) => new()
    {
        Messages = [.. input.Messages.Select(LLMMessageMapping.ToSeedTextMessage)],
        Tools = LLMMessageMapping.ToEngineTools(input.Tools),
        ForceToolId = input.ForceToolId,
    };

    /// <summary>One model round: the input's own messages plus whatever the loop appended, through the provider's stream.</summary>
    private static Func<TextRequest, CancellationToken, IAsyncEnumerable<TextChunk>> MakeRoundStream(ExtendedLLMInput input, ILLMProvider provider,
        ReasoningReplay replay)
    {
        int baseCount = input.Messages.Count;
        return (request, token) =>
        {
            List<LLMMessage> appended = replay.Carry([.. request.Messages.Skip(baseCount).Select(LLMMessageMapping.FromAppendedTextMessage)]);
            return provider.StreamAsync(input.CloneForRound(appended, request.ForceToolId), token);
        };
    }

    /// <summary>Carries each round's reasoning into the assistant turn that round made, as a think block, so the next round sees it.</summary>
    private sealed class ReasoningReplay
    {
        /// <summary>The reasoning of each finished round that made calls, in order.</summary>
        private readonly List<string> _callRounds = [];

        /// <summary>The reasoning of the round being streamed.</summary>
        private readonly StringBuilder _open = new();

        /// <summary>Whether a round has streamed, so the open round has finished by the next call of the stream.</summary>
        private bool _streamed;

        /// <summary>Adds streamed reasoning to the open round.</summary>
        public void Add(string text) => _open.Append(text);

        /// <summary>Closes the round that just finished, then prefixes each assistant turn that made calls with its round's think block.</summary>
        public List<LLMMessage> Carry(List<LLMMessage> appended)
        {
            if (_streamed)
            {
                _callRounds.Add(_open.ToString());
                _open.Clear();
            }
            _streamed = true;
            int call = 0;
            foreach (LLMMessage message in appended)
            {
                if (message.Role != LLMRoles.Assistant || message.ToolCalls is not { Count: > 0 })
                {
                    continue;
                }
                string thought = call < _callRounds.Count ? _callRounds[call] : "";
                call++;
                if (thought.Length > 0)
                {
                    message.Content = "<think>" + thought + "</think>" + message.Content;
                }
            }
            return appended;
        }
    }

    /// <summary>The wire name of a stop reason; null for a natural stop.</summary>
    private static string ToStopReason(StopReason? stop) => stop switch
    {
        StopReason.Length => "length",
        StopReason.Cancelled => "cancelled",
        StopReason.Error => "error",
        StopReason.ToolCall => "tool_call",
        _ => null,
    };

    /// <summary>Parses a JSON object, or an empty one for blank or malformed text.</summary>
    private static JObject ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JObject();
        }
        try
        {
            return JObject.Parse(json);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new JObject();
        }
    }

    /// <summary>Removes unpaired UTF-16 surrogates that would fail to encode when the thread is saved; valid pairs are kept.</summary>
    private static string StripLoneSurrogates(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c))
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Runs the engine's tool calls through the host: the dispatch hook when set, else the executor.</summary>
    private sealed class HostToolDispatcher(ToolTurnContext ctx, ExtendedLLMInput input) : IToolDispatcher
    {
        /// <inheritdoc/>
        public IReadOnlyList<ToolDefinition> Definitions { get; } = LLMMessageMapping.ToEngineTools(input.Tools);

        /// <inheritdoc/>
        public async Task<string> InvokeAsync(NativeToolCall call, CancellationToken cancel)
        {
            // Stopped: the calls not yet run never reach the executor, so none is dispatched or charged to its rate budget.
            cancel.ThrowIfCancellationRequested();
            if (!LLMMessageMapping.TryParseArguments(call.Arguments, out _, out string problem))
            {
                // A call whose arguments did not parse never runs; the model gets the error and can call again.
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = $"The arguments for {call.Name} were not valid JSON ({problem}). Call the tool again with its arguments as one JSON object.",
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            JObject result = await ctx.Dispatch(call, cancel);
            return (result ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
