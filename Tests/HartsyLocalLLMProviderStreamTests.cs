using Hartsy.Extensions.LLMAssistant.Backends;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using System.Text;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Covers <see cref="HartsyLocalLLMProvider.ForwardEngineStreamAsync"/>, the engine-stream to wire-event translation behind
/// <c>GenerateLive</c>, with scripted chunks: Reasoning chunks (what a structured parser such as Muse Spark's reports) become inline
/// <c>&lt;think&gt;</c> text flagged <c>reasoning</c>, which the one-shot accumulator leaves out, and the other new chunk kinds emit
/// nothing.</summary>
public class HartsyLocalLLMProviderStreamTests
{
    private static async IAsyncEnumerable<TextChunk> Stream(TextChunk[] chunks)
    {
        foreach (TextChunk chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async Task<List<JObject>> Forward(params TextChunk[] chunks)
    {
        List<JObject> events = [];
        await HartsyLocalLLMProvider.ForwardEngineStreamAsync(Stream(chunks), e => { events.Add(e); return Task.CompletedTask; }, "muse-spark.gguf", "cpu");
        return events;
    }

    private static TextChunk Reasoning(string text) => new() { Kind = TextChunkKind.Reasoning, Text = text };

    private static TextChunk Text(string text) => new() { Kind = TextChunkKind.Chunk, Text = text };

    private static TextChunk Stop(StopReason stop) => new() { Kind = TextChunkKind.StopReason, Stop = stop };

    private static string StreamedText(List<JObject> events) => string.Concat(events.Select(e => e["chunk"]?.ToString() ?? ""));

    private static bool IsReasoning(JObject e) => e.Value<bool?>("reasoning") == true;

    private static string OneShotReply(List<JObject> events)
    {
        StringBuilder output = new();
        foreach (JObject e in events)
        {
            LLMProviderBackend.AppendGenerateChunk(output, e);
        }
        return output.ToString();
    }

    [Fact]
    public async Task ReasoningThenAnswer_IsOneThinkBlockBeforeTheAnswer()
    {
        List<JObject> events = await Forward(Reasoning("Let me "), Reasoning("think."), Text("Hi."), Stop(StopReason.Stop));
        Assert.Collection(events,
            e => Assert.Equal("<think>Let me ", e["chunk"]?.ToString()),
            e => Assert.Equal("think.", e["chunk"]?.ToString()),
            e => Assert.Equal("</think>", e["chunk"]?.ToString()),
            e => Assert.Equal("Hi.", e["chunk"]?.ToString()));
        Assert.Equal("<think>Let me think.</think>Hi.", StreamedText(events));
        Assert.Equal([true, true, true, false], events.Select(IsReasoning));
    }

    [Fact]
    public async Task OneShotReply_HoldsOnlyTheAnswer()
    {
        // What a voice turn speaks and what prompt enhancement uses: Muse Spark has no enable_thinking switch to turn reasoning off.
        List<JObject> events = await Forward(Reasoning("Let me "), Reasoning("think."), Text("Hi."), Stop(StopReason.Stop));
        Assert.Equal("Hi.", OneShotReply(events));
    }

    [Fact]
    public async Task OneShotReply_KeepsAPlainStreamsOwnThinkTags()
    {
        // Qwen3's one-shot output is unchanged: its <think> text arrives as ordinary answer chunks.
        List<JObject> events = await Forward(Text("<think>x</think>"), Text("y"), Stop(StopReason.Stop));
        Assert.Equal("<think>x</think>y", OneShotReply(events));
    }

    [Fact]
    public async Task ReasoningThenToolCall_ClosesTheBlockBeforeTheCall_AndDropsTheDeltas()
    {
        NativeToolCall call = new() { Id = "call_1_0", Name = "get_time", Arguments = "{\"zone\":\"UTC\"}" };
        List<JObject> events = await Forward(
            Reasoning("I need the time."),
            new TextChunk { Kind = TextChunkKind.ToolCallDelta, ToolCallIndex = 0, ToolCall = call with { Arguments = "" } },
            new TextChunk { Kind = TextChunkKind.ToolCallDelta, ToolCallIndex = 0, Text = "{\"zone\":\"UTC\"}" },
            new TextChunk { Kind = TextChunkKind.NativeToolCall, ToolCallIndex = 0, ToolCall = call },
            Stop(StopReason.ToolCall));
        Assert.Collection(events,
            e => Assert.Equal("<think>I need the time.", e["chunk"]?.ToString()),
            e => Assert.Equal("</think>", e["chunk"]?.ToString()),
            e =>
            {
                Assert.Equal("get_time", e["native_tool_call"]?["name"]?.ToString());
                Assert.Equal("UTC", e["native_tool_call"]?["arguments"]?["zone"]?.ToString());
                Assert.False(IsReasoning(e));
            });
        Assert.StartsWith("<tool_call>", OneShotReply(events));
    }

    [Fact]
    public async Task ReasoningCutOffByTheTokenCap_IsClosedBeforeTheStopReason()
    {
        List<JObject> events = await Forward(Reasoning("hmm"), Stop(StopReason.Length));
        Assert.Collection(events,
            e => Assert.Equal("<think>hmm", e["chunk"]?.ToString()),
            e => Assert.Equal("</think>", e["chunk"]?.ToString()),
            e => Assert.Equal("length", e["stopReason"]?.ToString()));
    }

    [Fact]
    public async Task StreamEndingInReasoning_IsClosedAtTheEnd()
    {
        List<JObject> events = await Forward(Reasoning("still thinking"));
        Assert.Equal("<think>still thinking</think>", StreamedText(events));
    }

    [Fact]
    public async Task ReasoningAfterAnswerText_OpensASecondBlock()
    {
        List<JObject> events = await Forward(Text("a"), Reasoning("b"), Text("c"), Stop(StopReason.Stop));
        Assert.Equal("a<think>b</think>c", StreamedText(events));
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public async Task PlainStream_IsForwardedUnchanged()
    {
        // A Qwen3 passthrough stream carries its own <think> tags as Chunk text; nothing may be added around them.
        List<JObject> events = await Forward(Text("<think>x</think>"), Text("y"), Stop(StopReason.Stop));
        Assert.Collection(events,
            e => Assert.Equal("<think>x</think>", e["chunk"]?.ToString()),
            e => Assert.Equal("y", e["chunk"]?.ToString()));
        Assert.DoesNotContain(events, IsReasoning);
    }

    [Fact]
    public async Task OtherKinds_EmitNothing()
    {
        List<JObject> events = await Forward(
            Reasoning(""),
            new TextChunk { Kind = TextChunkKind.Usage },
            new TextChunk { Kind = TextChunkKind.ToolCallAbort, ToolCallIndex = 0 },
            new TextChunk { Kind = TextChunkKind.ToolResult, Text = "{}", ToolCall = new NativeToolCall { Id = "c", Name = "t", Arguments = "{}" } },
            new TextChunk { Kind = TextChunkKind.Status, Status = new TextStatus("loading_model") },
            new TextChunk { Kind = TextChunkKind.Result, Text = "full text" },
            Stop(StopReason.Stop));
        Assert.Empty(events);
    }

    [Fact]
    public async Task EngineError_AfterReasoning_ClosesTheBlockThenReportsTheError()
    {
        List<JObject> events = await Forward(Reasoning("r"), new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Error, Text = "boom" });
        Assert.Collection(events,
            e => Assert.Equal("<think>r", e["chunk"]?.ToString()),
            e => Assert.Equal("</think>", e["chunk"]?.ToString()),
            e => Assert.Equal("error", e["stopReason"]?.ToString()));
    }
}
