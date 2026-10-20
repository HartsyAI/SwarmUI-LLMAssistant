using System.Runtime.CompilerServices;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Unit tests for <see cref="LLMProviderBackend.AppendGenerateChunk"/>, the one-shot accumulation step behind
/// <c>Generate(ExtendedLLMInput, ct)</c>, driven one <see cref="JObject"/> event at a time as <c>GenerateLive</c> would.</summary>
public class LLMProviderBackendTests
{
    [Fact]
    public void AppendGenerateChunk_PlainChunksAndResult_AccumulatesTextUnchanged()
    {
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["chunk"] = "Hello" });
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["chunk"] = ", world." });
        Assert.Equal("Hello, world.", output.ToString());
    }

    [Fact]
    public void AppendGenerateChunk_ResultEvent_AppendsAsBefore()
    {
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["result"] = "the full text" });
        Assert.Equal("the full text", output.ToString());
    }

    [Fact]
    public void AppendGenerateChunk_UnrelatedEvent_IsIgnored()
    {
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["chunk"] = "ok" });
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["stopReason"] = "length" });
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["status"] = "loading_model" });
        Assert.Equal("ok", output.ToString());
    }

    [Fact]
    public void AppendGenerateChunk_NativeToolCall_AddsNothingToTheReply()
    {
        // A one-shot caller has no loop to run a call, so the call is not text in its reply.
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["chunk"] = "Let me check." });
        LLMProviderBackend.AppendGenerateChunk(output, new JObject
        {
            ["native_tool_call"] = new JObject { ["id"] = "call_1", ["name"] = "get_time", ["arguments"] = new JObject { ["tz"] = "UTC" } }
        });
        Assert.Equal("Let me check.", output.ToString());
    }

    [Fact]
    public void ErrorStopMessage_ReadsTheEngineMessage_AndIgnoresEveryOtherEvent()
    {
        // Generate throws with this message, so a one-shot route returns success:false instead of an empty reply.
        Assert.Equal("This model runs at most 4 loops.",
            LLMProviderBackend.ErrorStopMessage(new JObject { ["stopReason"] = "error", ["error"] = "This model runs at most 4 loops." }));
        Assert.Equal("the backend reported a failure without a message", LLMProviderBackend.ErrorStopMessage(new JObject { ["stopReason"] = "error" }));
        Assert.Null(LLMProviderBackend.ErrorStopMessage(new JObject { ["stopReason"] = "length" }));
        Assert.Null(LLMProviderBackend.ErrorStopMessage(new JObject { ["chunk"] = "text" }));
    }

    /// <summary>A provider backend that answers only through <c>GenerateLive</c> frames.</summary>
    private class FrameBackend : LLMProviderBackend
    {
        public override string ProviderKind => "test";

        public override string DisplayName => "Test";

        public override IEnumerable<string> SupportedFeatures => [];

        public override Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default) => Task.FromResult(new List<LLMModelInfo>());

        public override Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct)
            => onChunk(new JObject { ["chunk"] = "from frames" });

        protected override Task OnProviderInit() => Task.CompletedTask;

        protected override Task OnProviderShutdown() => Task.CompletedTask;
    }

    /// <summary>The same backend with its own chunk stream, as the local engine provider has.</summary>
    private sealed class NativeBackend : FrameBackend
    {
        public override async IAsyncEnumerable<TextChunk> StreamAsync(ExtendedLLMInput input, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new TextChunk { Kind = TextChunkKind.Chunk, Text = "native" };
        }
    }

    private static async Task<string> FirstText(ILLMProvider provider)
    {
        await foreach (TextChunk chunk in provider.StreamAsync(new ExtendedLLMInput(), CancellationToken.None))
        {
            return chunk.Text;
        }
        return null;
    }

    [Fact]
    public async Task StreamAsync_ThroughTheInterface_ReachesTheProvidersOwnStream()
    {
        // Callers hold the provider as ILLMProvider; an override must win over the frame adapter.
        Assert.Equal("native", await FirstText(new NativeBackend()));
        Assert.Equal("from frames", await FirstText(new FrameBackend()));
    }
}
