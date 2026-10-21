using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Tests.Fakes;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The chat turn: one runner for every provider, frames in the order the UI expects, the round the tool call starts,
/// the persisted reply, and the round limit.</summary>
public class ToolTurnRunnerTests
{
    private static TextChunk Text(string text) => new() { Kind = TextChunkKind.Chunk, Text = text };

    private static TextChunk Call(string id, string name, string args = "{}") => new()
    {
        Kind = TextChunkKind.NativeToolCall,
        ToolCall = new NativeToolCall { Id = id, Name = name, Arguments = args },
    };

    private static TextChunk Stop(StopReason reason) => new() { Kind = TextChunkKind.StopReason, Stop = reason };

    private static ExtendedLLMInput Input(params JObject[] tools) => new()
    {
        Model = "m",
        Messages = [new LLMMessage { Role = LLMRoles.User, Content = "what time is it?" }],
        Tools = [.. tools],
    };

    private static JObject GetTime() => new()
    {
        ["id"] = "get_time",
        ["name"] = "Get Time",
        ["description"] = "time",
        ["parameters"] = new JObject { ["type"] = "object", ["properties"] = new JObject() },
    };

    private static ToolTurnContext Context(ScriptedChatProvider provider, Func<NativeToolCall, CancellationToken, Task<JObject>> dispatch, List<JObject> saved, int maxRounds = 8) => new()
    {
        ResolveProvider = _ => Task.FromResult<ILLMProvider>(provider),
        Dispatch = dispatch,
        Persist = saved.Add,
        MaxRounds = maxRounds,
    };

    [Fact]
    public async Task ToolTurnStreamsCallResultRoundAndTextInOrder()
    {
        ScriptedChatProvider provider = new(
            [Text("Checking. "), Call("c1", "get_time"), Stop(StopReason.ToolCall)],
            [Text("It is 14:05."), Stop(StopReason.Stop)]);
        List<string> dispatched = [];
        List<JObject> saved = [];
        RecordingChatSink sink = new();
        ToolTurnContext ctx = Context(provider, (call, _) => { dispatched.Add(call.Name); return Task.FromResult(new JObject { ["now"] = "14:05" }); }, saved);
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(GetTime()), ctx, sink);

