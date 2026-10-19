using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The one-shot route's <c>{ response, reasoning, finish_reason }</c>: engine chunks go through the real
/// <see cref="HartsyLocalLLMProvider.ForwardEngineStreamAsync"/> translation, then <see cref="OneShotAccumulator"/>. Thinking on keeps the
/// reasoning apart from the answer, a budget that runs out mid-reasoning is a length stop with the partial reasoning, thinking off is all
/// answer, and an engine error is a failure with its message, never an empty success.</summary>
public class OneShotDetailedReplyTests
{
    private static async IAsyncEnumerable<TextChunk> Stream(TextChunk[] chunks)
    {
        foreach (TextChunk chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async Task<OneShotAccumulator> Accumulate(params TextChunk[] chunks)
    {
        OneShotAccumulator accumulator = new();
        await HartsyLocalLLMProvider.ForwardEngineStreamAsync(Stream(chunks), e => { accumulator.Add(e); return Task.CompletedTask; }, "dory.gguf", "cuda:0");
        return accumulator;
    }

    private static TextChunk Reasoning(string text) => new() { Kind = TextChunkKind.Reasoning, Text = text };

    private static TextChunk Text(string text) => new() { Kind = TextChunkKind.Chunk, Text = text };

    private static TextChunk Stop(StopReason stop, string text = null) => new() { Kind = TextChunkKind.StopReason, Stop = stop, Text = text };

    [Fact]
    public async Task ThinkingOn_ReasoningAndAnswerComeBackApart()
    {
        OneShotAccumulator acc = await Accumulate(Reasoning("The user asks "), Reasoning("about the sky."), Text("Rayleigh scattering."), Stop(StopReason.Stop));
        LLMGenerateResult result = acc.Finish("dory.gguf");
        Assert.Equal("Rayleigh scattering.", result.Response);
        Assert.Equal("The user asks about the sky.", result.Reasoning);
        Assert.Equal(LLMGenerateResult.Stop, result.FinishReason);
    }

    [Fact]
    public async Task BudgetRunningOutMidReasoning_IsALengthStopWithThePartialReasoning()
    {
        OneShotAccumulator acc = await Accumulate(Reasoning("The user asks two things"), Reasoning(" and I should"), Stop(StopReason.Length));
        LLMGenerateResult result = acc.Finish("dory.gguf");
        Assert.Equal("", result.Response);
        Assert.Equal("The user asks two things and I should", result.Reasoning);
        Assert.Equal(LLMGenerateResult.Length, result.FinishReason);
        JObject reply = ChatEndpoints.OneShotReply(result);
        Assert.True(reply.Value<bool>("success"));
        Assert.Equal("", reply.Value<string>("response"));
        Assert.Equal("The user asks two things and I should", reply.Value<string>("reasoning"));
        Assert.Equal("length", reply.Value<string>("finish_reason"));
    }

    [Fact]
    public async Task ThinkingOff_TheWholeReplyIsTheAnswer()
    {
        OneShotAccumulator acc = await Accumulate(Text("Sunlight scatters off air molecules. "), Text("Blue scatters most."), Stop(StopReason.Stop));
        LLMGenerateResult result = acc.Finish("dory.gguf");
        Assert.Equal("Sunlight scatters off air molecules. Blue scatters most.", result.Response);
        Assert.Equal("", result.Reasoning);
        Assert.Equal("stop", ChatEndpoints.OneShotReply(result).Value<string>("finish_reason"));
    }

    [Fact]
    public async Task AnswerCutOffByTheBudget_IsALengthStopWithItsText()
    {
        OneShotAccumulator acc = await Accumulate(Text("Sunlight scatters"), Stop(StopReason.Length));
        LLMGenerateResult result = acc.Finish("dory.gguf");
        Assert.Equal("Sunlight scatters", result.Response);
        Assert.Equal(LLMGenerateResult.Length, result.FinishReason);
    }

    [Fact]
    public async Task InlineThinkBlock_IsSplitOutOfTheAnswer()
    {
        // Qwen3 writes its own <think> block as ordinary text.
        OneShotAccumulator acc = await Accumulate(Text("<think>\nplan\n</think>\n\n"), Text("answer"), Stop(StopReason.Stop));
        LLMGenerateResult result = acc.Finish("qwen3.gguf");
        Assert.Equal("answer", result.Response);
        Assert.Equal("plan", result.Reasoning);
    }

    [Fact]
    public async Task EngineError_ThrowsWithTheEngineMessage_AndTheRouteReportsAFailure()
    {
        OneShotAccumulator acc = await Accumulate(Stop(StopReason.Error, "This model runs at most 4 loops."));
        SwarmReadableErrorException failure = Assert.Throws<SwarmReadableErrorException>(() => acc.Finish("dory.gguf"));
        Assert.Contains("This model runs at most 4 loops.", failure.Message);
        JObject reply = ChatEndpoints.OneShotError(failure.Message);
        Assert.False(reply.Value<bool>("success"));
        Assert.Equal("error", reply.Value<string>("finish_reason"));
        Assert.Contains("at most 4 loops", reply.Value<string>("error"));
    }

    [Fact]
    public async Task Cancelled_IsAFailureNotAnEmptyReply()
    {
        OneShotAccumulator acc = await Accumulate(Reasoning("half"), Stop(StopReason.Cancelled));
        Assert.Throws<SwarmReadableErrorException>(() => acc.Finish("dory.gguf"));
    }

    [Fact]
    public void CachedResult_RoundTrips_AndAPlainTextEntryIsAWholeAnswer()
    {
        LLMGenerateResult result = new("", "partial", LLMGenerateResult.Length);
        Assert.Equal(result, LLMGenerateResult.FromJson(result.ToJson()));
        Assert.Equal(new LLMGenerateResult("plain reply", "", LLMGenerateResult.Stop), LLMGenerateResult.FromJson("plain reply"));
        Assert.Equal(new LLMGenerateResult("{not json", "", LLMGenerateResult.Stop), LLMGenerateResult.FromJson("{not json"));
    }

    [Theory]
    [InlineData("{\"enableThinking\": true}", true)]
    [InlineData("{\"enableThinking\": false}", false)]
    [InlineData("{\"enableThinking\": \"false\"}", false)]
    [InlineData("{\"enableThinking\": null}", null)]
    [InlineData("{\"enableThinking\": 1}", null)]
    [InlineData("{}", null)]
    public void WebSocketField_TrueOrFalseIsTheValue_AnythingElseIsTheTemplateDefault(string json, bool? expected)
    {
        Assert.Equal(expected, ChatEndpoints.ReadOptionalBool(JObject.Parse(json), "enableThinking"));
    }
}
