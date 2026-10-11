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
    public Session Session { get; init; }

    /// <summary>The thread the reply belongs to; null for a turn that is not saved (voice).</summary>
    public string ThreadId { get; init; }

    /// <summary>The assistant whose tool allowlist applies.</summary>
    public string AssistantId { get; init; }

    /// <summary>The client's id for the reply message, when it chose one.</summary>
    public string ClientMessageId { get; init; }

    /// <summary>Compare-mode column index; -1 for a single reply.</summary>
    public int Lane { get; init; } = -1;

    /// <summary>The user turn a compare reply hangs off, so lanes are siblings.</summary>
    public string ParentMessageId { get; init; }

    /// <summary>Tags the reply as part of a compare group.</summary>
    public string CompareGroupId { get; init; }

    /// <summary>The compute device the model ran on, stored in the reply's meta.</summary>
    public string DeviceLabel { get; init; }

    /// <summary>Whether the reply becomes the thread's active leaf (only lane 0 does in compare mode).</summary>
    public bool SetActiveLeaf { get; init; } = true;

    /// <summary>Model rounds allowed for one turn.</summary>
    public int MaxRounds { get; init; } = ToolLoop.DefaultMaxRounds;

    /// <summary>Provider lookup for the request (defaults to <see cref="LLMDispatcher.GetProvider"/>).</summary>
    public Func<ExtendedLLMInput, Task<ILLMProvider>> ResolveProvider { get; init; }

    /// <summary>Runs one tool call and returns its result object (defaults to <see cref="ToolExecutorService.ExecuteTool"/>).</summary>
    public Func<NativeToolCall, CancellationToken, Task<JObject>> Dispatch { get; init; }

    /// <summary>Saves the finished reply (defaults to the thread store; a turn without a thread saves nothing).</summary>
    public Action<JObject> Persist { get; init; }
}

/// <summary>What a finished turn produced: its visible text, why it ended, whether the round limit cut it, and its tool events.</summary>
public sealed record ToolTurnOutcome(string FullText, string StopReason, bool Truncated, string Reason, JArray ToolEvents);

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

    /// <summary>A backend status phase (for example <c>queued</c>).</summary>
    Task Status(string phase);

    /// <summary>A tool call the model made: <c>{id, name, arguments}</c>.</summary>
    Task ToolCall(JObject call);

    /// <summary>A tool's result: <c>{id, name, result}</c>.</summary>
    Task ToolResult(JObject result);

    /// <summary>The turn ended.</summary>
    Task Done(ToolTurnOutcome outcome);
}

/// <summary>The one chat loop: streams each model round through the provider, runs the tool calls it makes through the engine's
/// <see cref="ToolLoop"/>, forwards events to a sink and saves the reply. A turn with no tools is a single round with the same events.</summary>
public static class ToolTurnRunner
{
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
        ToolTurnContext resolved = WithDefaults(ctx);
        StringBuilder fullText = new();
        JArray toolEvents = [];
        string stopReason = null;
        bool truncated = false;
        bool pendingIteration = false;
        bool inReasoning = false;
        int round = 1;
        try
        {
            IAsyncEnumerable<TextChunk> chunks = input.Tools is { Count: > 0 }
                ? ToolLoop.Create(MakeRoundStream(input, provider), SeedRequest(input), new HostToolDispatcher(resolved, input, linked.Token), new ToolLoopOptions { MaxRounds = resolved.MaxRounds }).RunAsync(linked.Token)
                : provider.StreamAsync(input, linked.Token);
            await foreach (TextChunk chunk in chunks.WithCancellation(linked.Token))
            {
                if (chunk.Kind is TextChunkKind.Chunk or TextChunkKind.Reasoning or TextChunkKind.NativeToolCall && pendingIteration)
                {
                    pendingIteration = false;
                    round++;
                    await sink.Iteration(round);
                }
                if (chunk.Kind != TextChunkKind.Reasoning && inReasoning)
                {
                    inReasoning = false;
                    await Emit(sink, fullText, "</think>");
                }
                switch (chunk.Kind)
                {
                    case TextChunkKind.Chunk:
                        await Emit(sink, fullText, chunk.Text ?? "");
                        break;
                    case TextChunkKind.Reasoning:
                        if (!inReasoning)
                        {
                            inReasoning = true;
                            await Emit(sink, fullText, "<think>");
                        }
                        await Emit(sink, fullText, chunk.Text ?? "");
                        break;
                    case TextChunkKind.NativeToolCall when chunk.ToolCall is { } call:
                        toolEvents.Add(new JObject { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = LLMMessageMapping.ArgumentsObject(new JValue(call.Arguments)), ["result"] = null });
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
                        break;
                    case TextChunkKind.Status when chunk.Status is { } status:
                        await sink.Status(status.Phase);
                        break;
                    case TextChunkKind.StopReason:
                        stopReason = ToStopReason(chunk.Stop);
                        break;
                }
            }
            if (inReasoning) await Emit(sink, fullText, "</think>");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // A Stop (or the socket closing) landed mid-turn: keep what was produced rather than dropping it.
            stopReason ??= "cancelled";
        }
        // A call with no result yet must not reach a later turn as an orphan tool use.
        foreach (JObject entry in toolEvents.OfType<JObject>().Where(e => e["result"] is null or { Type: JTokenType.Null }))
        {
            entry["result"] = new JObject { ["success"] = false, ["error"] = "The turn was stopped before this tool finished." };
        }
        string reason = truncated ? "max_iterations" : null;
        ToolTurnOutcome outcome = new(fullText.ToString(), truncated ? null : stopReason, truncated, reason, toolEvents);
        if (fullText.Length > 0 || toolEvents.Count > 0)
        {
            resolved.Persist(BuildMessage(outcome, input.Model, startTime, ctx, stopReason));
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
        if (!string.IsNullOrEmpty(ctx.DeviceLabel)) meta["device"] = ctx.DeviceLabel;
        JObject message = new()
        {
            ["role"] = "assistant",
            ["content"] = clean,
            ["rawContent"] = raw ?? "",
            ["toolCalls"] = outcome.ToolEvents ?? [],
            ["meta"] = meta,
        };
        if (!string.IsNullOrEmpty(ctx.ClientMessageId)) message["id"] = ctx.ClientMessageId;
        if (!string.IsNullOrEmpty(ctx.CompareGroupId)) message["groupId"] = ctx.CompareGroupId;
        if (ctx.Lane >= 0) message["lane"] = ctx.Lane;
        return message;
    }