        Assert.Equal(["get_time"], dispatched);
        Assert.Equal(
            ["chunk:Checking. ", "tool_call:get_time", "tool_result:get_time", "iteration:2", "chunk:It is 14:05.", "done"],
            sink.Events);
        Assert.Equal("Checking. It is 14:05.", outcome.FullText);
        Assert.False(outcome.Truncated);
        Assert.Single(saved);
        Assert.Equal("{\"now\":\"14:05\"}", saved[0]["toolCalls"]![0]!["result"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public async Task RoundTwoSeesTheCallAndItsResultButTheForcedToolOnlyOnRoundOne()
    {
        ScriptedChatProvider provider = new(
            [Call("c1", "get_time"), Stop(StopReason.ToolCall)],
            [Text("done."), Stop(StopReason.Stop)]);
        ExtendedLLMInput input = Input(GetTime());
        input.ForceToolId = "get_time";
        ToolTurnContext ctx = Context(provider, (_, _) => Task.FromResult(new JObject { ["now"] = "14:05" }), []);
        await ToolTurnRunner.RunAsync(input, ctx, new RecordingChatSink());

        Assert.Equal("get_time", provider.Inputs[0].ForceToolId);
        Assert.Null(provider.Inputs[1].ForceToolId);
        List<LLMMessage> second = provider.Inputs[1].Messages;
        Assert.Equal(["user", "assistant", "tool"], second.Select(m => m.Role));
        Assert.Equal("c1", second[2].ToolCallId);
        Assert.Equal("c1", second[1].ToolCalls![0]["id"]!.ToString());
    }

    [Fact]
    public async Task TheRoundLimitEndsTheTurnAsTruncatedAndDoesNotDispatchTheLastCall()
    {
        ScriptedChatProvider provider = new([Call("c1", "get_time"), Stop(StopReason.ToolCall)]);
        int dispatched = 0;
        List<JObject> saved = [];
        RecordingChatSink sink = new();
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(GetTime()),
            Context(provider, (_, _) => { dispatched++; return Task.FromResult(new JObject { ["ok"] = true }); }, saved, maxRounds: 1), sink);

        Assert.Equal(0, dispatched);
        Assert.True(outcome.Truncated);
        Assert.Equal("max_iterations", outcome.Reason);
        Assert.True(saved[0]["meta"]!["truncated"]!.Value<bool>());
        // Saved as the voice done frame reports a round limit, never as tool_call.
        Assert.Equal("tool_round_limit", saved[0]["meta"]!["stopReason"]!.ToString());
        Assert.Equal("max_rounds=1", outcome.RoundLimit);
        // The call the limit stopped gets a stand-in result, so the client's bubble is not left pending.
        Assert.Equal(["tool_call:get_time", "tool_not_run:get_time", "done"], sink.Events);
        Assert.False(outcome.ToolEvents[0]!["result"]!["success"]!.Value<bool>());
    }

    [Fact]
    public async Task ATurnWithoutToolsIsOneRoundOfChunksAndADoneFrame()
    {
        ScriptedChatProvider provider = new([Text("Hello "), Text("there."), Stop(StopReason.Stop)]);
        RecordingChatSink sink = new();
        List<JObject> saved = [];
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(), Context(provider, (_, _) => throw new InvalidOperationException("no tools"), saved), sink);

        Assert.Equal(["chunk:Hello ", "chunk:there.", "done"], sink.Events);
        Assert.Equal("Hello there.", outcome.FullText);
        Assert.Null(outcome.StopReason);
        Assert.Single(provider.Inputs);
    }

    [Fact]
    public async Task ReasoningIsFedBackToTheNextRoundAsAThinkBlock()
    {
        ScriptedChatProvider provider = new(
            [new TextChunk { Kind = TextChunkKind.Reasoning, Text = "pondering" }, Call("c1", "get_time"), Stop(StopReason.ToolCall)],
            [Text("answer"), Stop(StopReason.Stop)]);
        RecordingChatSink sink = new();
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(GetTime()),
            Context(provider, (_, _) => Task.FromResult(new JObject { ["ok"] = true }), []), sink);

