using System.Runtime.CompilerServices;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;
using HartsyInference.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Covers the Round-3 addendum's contract additions to
/// <see cref="ChatEndpoints.LLMAssistantVoiceTurnWS"/> — conversation history (<c>messages</c>),
/// <c>enableThinking</c>, and the no-hard-error notice-then-plain-stream fallback — at every layer that is
/// actually reachable without a live <c>Session</c>/<c>WebSocket</c>/host (the route itself needs all three; see
/// <c>ToolLoopIntegrationTests</c>' own comment on the same limitation). Three layers, three test groups below:
/// <list type="bullet">
/// <item><see cref="ParseMessagesArrayTests"/> — the wire JSON → <see cref="LLMMessage"/> parsing
/// (<see cref="ChatEndpoints.ParseMessagesArray"/>), pure.</item>
/// <item><see cref="ForwardFramesAsyncTests"/> — the chunk → wire-frame emission
/// (<see cref="ChatEndpoints.ForwardFramesAsync"/>) against a real <see cref="ToolLoop.RunAsync"/> driven by a
/// scripted fake <see cref="ITextService"/>, exactly the "fake ITextService" coverage the addendum asked for,
/// including the 3-turn history round trip and the empty-registry "plain stream" shape the notice fallback
/// relies on.</item>
/// <item>The system-prompt dedup rule itself (<c>messages[0]</c> already system vs not) is pinned in
/// <see cref="ExtendedLLMInputCreateFromMessagesTests"/>, not duplicated here.</item>
/// </list></summary>
public class ChatEndpointsVoiceTurnWsContractTests
{
    public class ParseMessagesArrayTests
    {
        [Fact]
        public void ParsesRoleContentAndToolFields()
        {
            JArray raw =
            [
                new JObject { ["role"] = "system", ["content"] = "Be helpful." },
                new JObject { ["role"] = "user", ["content"] = "what time is it?" },
                new JObject
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["toolCalls"] = new JArray { new JObject { ["id"] = "call_1", ["name"] = "get_time", ["arguments"] = new JObject() } }
                },
                new JObject { ["role"] = "tool", ["content"] = "{\"time\":\"noon\"}", ["toolCallId"] = "call_1", ["name"] = "get_time" }
            ];
            List<LLMMessage> parsed = ChatEndpoints.ParseMessagesArray(raw);
            Assert.Equal(4, parsed.Count);
            Assert.Equal((LLMRoles.System, "Be helpful."), (parsed[0].Role, parsed[0].Content));
            Assert.Equal((LLMRoles.User, "what time is it?"), (parsed[1].Role, parsed[1].Content));
            Assert.Equal(LLMRoles.Assistant, parsed[2].Role);
            Assert.Single(parsed[2].ToolCalls);
            Assert.Equal("get_time", parsed[2].ToolCalls[0]["name"]?.ToString());
            Assert.Equal(LLMRoles.Tool, parsed[3].Role);
            Assert.Equal("call_1", parsed[3].ToolCallId);
            Assert.Equal("get_time", parsed[3].Name);
        }

        [Fact]
        public void UnrecognizedRole_FallsBackToUser()
        {
            JArray raw = [new JObject { ["role"] = "narrator", ["content"] = "once upon a time" }];
            List<LLMMessage> parsed = ChatEndpoints.ParseMessagesArray(raw);
            Assert.Equal(LLMRoles.User, Assert.Single(parsed).Role);
        }

        [Fact]
        public void MissingContent_BecomesEmptyStringNotNull()
        {
            JArray raw = [new JObject { ["role"] = "user" }];
            Assert.Equal("", Assert.Single(ChatEndpoints.ParseMessagesArray(raw)).Content);
        }

        [Fact]
        public void NonObjectEntry_IsSkippedNotFatal()
        {
            JArray raw = ["not an object", new JObject { ["role"] = "user", ["content"] = "hi" }];
            Assert.Single(ChatEndpoints.ParseMessagesArray(raw));
        }

