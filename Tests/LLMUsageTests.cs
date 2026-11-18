using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Tests.Fakes;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Token usage: how a provider's report becomes a chunk, how rounds add up, and where it lands (the saved reply and the done frame).</summary>
public class LLMUsageTests
{
    private static TextChunk Text(string text) => new() { Kind = TextChunkKind.Chunk, Text = text };

    private static TextChunk Stop(StopReason reason) => new() { Kind = TextChunkKind.StopReason, Stop = reason };

    private static TextChunk Used(int prompt, int completion, int cached = 0) => new()
    {
        Kind = TextChunkKind.Usage,
        Usage = new TextUsage { PromptTokens = prompt, CompletionTokens = completion, CachedPromptTokens = cached },
    };

    private static TextChunk Call(string id) => new() { Kind = TextChunkKind.NativeToolCall, ToolCall = new NativeToolCall { Id = id, Name = "get_time", Arguments = "{}" } };

    private static ExtendedLLMInput Input(bool withTool) => new()
    {
        Model = "m",
        Messages = [new LLMMessage { Role = LLMRoles.User, Content = "hi" }],
        Tools = withTool
            ? [new JObject { ["id"] = "get_time", ["name"] = "Get Time", ["description"] = "time", ["parameters"] = new JObject { ["type"] = "object", ["properties"] = new JObject() } }]
            : [],
    };

    private static ToolTurnContext Context(ScriptedChatProvider provider, List<JObject> saved) => new()
    {
        ResolveProvider = _ => Task.FromResult<ILLMProvider>(provider),
        Dispatch = (_, _) => Task.FromResult(new JObject { ["now"] = "14:05" }),
        Persist = saved.Add,
    };

    [Fact]
    public void FramesRoundTripThroughTheChunkStream()
    {
        TextChunk chunk = LLMChunkStream.ToChunk(new JObject { ["usage"] = new JObject { ["promptTokens"] = 120, ["completionTokens"] = 30, ["cachedTokens"] = 100 } });
        Assert.Equal(TextChunkKind.Usage, chunk.Kind);
        Assert.Equal(120, chunk.Usage!.Value.PromptTokens);
        Assert.Equal(30, chunk.Usage!.Value.CompletionTokens);
        Assert.Equal(100, chunk.Usage!.Value.CachedPromptTokens);
    }

    [Fact]
    public void AFrameWithNoCountsIsNotAChunk()
    {
        Assert.Null(LLMChunkStream.ToChunk(new JObject { ["usage"] = new JObject { ["promptTokens"] = 0, ["completionTokens"] = 0 } }));
        Assert.Null(LLMChunkStream.ToChunk(new JObject { ["usage"] = JValue.CreateNull() }));
    }

    [Fact]
    public void RoundsAddUpButTheContextIsTheLastRound()
    {
        LLMUsage usage = new();
        usage.Add(100, 20, 10);
        usage.Add(150, 40, 60);
        Assert.Equal(250, usage.PromptTokens);
        Assert.Equal(60, usage.CompletionTokens);
        Assert.Equal(70, usage.CachedTokens);
        Assert.Equal(190, usage.ContextTokens);
        Assert.Equal(2, usage.Rounds);
    }

    [Fact]
    public void JsonRoundTripKeepsTheContextSize()
    {
        LLMUsage usage = new();
        usage.Add(100, 20);
        LLMUsage back = LLMUsage.FromJson(usage.ToJson());
        Assert.Equal(usage.ContextTokens, back.ContextTokens);
        Assert.Equal(100, back.PromptTokens);
        Assert.Null(LLMUsage.FromJson(JValue.CreateNull()));
    }

    [Fact]
    public async Task AToolTurnSumsEveryRoundAndSavesItOnTheReply()
    {
        ScriptedChatProvider provider = new(
            [Text("Checking. "), Call("c1"), Used(100, 20), Stop(StopReason.ToolCall)],
            [Text("14:05."), Used(160, 10), Stop(StopReason.Stop)]);
        List<JObject> saved = [];
        RecordingChatSink sink = new();
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(true), Context(provider, saved), sink);

        Assert.NotNull(outcome.Usage);
        Assert.Equal(260, outcome.Usage.PromptTokens);
        Assert.Equal(30, outcome.Usage.CompletionTokens);
        Assert.Equal(170, outcome.Usage.ContextTokens);
        JObject meta = (JObject)saved[0]["meta"];
        Assert.Equal(260, meta["usage"]!.Value<long>("promptTokens"));
        Assert.Equal(170, meta["usage"]!.Value<long>("contextTokens"));
    }

    [Fact]
    public async Task APlainTurnWithoutToolsAlsoReportsUsage()
    {
        ScriptedChatProvider provider = new([Text("hello"), Used(40, 5), Stop(StopReason.Stop)]);
        List<JObject> saved = [];
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(false), Context(provider, saved), new RecordingChatSink());
        Assert.Equal(45, outcome.Usage!.ContextTokens);
        Assert.NotNull(saved[0]["meta"]!["usage"]);
    }

    [Fact]
    public async Task AProviderThatReportsNothingLeavesUsageOut()
    {
        ScriptedChatProvider provider = new([Text("hello"), Stop(StopReason.Stop)]);
        List<JObject> saved = [];
        ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(Input(false), Context(provider, saved), new RecordingChatSink());
        Assert.Null(outcome.Usage);
        Assert.Null(saved[0]["meta"]!["usage"]);
    }
}

/// <summary>Where a model listing states its context window: each endpoint names it differently.</summary>
public class ContextLengthFieldTests
{
    [Theory]
    [InlineData("""{"id":"a","max_input_tokens":200000}""", 200000)]
    [InlineData("""{"id":"a","context_length":131072}""", 131072)]
    [InlineData("""{"id":"a","max_model_len":8192}""", 8192)]
    [InlineData("""{"id":"a","max_context_length":4096}""", 4096)]
    [InlineData("""{"id":"a","meta":{"n_ctx_train":32768}}""", 32768)]
    [InlineData("""{"id":"a"}""", -1)]
    [InlineData("""{"id":"a","context_length":0}""", -1)]
    [InlineData("""{"id":"a","context_length":"lots"}""", -1)]
    public void TheWindowIsReadFromWhicheverFieldTheEndpointUses(string model, int expected)
        => Assert.Equal(expected, Hartsy.Extensions.LLMAssistant.Backends.RemoteOpenAILLMProvider.ContextLengthOf(JObject.Parse(model)));
}