        Assert.StartsWith("<think>pondering</think>", outcome.FullText, StringComparison.Ordinal);
        Assert.Equal(["reasoning:pondering", "reasoning_end", "tool_call:get_time", "tool_result:get_time", "iteration:2", "chunk:answer", "done"], sink.Events);
        Assert.Equal("answer", outcome.SpokenText);
        // Round two sees the reasoning of the round that made the call, as a think block on that assistant turn (as main did).
        LLMMessage callTurn = Assert.Single(provider.Inputs[1].Messages, m => m.Role == LLMRoles.Assistant && m.ToolCalls is { Count: > 0 });
        Assert.Equal("<think>pondering</think>", callTurn.Content);
    }

    [Fact]
    public async Task ARoundWithoutReasoningLeavesItsCallTurnUnchanged()
    {
        ScriptedChatProvider provider = new([Call("c1", "get_time"), Stop(StopReason.ToolCall)], [Text("done"), Stop(StopReason.Stop)]);
        await ToolTurnRunner.RunAsync(Input(GetTime()), Context(provider, (_, _) => Task.FromResult(new JObject { ["ok"] = true }), []), new RecordingChatSink());

        LLMMessage callTurn = Assert.Single(provider.Inputs[1].Messages, m => m.Role == LLMRoles.Assistant && m.ToolCalls is { Count: > 0 });
        Assert.Equal("", callTurn.Content);
    }

    [Fact]
    public async Task AnInlineThinkBlockIsNotWrappedAgain()
    {
        // A template that writes its reasoning as visible text (Qwen3) already carries it in the call turn's content.
        ScriptedChatProvider provider = new([Text("<think>x</think>"), Call("c1", "get_time"), Stop(StopReason.ToolCall)], [Text("y"), Stop(StopReason.Stop)]);
        await ToolTurnRunner.RunAsync(Input(GetTime()), Context(provider, (_, _) => Task.FromResult(new JObject { ["ok"] = true }), []), new RecordingChatSink());

        LLMMessage callTurn = Assert.Single(provider.Inputs[1].Messages, m => m.Role == LLMRoles.Assistant && m.ToolCalls is { Count: > 0 });
        Assert.Equal("<think>x</think>", callTurn.Content);
    }

    [Fact]
    public async Task EachCallRoundsReasoningReachesItsOwnTurnInOrder()
    {
        ScriptedChatProvider provider = new(
            [new TextChunk { Kind = TextChunkKind.Reasoning, Text = "one" }, Call("c1", "get_time"), Stop(StopReason.ToolCall)],
            [new TextChunk { Kind = TextChunkKind.Reasoning, Text = "two" }, Call("c2", "get_time"), Stop(StopReason.ToolCall)],
            [Text("done"), Stop(StopReason.Stop)]);
        await ToolTurnRunner.RunAsync(Input(GetTime()), Context(provider, (_, _) => Task.FromResult(new JObject { ["ok"] = true }), []), new RecordingChatSink());

        List<LLMMessage> calls = provider.Inputs[2].Messages.Where(m => m.Role == LLMRoles.Assistant && m.ToolCalls is { Count: > 0 }).ToList();
        Assert.Equal(["<think>one</think>", "<think>two</think>"], calls.Select(m => m.Content));
    }

    [Fact]
    public async Task AStoppedTurnKeepsItsTextAndClosesTheOpenCall()
    {
        using CancellationTokenSource cts = new();
        ScriptedChatProvider provider = new([Text("partial "), Call("c1", "get_time"), Stop(StopReason.ToolCall)]);
        List<JObject> saved = [];
        RecordingChatSink sink = new() { IsOpen = false };
        ToolTurnContext ctx = Context(provider, (_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(new JObject()); }, saved);
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(GetTime()), ctx, sink, cts.Token);

        Assert.Equal("partial ", outcome.FullText);
        Assert.Equal("cancelled", outcome.StopReason);
        Assert.Single(saved);
        JObject entry = (JObject)saved[0]["toolCalls"]![0]!;
        Assert.False(entry["result"]!["success"]!.Value<bool>());
        Assert.DoesNotContain("done", sink.Events);
    }

    [Fact]
    public async Task AStopInsideOneCallEndsTheRoundBeforeTheNextCall()
    {
        using CancellationTokenSource cts = new();
        ScriptedChatProvider provider = new([Call("c1", "get_time"), Call("c2", "get_time"), Stop(StopReason.ToolCall)]);
        List<string> dispatched = [];
        RecordingChatSink sink = new();
        ToolTurnContext ctx = Context(provider, (call, _) =>
        {
            dispatched.Add(call.Id);
            cts.Cancel();
            return Task.FromResult(new JObject { ["now"] = "14:05" });
        }, []);
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(GetTime()), ctx, sink, cts.Token);

        // The second call of the round never reaches the dispatch hook; it gets a stand-in result instead.
        Assert.Equal(["c1"], dispatched);
        Assert.Equal("cancelled", outcome.StopReason);
        Assert.Equal(["tool_call:get_time", "tool_call:get_time", "tool_result:get_time", "tool_not_run:get_time", "done"], sink.Events);
        Assert.False(outcome.ToolEvents[1]!["result"]!["success"]!.Value<bool>());
    }

    [Fact]
    public async Task AFailureInALaterRoundSavesTheRoundsThatRanAndIsRaised()
    {
        // A provider error on round 2 must not discard round 1's call: the saved reply keeps it, and the caller reports the error.
        ScriptedChatProvider provider = new([Call("c1", "get_time"), Stop(StopReason.ToolCall)], [Text("never"), Stop(StopReason.Stop)])
        {
            FailRound = 2,
            Failure = new InvalidOperationException("HTTP 529 from the provider"),
        };
        List<JObject> saved = [];
        RecordingChatSink sink = new();
        InvalidOperationException raised = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ToolTurnRunner.RunAsync(Input(GetTime()), Context(provider, (_, _) => Task.FromResult(new JObject { ["now"] = "14:05" }), saved), sink));

        Assert.Equal("HTTP 529 from the provider", raised.Message);
        JObject message = Assert.Single(saved);
        Assert.Equal("error", message["meta"]!["stopReason"]!.ToString());
        Assert.Equal("HTTP 529 from the provider", message["meta"]!["error"]!.ToString());
        JObject call = (JObject)Assert.Single(message["toolCalls"]!);
        Assert.Equal("14:05", call["result"]!["now"]!.ToString());
        // No done frame follows the failure: the caller's error frame is the turn's last frame.
        Assert.Equal(["tool_call:get_time", "tool_result:get_time"], sink.Events);
    }

    [Fact]
    public async Task ASinkThatIsGoneGetsNoFramesButTheReplyIsStillSaved()
    {
        ScriptedChatProvider provider = new([Text("saved anyway."), Stop(StopReason.Stop)]);
        RecordingChatSink sink = new() { IsOpen = false };
        List<JObject> saved = [];
        await ToolTurnRunner.RunAsync(Input(), Context(provider, (_, _) => Task.FromResult(new JObject()), saved), sink);

        Assert.Equal(["chunk:saved anyway."], sink.Events);
        Assert.Single(saved);
        Assert.Equal("saved anyway.", saved[0]["content"]!.ToString());
    }

    [Fact]
    public async Task EachRoundIsRecordedWithItsTextAndCalls()
    {
        ScriptedChatProvider provider = new(
            [Text("Checking. "), Call("c1", "get_time"), Stop(StopReason.ToolCall)],
            [Text("It is 14:05."), Stop(StopReason.Stop)]);
        List<JObject> saved = [];
        await ToolTurnRunner.RunAsync(Input(GetTime()),
            Context(provider, (_, _) => Task.FromResult(new JObject { ["now"] = "14:05" }), saved), new RecordingChatSink());

        JArray rounds = (JArray)saved[0]["rounds"]!;
        Assert.Equal(2, rounds.Count);
        Assert.Equal("Checking. ", rounds[0]!["text"]!.ToString());
        Assert.Equal("c1", rounds[0]!["toolCallIds"]![0]!.ToString());
        Assert.Equal("It is 14:05.", rounds[1]!["text"]!.ToString());
        Assert.Empty((JArray)rounds[1]!["toolCallIds"]!);
    }

    [Fact]
    public async Task AReplyReportedOnlyAsAResultIsShownAndSaved()
    {
        ScriptedChatProvider provider = new([new TextChunk { Kind = TextChunkKind.Result, Text = "Whole reply." }, Stop(StopReason.Stop)]);
        RecordingChatSink sink = new();
        List<JObject> saved = [];
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(), Context(provider, (_, _) => Task.FromResult(new JObject()), saved), sink);

        Assert.Equal(["chunk:Whole reply.", "done"], sink.Events);
        Assert.Equal("Whole reply.", outcome.FullText);
        Assert.Equal("Whole reply.", saved[0]["content"]!.ToString());
    }

    [Fact]
    public async Task AResultAfterStreamedTextIsNotShownTwice()
    {
        ScriptedChatProvider provider = new([Text("Hi."), new TextChunk { Kind = TextChunkKind.Result, Text = "Hi." }, Stop(StopReason.Stop)]);
        RecordingChatSink sink = new();
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(), Context(provider, (_, _) => Task.FromResult(new JObject()), []), sink);

        Assert.Equal(["chunk:Hi.", "done"], sink.Events);
        Assert.Equal("Hi.", outcome.FullText);
    }

    [Fact]
    public async Task AResultAfterThinkOnlyTextIsNotShownTwice()
    {
        ScriptedChatProvider provider = new([Text("<think>Weighing it.</think>"), new TextChunk { Kind = TextChunkKind.Result, Text = "<think>Weighing it.</think>" }, Stop(StopReason.Stop)]);
        RecordingChatSink sink = new();
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(), Context(provider, (_, _) => Task.FromResult(new JObject()), []), sink);

        Assert.Equal(["chunk:<think>Weighing it.</think>", "done"], sink.Events);
        Assert.Equal("<think>Weighing it.</think>", outcome.FullText);
    }

    [Fact]
    public async Task MalformedArgumentsAreAnsweredWithAnErrorAndTheToolDoesNotRun()
    {
        ScriptedChatProvider provider = new(
            [Call("c1", "get_time", "{\"tz\":"), Stop(StopReason.ToolCall)],
            [Text("Let me try again."), Stop(StopReason.Stop)]);
        int dispatched = 0;
        RecordingChatSink sink = new();
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(GetTime()),
            Context(provider, (_, _) => { dispatched++; return Task.FromResult(new JObject { ["ok"] = true }); }, []), sink);

        Assert.Equal(0, dispatched);
        JObject result = (JObject)outcome.ToolEvents[0]!["result"]!;
        Assert.False(result["success"]!.Value<bool>());
        Assert.Contains("not valid JSON", result["error"]!.ToString());
        Assert.Equal("Let me try again.", outcome.SpokenText);
        Assert.Equal("tool", provider.Inputs[1].Messages[^1].Role);
        // The call as written stays in the saved turn and in the next round, not swapped for {}.
        Assert.Equal("{\"tz\":", outcome.ToolEvents[0]!["arguments"]!.ToString());
        LLMMessage callTurn = Assert.Single(provider.Inputs[1].Messages, m => m.Role == LLMRoles.Assistant && m.ToolCalls is { Count: > 0 });
        Assert.Equal("{\"tz\":", callTurn.ToolCalls![0]["arguments"]!.ToString());
    }

    [Fact]
    public async Task AnErrorStopKeepsTheProvidersMessage()
    {
        ScriptedChatProvider provider = new([Text("partial"), new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Error, Text = "CUDA is not available." }]);
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(), Context(provider, (_, _) => Task.FromResult(new JObject()), []), new RecordingChatSink());

        Assert.Equal("error", outcome.StopReason);
        Assert.Equal("CUDA is not available.", outcome.Error);
    }

    [Fact]
    public async Task InlineThinkTextIsNotSpokenButIsKeptInTheReply()
    {
        // A template that writes its reasoning as visible text (Qwen3): the chat keeps it, the speech does not.
        ScriptedChatProvider provider = new([Text("<think>pla"), Text("n</think>Noon."), Stop(StopReason.Stop)]);
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(), Context(provider, (_, _) => Task.FromResult(new JObject()), []), new RecordingChatSink());

        Assert.Equal("Noon.", outcome.SpokenText);
        Assert.Equal("<think>plan</think>Noon.", outcome.FullText);
    }

    [Fact]
    public async Task SpokenTextLeavesOutTheReasoningTrace()
    {
        ScriptedChatProvider provider = new([new TextChunk { Kind = TextChunkKind.Reasoning, Text = "private" }, Text("public answer"), Stop(StopReason.Stop)]);
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(), Context(provider, (_, _) => Task.FromResult(new JObject()), []), new RecordingChatSink());

        Assert.Contains("private", outcome.FullText, StringComparison.Ordinal);
        Assert.Equal("public answer", outcome.SpokenText);
    }
}