    /// <summary>Appends a finished reply to its thread: a compare reply hangs off the user turn that asked, a normal reply goes on the
    /// active leaf. A turn with no thread saves nothing.</summary>
    public static void SaveToThread(Session session, string threadId, ToolTurnContext ctx, JObject message)
    {
        if (session?.User is null || string.IsNullOrEmpty(threadId)) return;
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

    private static ToolTurnContext WithDefaults(ToolTurnContext ctx) => new()
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
        Dispatch = ctx.Dispatch ?? DefaultDispatch(ctx),
        Persist = ctx.Persist ?? (message => SaveToThread(ctx.Session, ctx.ThreadId, ctx, message)),
    };

    private static Func<NativeToolCall, CancellationToken, Task<JObject>> DefaultDispatch(ToolTurnContext ctx) => async (call, cancel) =>
    {
        try
        {
            return await ToolExecutorService.ExecuteTool(call.Name, ParseObject(call.Arguments), ctx.Session, ctx.AssistantId, ctx.ThreadId, null, cancel);
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
    private static Func<TextRequest, CancellationToken, IAsyncEnumerable<TextChunk>> MakeRoundStream(ExtendedLLMInput input, ILLMProvider provider)
    {
        int baseCount = input.Messages.Count;
        return (request, token) =>
        {
            List<LLMMessage> appended = [.. request.Messages.Skip(baseCount).Select(LLMMessageMapping.FromAppendedTextMessage)];
            return provider.StreamAsync(input.CloneForRound(appended, request.ForceToolId), token);
        };
    }

    private static Task Emit(IToolTurnSink sink, StringBuilder fullText, string text)
    {
        fullText.Append(text);
        return sink.Chunk(text);
    }

    private static string ToStopReason(StopReason? stop) => stop switch
    {
        StopReason.Length => "length",
        StopReason.Cancelled => "cancelled",
        StopReason.Error => "error",
        StopReason.ToolCall => "tool_call",
        _ => null,
    };

    private static JObject ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JObject();
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
        if (string.IsNullOrEmpty(text)) return text;
        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[i + 1]); i++; }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Runs the engine's tool calls through the host: the dispatch hook when set, else the executor.</summary>
    private sealed class HostToolDispatcher(ToolTurnContext ctx, ExtendedLLMInput input, CancellationToken linked) : IToolDispatcher
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = LLMMessageMapping.ToEngineTools(input.Tools);

        public async Task<string> InvokeAsync(NativeToolCall call, CancellationToken cancel)
        {
            JObject result = await ctx.Dispatch(call, cancel);
            return (result ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
