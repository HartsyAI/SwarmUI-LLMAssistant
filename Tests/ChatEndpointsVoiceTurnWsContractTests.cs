using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Tests.Fakes;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The <see cref="ChatEndpoints.LLMAssistantVoiceTurnWS"/> contract at every layer reachable without a live session or socket:
/// the <c>messages</c> parsing (<see cref="ParseMessagesArrayTests"/>) and the frames a turn sends (<see cref="VoiceTurnFramesTests"/>,
/// the real <see cref="ToolTurnRunner"/> and engine <c>ToolLoop</c> over a scripted provider into <see cref="VoiceWsSink"/>). The
/// system-prompt rule for <c>messages</c> is pinned in <see cref="ExtendedLLMInputCreateFromMessagesTests"/>.</summary>
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

    public class VoiceTurnFramesTests
    {
        private static TextChunk Text(string text) => new() { Kind = TextChunkKind.Chunk, Text = text };

        private static TextChunk Reasoning(string text) => new() { Kind = TextChunkKind.Reasoning, Text = text };

        private static TextChunk Call(string id) => new() { Kind = TextChunkKind.NativeToolCall, ToolCall = new NativeToolCall { Id = id, Name = "get_time", Arguments = "{}" } };

        private static TextChunk Stop(StopReason reason) => new() { Kind = TextChunkKind.StopReason, Stop = reason };

        private static ExtendedLLMInput Input() => new()
        {
            Model = "m",
            Messages = [new LLMMessage { Role = LLMRoles.User, Content = "time?" }],
            Tools = [new JObject { ["id"] = "get_time", ["name"] = "Get Time", ["description"] = "time", ["parameters"] = new JObject { ["type"] = "object" } }],
        };

        /// <summary>One voice turn over <paramref name="provider"/>: the frames sent and how many times the tool ran.</summary>
        private static async Task<(List<JObject> Frames, int Runs)> Run(ScriptedChatProvider provider, int maxRounds = 8, ExtendedLLMInput input = null,
            Func<bool> isOpen = null, Action<List<JObject>> onSend = null, Func<CancellationToken, Task<JObject>> dispatch = null, CancellationToken ct = default)
        {
            List<JObject> frames = [];
            int runs = 0;
            ToolTurnContext ctx = new()
            {
                ResolveProvider = _ => Task.FromResult<ILLMProvider>(provider),
                Dispatch = (call, token) =>
                {
                    runs++;
                    return dispatch is null ? Task.FromResult(new JObject { ["success"] = true, ["time"] = "noon" }) : dispatch(token);
                },
                Persist = _ => { },
                MaxRounds = maxRounds,
            };
            VoiceWsSink sink = new(frame =>
            {
                frames.Add(frame);
                onSend?.Invoke(frames);
                return Task.CompletedTask;
            }, isOpen ?? (() => true), []);
            await ToolTurnRunner.RunAsync(input ?? Input(), ctx, sink, ct);
            return (frames, runs);
        }

        /// <summary>Every <c>native_tool_call</c> frame is followed by the <c>tool_result</c> for the same id: a relay such as AudioLab's
        /// <c>RemoteTextService</c> treats a call without a result as one it has to run itself.</summary>
        private static void AssertEveryCallFrameHasItsResult(List<JObject> frames)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i]["native_tool_call"] is not JObject call)
                {
                    continue;
                }
                string id = call["id"]?.ToString();
                Assert.Contains(frames.Skip(i + 1), f => f["tool_result"]?["id"]?.ToString() == id);
            }
        }

        [Fact]
        public async Task RoundLimit_NeverForwardsTheUndispatchedCall_AndReportsANoticeInstead()
        {
            ScriptedChatProvider provider = new([Text("Checking. "), Call("call_1"), Stop(StopReason.ToolCall)]);
            (List<JObject> frames, int runs) = await Run(provider, maxRounds: 1);

            Assert.Equal(0, runs);
            Assert.DoesNotContain(frames, f => f["native_tool_call"] is not null || f["tool_result"] is not null);
            Assert.Collection(frames,
                f => Assert.Equal("Checking. ", f["chunk"]?.ToString()),
                f => Assert.Equal("tool round limit reached (max_rounds=1); the model's last tool call was not run", f["notice"]?.ToString()),
                f => Assert.Equal("tool_round_limit", f["stopReason"]?.ToString()));
        }

        [Fact]
        public async Task RoundLimitAfterADispatchedRound_ForwardsOnlyTheCallThatRan()
        {
            ScriptedChatProvider provider = new([Call("call_1"), Stop(StopReason.ToolCall)], [Call("call_2"), Stop(StopReason.ToolCall)]);
            (List<JObject> frames, int runs) = await Run(provider, maxRounds: 2);

            Assert.Equal(1, runs);
            AssertEveryCallFrameHasItsResult(frames);
            Assert.Collection(frames,
                f => Assert.Equal("call_1", f["native_tool_call"]?["id"]?.ToString()),
                f => Assert.Equal("call_1", f["tool_result"]?["id"]?.ToString()),
                f => Assert.NotNull(f["notice"]),
                f => Assert.True(f["done"]?.Value<bool>()));
        }

        [Fact]
        public async Task CallAnnouncedInARoundThatErrors_IsNotForwarded()
        {
            ScriptedChatProvider provider = new([Text("partial"), Call("call_1"), Stop(StopReason.Error)]);
            (List<JObject> frames, int runs) = await Run(provider);

            Assert.Equal(0, runs);
            Assert.DoesNotContain(frames, f => f["native_tool_call"] is not null || f["tool_result"] is not null || f["notice"] is not null);
            Assert.Equal("error", frames[^1]["stopReason"]?.ToString());
        }

        [Fact]
        public async Task DispatchedCalls_StillForwardCallThenResult()
        {
            ScriptedChatProvider provider = new([Call("call_1"), Call("call_2"), Stop(StopReason.ToolCall)], [Text("Noon."), Stop(StopReason.Stop)]);
            (List<JObject> frames, int runs) = await Run(provider);

            Assert.Equal(2, runs);
            AssertEveryCallFrameHasItsResult(frames);
            Assert.Collection(frames,
                f => Assert.Equal("call_1", f["native_tool_call"]?["id"]?.ToString()),
                f => Assert.Equal("call_1", f["tool_result"]?["id"]?.ToString()),
                f => Assert.Equal("call_2", f["native_tool_call"]?["id"]?.ToString()),
                f => Assert.Equal("call_2", f["tool_result"]?["id"]?.ToString()),
                f => Assert.Equal("Noon.", f["chunk"]?.ToString()),
                f =>
                {
                    Assert.Equal("Noon.", f["full_text"]?.ToString());
                    Assert.Null(f["stopReason"]?.Value<string>());
                });
        }

        [Fact]
        public async Task ReasoningChunks_AreNeitherSpokenNorSent_AndTheToolResultArrivesWhole()
        {
            ScriptedChatProvider provider = new(
                [Reasoning("I should check the clock."), Call("call_1"), Stop(StopReason.ToolCall)],
                [Reasoning("It said noon."), Text("Noon."), Stop(StopReason.Stop)]);
            (List<JObject> frames, _) = await Run(provider);

            Assert.DoesNotContain(frames, f => f.ToString().Contains("clock") || f.ToString().Contains("It said"));
            Assert.Collection(frames,
                f => Assert.Equal("call_1", f["native_tool_call"]?["id"]?.ToString()),
                f => Assert.Equal("noon", f["tool_result"]?["result"]?["time"]?.ToString()),
                f => Assert.Equal("Noon.", f["chunk"]?.ToString()),
                f => Assert.Equal("Noon.", f["full_text"]?.ToString()));
        }

        [Fact]
        public async Task ThreeTurnHistoryWithAnEarlierToolCall_ReachesTheProviderAndStreamsPlainText()
        {
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
            ExtendedLLMInput input = ExtendedLLMInput.CreateFromMessages(ChatEndpoints.ParseMessagesArray(raw), systemPrompt: null, model: "m");
            ScriptedChatProvider provider = new([Text("It is noon."), Stop(StopReason.Stop)]);
            (List<JObject> frames, int runs) = await Run(provider, input: input);

            List<LLMMessage> seen = Assert.Single(provider.Inputs).Messages;
            List<TextMessage> engine = [.. seen.Select(m => HartsyLocalLLMProvider.ToTextMessage(m, m.Content, images: null))];
            Assert.Equal([TextRole.User, TextRole.Assistant, TextRole.Tool], engine.Select(m => m.Role));
            Assert.Equal("call_1", Assert.Single(engine[1].ToolCalls).Id);
            Assert.Equal("call_1", engine[2].ToolCallId);
            Assert.Equal(0, runs);
            Assert.Collection(frames,
                f => Assert.Equal("It is noon.", f["chunk"]?.ToString()),
                f => Assert.True(f["done"]?.Value<bool>()));
        }

        [Fact]
        public async Task SocketClosedMidStream_SendsNothingMore()
        {
            ScriptedChatProvider provider = new([Text("first "), Text("second "), Stop(StopReason.Stop)]);
            bool open = true;
            (List<JObject> frames, _) = await Run(provider, isOpen: () => open, onSend: _ => open = false);

            Assert.Equal("first ", Assert.Single(frames)["chunk"]?.ToString());
        }

        [Fact]
        public async Task StoppedTurn_NeverForwardsTheCallThatWasCutOff()
        {
            using CancellationTokenSource cts = new();
            ScriptedChatProvider provider = new([Call("call_1"), Stop(StopReason.ToolCall)]);
            (List<JObject> frames, int runs) = await Run(provider, dispatch: token =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new JObject());
            }, ct: cts.Token);

            Assert.Equal(1, runs);
            Assert.DoesNotContain(frames, f => f["native_tool_call"] is not null || f["tool_result"] is not null);
            Assert.Equal("cancelled", frames[^1]["stopReason"]?.ToString());
        }
    }
}
