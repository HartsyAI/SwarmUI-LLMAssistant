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
        Assert.Equal("done", sink.Events[^1]);
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
    public async Task ReasoningIsWrappedInThinkTagsAndNeverFedBackToTheModel()
    {
        ScriptedChatProvider provider = new(
            [new TextChunk { Kind = TextChunkKind.Reasoning, Text = "pondering" }, Call("c1", "get_time"), Stop(StopReason.ToolCall)],
            [Text("answer"), Stop(StopReason.Stop)]);
        RecordingChatSink sink = new();
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(GetTime()),
            Context(provider, (_, _) => Task.FromResult(new JObject { ["ok"] = true }), []), sink);

        Assert.StartsWith("<think>pondering</think>", outcome.FullText, StringComparison.Ordinal);
        Assert.Equal(["chunk:<think>", "chunk:pondering", "chunk:</think>", "tool_call:get_time", "tool_result:get_time", "iteration:2", "chunk:answer", "done"], sink.Events);
        Assert.DoesNotContain(provider.Inputs[1].Messages, m => m.Role == LLMRoles.Assistant && (m.Content ?? "").Contains("<think>", StringComparison.Ordinal));
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
}
