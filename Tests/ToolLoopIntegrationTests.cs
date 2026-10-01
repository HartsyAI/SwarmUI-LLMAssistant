using System.Runtime.CompilerServices;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;
using HartsyInference.Tools;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Exercises the Tools package's <see cref="ToolLoop"/> against a scripted fake
/// <see cref="ITextService"/> — the same contract <c>HartsyLocalLLMProvider.StreamToolLoopAsync</c> hands
/// <see cref="ToolLoop.RunAsync"/> (<c>Engine.Text</c>), just without a real engine or GGUF weights. This is
/// the "fake ITextService" coverage for the new streaming voice-turn route: it proves the round-trip shape
/// (<c>NativeToolCall</c> → dispatched result → a second model turn that sees it) end to end, which is exactly
/// what <c>ChatEndpoints.LLMAssistantVoiceTurnWS</c> depends on when it forwards chunks over the socket.</summary>
public class ToolLoopIntegrationTests
{
    /// <summary>Replays one scripted <see cref="TextChunk"/> sequence per call to <see cref="StreamAsync"/>,
    /// in order; a call past the scripted rounds is a test bug, not a silent empty stream.</summary>
    private sealed class ScriptedTextService : ITextService
    {
        private readonly List<IReadOnlyList<TextChunk>> _rounds;
        private int _callIndex;

        public List<TextRequest> SeenRequests { get; } = [];

        public ScriptedTextService(params IReadOnlyList<TextChunk>[] rounds) => _rounds = [.. rounds];

        public async IAsyncEnumerable<TextChunk> StreamAsync(ModelSpec spec, TextRequest request,
            [EnumeratorCancellation] CancellationToken cancel = default)
        {
            SeenRequests.Add(request);
            Assert.True(_callIndex < _rounds.Count, "ScriptedTextService.StreamAsync called more times than scripted.");
            foreach (TextChunk chunk in _rounds[_callIndex++])
            {
                await Task.Yield();
                cancel.ThrowIfCancellationRequested();
                yield return chunk;
            }
        }

        public Task<TextResult> GenerateAsync(ModelSpec spec, TextRequest request, CancellationToken cancel = default)
            => throw new NotSupportedException("ToolLoop only calls StreamAsync.");

        public int CountTokens(ModelSpec spec, string text) => (text?.Length ?? 0) / 4;

        public bool Unload(string device = null) => false;
    }

    private static ModelSpec Spec() => new() { Requested = "fake-model", Modality = Modality.Text };

    private static TextRequest Request() => new() { Messages = [new TextMessage { Role = TextRole.User, Content = "what time is it?" }] };

    [Fact]
    public async Task RunAsync_OneToolCall_DispatchesAndFeedsResultBack()
    {
        NativeToolCall getTimeCall = new() { Id = "call_1", Name = "get_time", Arguments = "{}" };
        ScriptedTextService fake = new(
            // Round 1: the model asks for the tool.
            [
                new TextChunk { Kind = TextChunkKind.Chunk, Text = "Let me check. " },
                new TextChunk { Kind = TextChunkKind.NativeToolCall, ToolCall = getTimeCall },
                new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.ToolCall }
            ],
            // Round 2: the model has the tool result (as a TextRole.Tool message in the conversation) and
            // answers in prose.
            [
                new TextChunk { Kind = TextChunkKind.Chunk, Text = "It is noon." },
                new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Stop }
            ]);
        int invocations = 0;
        ToolRegistry registry = new ToolRegistry().Add("get_time", "Gets the current time", "{}", (argsJson, _) =>
        {
            invocations++;
            return Task.FromResult("{\"success\":true,\"time\":\"noon\"}");
        });

        List<TextChunk> emitted = [];
        await foreach (TextChunk chunk in ToolLoop.RunAsync(fake, Spec(), Request(), registry))
        {
            emitted.Add(chunk);
        }

        Assert.Equal(1, invocations);
        Assert.Equal(2, fake.SeenRequests.Count); // one model call per round

        // Round 1's chunk, the tool call, the tool result, round 2's chunk, the final combined Result, StopReason.
        Assert.Collection(emitted,
            c => Assert.Equal((TextChunkKind.Chunk, "Let me check. "), (c.Kind, c.Text)),
            c =>
            {
                Assert.Equal(TextChunkKind.NativeToolCall, c.Kind);
                Assert.Same(getTimeCall, c.ToolCall);
            },
            c =>
            {
                Assert.Equal(TextChunkKind.Status, c.Kind);
                Assert.Equal(ToolLoop.ToolResultPhase, c.Status?.Phase);
                Assert.StartsWith(ToolLoop.ToolResultPrefix, c.Text);
                Assert.Contains("\"time\":\"noon\"", c.Text);
            },
            c => Assert.Equal((TextChunkKind.Chunk, "It is noon."), (c.Kind, c.Text)),
            c =>
            {
                Assert.Equal(TextChunkKind.Result, c.Kind);
                Assert.Equal("Let me check. It is noon.", c.Text);
            },
            c => Assert.Equal((TextChunkKind.StopReason, StopReason.Stop), (c.Kind, c.Stop)));

        // The second round's request carries the assistant's tool-call turn and the Tool-role result -- the
        // same shape HartsyLocalLLMProvider.ToTextMessage produces for a Tool-role message, just built directly
        // by ToolLoop here since there's no LLMMessage round-trip inside the loop itself.
        TextRequest round2 = fake.SeenRequests[1];
        Assert.Equal(TextRole.Assistant, round2.Messages[^2].Role);
        Assert.Equal(TextRole.Tool, round2.Messages[^1].Role);
        Assert.Equal("call_1", round2.Messages[^1].ToolCallId);
        Assert.Equal("get_time", round2.Messages[^1].Name);
    }

    [Fact]
    public async Task RunAsync_NoToolCall_PassesPlainTextThroughUnchanged()
    {
        ScriptedTextService fake = new(
            [
                new TextChunk { Kind = TextChunkKind.Chunk, Text = "Hello there." },
                new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Stop }
            ]);
        ToolRegistry registry = new ToolRegistry().Add("get_time", "Gets the current time", "{}", (_, _) => Task.FromResult("{}"));

        List<TextChunk> emitted = [];
        await foreach (TextChunk chunk in ToolLoop.RunAsync(fake, Spec(), Request(), registry))
        {
            emitted.Add(chunk);
        }

        Assert.Single(fake.SeenRequests); // never asked a second round
        Assert.Contains(emitted, c => c.Kind == TextChunkKind.Chunk && c.Text == "Hello there.");
        Assert.Contains(emitted, c => c.Kind == TextChunkKind.Result && c.Text == "Hello there.");
        Assert.Contains(emitted, c => c.Kind == TextChunkKind.StopReason && c.Stop == StopReason.Stop);
        Assert.DoesNotContain(emitted, c => c.Kind == TextChunkKind.NativeToolCall);
    }
}
