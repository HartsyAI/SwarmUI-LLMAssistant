using Hartsy.Extensions.LLMAssistant.LLMs;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The frame-to-chunk adapter every provider's stream passes through: frames map to the engine's chunk kinds, and the
/// stream always ends with a stop reason.</summary>
public class LLMChunkStreamTests
{
    private static async Task<List<TextChunk>> Collect(IAsyncEnumerable<TextChunk> stream)
    {
        List<TextChunk> chunks = [];
        await foreach (TextChunk chunk in stream) chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public void FramesMapToTheChunkVocabulary()
    {
        Assert.Equal("hi", LLMChunkStream.ToChunk(new JObject { ["chunk"] = "hi" })!.Text);
        TextChunk call = LLMChunkStream.ToChunk(new JObject
        {
            ["native_tool_call"] = new JObject { ["id"] = "c1", ["name"] = "get_time", ["arguments"] = new JObject { ["tz"] = "UTC" } },
        })!;
        Assert.Equal(TextChunkKind.NativeToolCall, call.Kind);
        Assert.Equal("get_time", call.ToolCall!.Name);
        Assert.Equal("{\"tz\":\"UTC\"}", call.ToolCall.Arguments);
        Assert.Equal(StopReason.Length, LLMChunkStream.ToChunk(new JObject { ["stopReason"] = "length" })!.Stop);
        Assert.Equal(StopReason.Cancelled, LLMChunkStream.ToChunk(new JObject { ["stopReason"] = "cancelled" })!.Stop);
        Assert.Equal("loading_model", LLMChunkStream.ToChunk(new JObject { ["status"] = "loading_model" })!.Status!.Value.Phase);
        Assert.Null(LLMChunkStream.ToChunk(new JObject { ["done"] = true }));
    }

    [Fact]
    public async Task StreamEndsWithAStopWhenTheProducerDoesNotSayOne()
    {
        IAsyncEnumerable<TextChunk> stream = LLMChunkStream.FromFrames(async (emit, ct) =>
        {
            await emit(new JObject { ["chunk"] = "a" });
            await emit(new JObject { ["chunk"] = "b" });
        });
        List<TextChunk> chunks = await Collect(stream);
        Assert.Equal([TextChunkKind.Chunk, TextChunkKind.Chunk, TextChunkKind.StopReason], chunks.Select(c => c.Kind));
        Assert.Equal(StopReason.Stop, chunks[2].Stop);
    }

    [Fact]
    public async Task AnExplicitStopIsNotFollowedByAnotherOne()
    {
        IAsyncEnumerable<TextChunk> stream = LLMChunkStream.FromFrames(async (emit, ct) =>
        {
            await emit(new JObject { ["chunk"] = "cut" });
            await emit(new JObject { ["stopReason"] = "length" });
        });
        List<TextChunk> chunks = await Collect(stream);
        Assert.Equal([TextChunkKind.Chunk, TextChunkKind.StopReason], chunks.Select(c => c.Kind));
        Assert.Equal(StopReason.Length, chunks[1].Stop);
    }

    [Fact]
    public async Task AProducerFailureReachesTheConsumer()
    {
        IAsyncEnumerable<TextChunk> stream = LLMChunkStream.FromFrames(async (emit, ct) =>
        {
            await emit(new JObject { ["chunk"] = "partial" });
            throw new InvalidOperationException("provider failed");
        });
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(stream));
        Assert.Equal("provider failed", error.Message);
    }

    [Fact]
    public async Task CancellingTheConsumerStopsTheProducer()
    {
        using CancellationTokenSource cts = new();
        bool producerSawCancel = false;
        IAsyncEnumerable<TextChunk> stream = LLMChunkStream.FromFrames(async (emit, ct) =>
        {
            await emit(new JObject { ["chunk"] = "first" });
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                producerSawCancel = true;
                throw;
            }
        }, cts.Token);
        int seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (TextChunk _ in stream)
            {
                if (++seen == 1) cts.Cancel();
            }
        });
        Assert.Equal(1, seen);
        for (int i = 0; i < 50 && !producerSawCancel; i++) await Task.Delay(10);
        Assert.True(producerSawCancel);
    }
}
