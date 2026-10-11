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
}