        [Fact]
        public void EmptyToolCallsArray_LeavesToolCallsNullNotAnEmptyList()
        {
            JArray raw = [new JObject { ["role"] = "assistant", ["content"] = "hi", ["toolCalls"] = new JArray() }];
            Assert.Null(Assert.Single(ChatEndpoints.ParseMessagesArray(raw)).ToolCalls);
        }
    }

    public class ForwardFramesAsyncTests
    {
        /// <summary>Same role as <c>ToolLoopIntegrationTests.ScriptedTextService</c> (duplicated rather than
        /// shared, matching that file's own per-file scoping): replays one scripted <see cref="TextChunk"/>
        /// sequence per call to <see cref="StreamAsync"/>, in order.</summary>
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
                    yield return chunk;
                }
            }

            public Task<TextResult> GenerateAsync(ModelSpec spec, TextRequest request, CancellationToken cancel = default)
                => throw new NotSupportedException("Not exercised by these tests.");
            public int CountTokens(ModelSpec spec, string text) => 0;
            public bool Unload(string device = null) => false;
        }

        private static ModelSpec Spec() => new() { Requested = "qwen3-4b", Modality = Modality.Text };

        [Fact]
        public async Task ThreeTurnHistoryWithAnEarlierToolCall_RoundTripsThroughTheRealMappingAndEmission()
        {
            // The addendum's "3-turn messages round trip with one earlier tool call rendered through the fake
            // ITextService": builds the request the same way the production pipeline does -- ParseMessagesArray
            // -> ExtendedLLMInput.CreateFromMessages -> HartsyLocalLLMProvider.ToTextMessage per message -- then
            // drives it through the real ToolLoop.RunAsync and the real ForwardFramesAsync, proving both the
            // mapping and the emission, not just one or the other.
            JArray raw =
            [
                new JObject { ["role"] = "user", ["content"] = "what time is it?" },
                new JObject
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["toolCalls"] = new JArray { new JObject { ["id"] = "call_1", ["name"] = "get_time", ["arguments"] = new JObject() } }
                },
                new JObject { ["role"] = "tool", ["content"] = "{\"time\":\"noon\"}", ["toolCallId"] = "call_1", ["name"] = "get_time" }
            ];
            ExtendedLLMInput input = ExtendedLLMInput.CreateFromMessages(ChatEndpoints.ParseMessagesArray(raw), systemPrompt: null, model: "qwen3-4b");
            Assert.Equal(3, input.Messages.Count); // no system prompt given, none prepended

            List<TextMessage> engineMessages = [.. input.Messages.Select(m => HartsyLocalLLMProvider.ToTextMessage(m, m.Content, images: null))];
            TextRequest request = new() { Messages = engineMessages };

            ScriptedTextService fake = new(
                [
                    new TextChunk { Kind = TextChunkKind.Chunk, Text = "It is noon." },
                    new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Stop }
                ]);
            ToolRegistry registry = new();
            List<JObject> sentFrames = [];
            JArray deviceCalls = [];
            (string fullText, StopReason? stop) = await ChatEndpoints.ForwardFramesAsync(
                ToolLoop.RunAsync(fake, Spec(), request, registry), f => { sentFrames.Add(f); return Task.CompletedTask; }, () => true, deviceCalls);

            // The mapping: round-tripped history reached the engine with the assistant/tool roles and the
            // tool-call id/name intact, exactly the shape ToolLoopIntegrationTests documents ToolLoop itself
            // produces for a *dispatched* call -- here it arrives pre-built, as history.
            TextRequest seen = Assert.Single(fake.SeenRequests);
            Assert.Equal(TextRole.User, seen.Messages[0].Role);
            Assert.Equal(TextRole.Assistant, seen.Messages[1].Role);
            Assert.Equal("call_1", Assert.Single(seen.Messages[1].ToolCalls).Id);
            Assert.Equal(TextRole.Tool, seen.Messages[2].Role);
            Assert.Equal("call_1", seen.Messages[2].ToolCallId);

            // The emission: one chunk frame, then done's shape via ForwardFramesAsync's own return value.
            Assert.Equal("It is noon.", fullText);
            Assert.Equal(StopReason.Stop, stop);
            Assert.Equal(1, sentFrames.Count(f => f["chunk"] is not null));
            Assert.DoesNotContain(sentFrames, f => f["native_tool_call"] is not null || f["tool_result"] is not null);
        }

        [Fact]
        public async Task NoticeFollowedByAnEmptyRegistryStream_IsOneNoticeThenPlainChunksThenDoneNoToolFrames()
        {
            // The addendum's "notice-then-plain-stream path": LLMAssistantVoiceTurnWS itself can't run here (it
            // needs a live Session/socket/provider), so this drives the exact two things it actually does in
            // that branch -- send one notice frame, then run ForwardFramesAsync over an EMPTY ToolRegistry
            // stream (what the route passes when SupportsNativeToolCallingFor is false; it never calls
            // ApplyToolsToInput in that branch, so no tool-call frame can appear either way) -- and asserts the
            // combined frame sequence a WS client would actually see.
            ScriptedTextService fake = new(
                [
                    new TextChunk { Kind = TextChunkKind.Chunk, Text = "Sure, here's the answer." },
                    new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Stop }
                ]);
            ToolRegistry emptyRegistry = new();
            TextRequest request = new() { Messages = [new TextMessage { Role = TextRole.User, Content = "anything" }] };
            List<JObject> sentFrames = [];
            Func<JObject, Task> send = f => { sentFrames.Add(f); return Task.CompletedTask; };

            await send(new JObject { ["notice"] = "tool calling unavailable for qwen3-4b: Structured Tool Calling is turned off (Server > Backends); replying without tools" });
            JArray deviceCalls = [];
            (string fullText, StopReason? stop) = await ChatEndpoints.ForwardFramesAsync(
                ToolLoop.RunAsync(fake, Spec(), request, emptyRegistry), send, () => true, deviceCalls);
            await send(new JObject { ["done"] = true, ["full_text"] = fullText, ["toolCalls"] = deviceCalls, ["stopReason"] = null });

            Assert.Single(fake.SeenRequests); // one plain round -- never asked for a second
            Assert.Equal("Sure, here's the answer.", fullText);
            Assert.Empty(deviceCalls);
            Assert.Collection(sentFrames,
                f => Assert.NotNull(f["notice"]),
                f => Assert.Equal("Sure, here's the answer.", f["chunk"]?.ToString()),
                f => Assert.True(f["done"]?.Value<bool>()));
            Assert.DoesNotContain(sentFrames, f => f["native_tool_call"] is not null || f["tool_result"] is not null || f["error"] is not null);
        }

        [Fact]
        public async Task SocketClosedMidStream_StopsDrainingAndReturnsWhateverCameBeforeIt()
        {
            ScriptedTextService fake = new(
                [
                    new TextChunk { Kind = TextChunkKind.Chunk, Text = "first " },
                    new TextChunk { Kind = TextChunkKind.Chunk, Text = "second " },
                    new TextChunk { Kind = TextChunkKind.StopReason, Stop = StopReason.Stop }
                ]);
            TextRequest request = new() { Messages = [new TextMessage { Role = TextRole.User, Content = "x" }] };
            int sent = 0;
            bool open = true;
            (string fullText, StopReason? stop) = await ChatEndpoints.ForwardFramesAsync(
                ToolLoop.RunAsync(fake, Spec(), request, new ToolRegistry()),
                f => { sent++; open = false; return Task.CompletedTask; }, // "close" the socket right after the first frame
                () => open, []);
            Assert.Equal(1, sent);
            Assert.Equal("first ", fullText); // only the first chunk was appended before the open-check broke the loop
            Assert.Null(stop); // StopReason chunk never reached
        }
    }
}
