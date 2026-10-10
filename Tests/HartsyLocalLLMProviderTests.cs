using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using HartsyInference.Engine.Requests;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Unit tests for <see cref="HartsyLocalLLMProvider"/>'s pure, static role/message/request mapping
/// (<c>RoleFor</c>/<c>ToTextMessage</c>/<c>ToNativeToolCall</c>/<c>BuildRequestCore</c>, all <c>internal</c> via
/// <c>InternalsVisibleTo</c>). These run with no SwarmUI host, no <c>Program.ServerSettings</c> and no
/// constructed provider instance — a live provider's settings (<c>SettingsRaw</c>) are populated by SwarmUI's
/// backend framework at runtime, which this project does not have, so only the static helpers that take their
/// own <see cref="HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings"/> parameter (a plain
/// <c>AutoConfiguration</c>, freely constructible on its own — see <c>BuildRequestCoreTests</c>) or no settings
/// at all are exercised here.</summary>
public class HartsyLocalLLMProviderTests
{
    [Theory]
    [InlineData(LLMRoles.System, TextRole.System)]
    [InlineData(LLMRoles.Assistant, TextRole.Assistant)]
    [InlineData(LLMRoles.User, TextRole.User)]
    [InlineData(LLMRoles.Tool, TextRole.Tool)]
    [InlineData("something-unrecognized", TextRole.User)]
    public void RoleFor_MapsEachKnownRole(string input, TextRole expected)
    {
        Assert.Equal(expected, HartsyLocalLLMProvider.RoleFor(input));
    }

    [Fact]
    public void ToTextMessage_ToolRole_CarriesCallIdAndName()
    {
        // Before this fix, RoleFor's switch fell through to TextRole.User for any role it didn't recognize,
        // including "tool" — silently losing the tool-call id/name the engine's chat templates need to render
        // a `tool` turn correctly (Qwen's <tool_response>, the Jinja tool_call_id/name fields).
        LLMMessage toolResult = new()
        {
            Role = LLMRoles.Tool,
            Content = "{\"success\":true}",
            ToolCallId = "call_123",
            Name = "get_time"
        };
        TextMessage msg = HartsyLocalLLMProvider.ToTextMessage(toolResult, toolResult.Content, images: null);
        Assert.Equal(TextRole.Tool, msg.Role);
        Assert.Equal("{\"success\":true}", msg.Content);
        Assert.Equal("call_123", msg.ToolCallId);
        Assert.Equal("get_time", msg.Name);
    }

    [Theory]
    [InlineData(LLMRoles.User)]
    [InlineData(LLMRoles.Assistant)]
    [InlineData(LLMRoles.System)]
    public void ToTextMessage_NonToolRole_NeverCarriesCallIdOrName(string role)
    {
        // A non-tool message that happens to have ToolCallId/Name set (eg stale data on a reused LLMMessage)
        // must not leak them onto the TextMessage -- those fields are only meaningful for a Tool turn.
        LLMMessage msg = new() { Role = role, Content = "hi", ToolCallId = "leaked", Name = "leaked" };
        TextMessage result = HartsyLocalLLMProvider.ToTextMessage(msg, msg.Content, images: null);
        Assert.Null(result.ToolCallId);
        Assert.Null(result.Name);
    }

    [Fact]
    public void ToTextMessage_NoImages_LeavesImagesNull()
    {
        LLMMessage msg = new() { Role = LLMRoles.User, Content = "hello" };
        TextMessage result = HartsyLocalLLMProvider.ToTextMessage(msg, msg.Content, images: null);
        Assert.Null(result.Images);
    }

    [Fact]
    public void NativeToolCallJson_ValidArguments_ParsesThemAsAnObject()
    {
        NativeToolCall call = new() { Id = "call_1", Name = "get_time", Arguments = "{\"tz\":\"UTC\"}" };
        Newtonsoft.Json.Linq.JObject json = HartsyLocalLLMProvider.NativeToolCallJson(call);
        Assert.Equal("call_1", json["id"]);
        Assert.Equal("get_time", json["name"]);
        Assert.Equal("UTC", json["arguments"]?["tz"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{broken")]
    public void NativeToolCallJson_MalformedOrEmptyArguments_FailsSoftToAnEmptyObject(string arguments)
    {
        // Contrary to the contract (NativeToolCall.Arguments is documented as JSON), but the engine's own
        // AnthropicLLMProvider precedent fails soft here rather than losing the whole streamed turn over one
        // malformed call.
        NativeToolCall call = new() { Id = "call_1", Name = "get_time", Arguments = arguments };
        Newtonsoft.Json.Linq.JObject json = HartsyLocalLLMProvider.NativeToolCallJson(call);
        Assert.Empty(((Newtonsoft.Json.Linq.JObject)json["arguments"]).Properties());
    }

    [Fact]
    public void ToTextMessage_AssistantWithToolCalls_MapsEachOntoATextMessageNativeToolCall()
    {
        LLMMessage assistantTurn = new()
        {
            Role = LLMRoles.Assistant,
            Content = "",
            ToolCalls =
            [
                new() { ["id"] = "call_1", ["name"] = "get_time", ["arguments"] = new Newtonsoft.Json.Linq.JObject { ["tz"] = "UTC" } }
            ]
        };
        TextMessage msg = HartsyLocalLLMProvider.ToTextMessage(assistantTurn, assistantTurn.Content, images: null);
        Assert.NotNull(msg.ToolCalls);
        NativeToolCall call = Assert.Single(msg.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_time", call.Name);
        Assert.Equal("{\"tz\":\"UTC\"}", call.Arguments);
    }

    [Theory]
    [InlineData(LLMRoles.User)]
    [InlineData(LLMRoles.Tool)]
    [InlineData(LLMRoles.System)]
    public void ToTextMessage_NonAssistantRoleWithToolCallsSet_NeverLeaksThemOntoTheTextMessage(string role)
    {
        // Mirrors ToTextMessage_NonToolRole_NeverCarriesCallIdOrName above: ToolCalls is only meaningful on an
        // Assistant turn, same as ToolCallId/Name are only meaningful on a Tool turn.
        LLMMessage msg = new() { Role = role, Content = "x", ToolCalls = [new() { ["id"] = "leaked", ["name"] = "leaked" }] };
        TextMessage result = HartsyLocalLLMProvider.ToTextMessage(msg, msg.Content, images: null);
        Assert.Null(result.ToolCalls);
    }

    [Fact]
    public void ToTextMessage_AssistantWithNoToolCalls_LeavesToolCallsNull()
    {
        LLMMessage msg = new() { Role = LLMRoles.Assistant, Content = "hello" };
        TextMessage result = HartsyLocalLLMProvider.ToTextMessage(msg, msg.Content, images: null);
        Assert.Null(result.ToolCalls);
    }

    [Fact]
    public void ToNativeToolCall_ObjectArguments_SerializesThemToACompactJsonString()
    {
        // The common case: a client echoing back the exact {id,name,arguments} shape it received on an
        // earlier native_tool_call frame, where "arguments" is a parsed object, not a string.
        Newtonsoft.Json.Linq.JObject call = new() { ["id"] = "call_1", ["name"] = "get_time", ["arguments"] = new Newtonsoft.Json.Linq.JObject { ["tz"] = "UTC" } };
        NativeToolCall result = HartsyLocalLLMProvider.ToNativeToolCall(call);
        Assert.Equal("call_1", result.Id);
        Assert.Equal("get_time", result.Name);
        Assert.Equal("{\"tz\":\"UTC\"}", result.Arguments);
    }

    [Fact]
    public void ToNativeToolCall_StringArguments_AcceptedAsIs()
    {
        // A client that re-serializes "arguments" as a JSON string instead of leaving it a parsed object --
        // NativeToolCall.Arguments is a string either way, so this costs nothing extra to accept.
        Newtonsoft.Json.Linq.JObject call = new() { ["id"] = "call_1", ["name"] = "get_time", ["arguments"] = "{\"tz\":\"UTC\"}" };
        NativeToolCall result = HartsyLocalLLMProvider.ToNativeToolCall(call);
        Assert.Equal("{\"tz\":\"UTC\"}", result.Arguments);
    }

    [Fact]
    public void ToNativeToolCall_MissingArguments_FailsSoftToAnEmptyObjectString()
    {
        Newtonsoft.Json.Linq.JObject call = new() { ["id"] = "call_1", ["name"] = "get_time" };
        NativeToolCall result = HartsyLocalLLMProvider.ToNativeToolCall(call);
        Assert.Equal("{}", result.Arguments);
    }

    [Fact]
    public void ToNativeToolCall_MissingIdOrName_FallsBackToEmptyStringsNotNull()
    {
        // Name is `required` on the engine's NativeToolCall record -- null would throw at construction.
        Newtonsoft.Json.Linq.JObject call = new();
        NativeToolCall result = HartsyLocalLLMProvider.ToNativeToolCall(call);
        Assert.Equal("", result.Id);
        Assert.Equal("", result.Name);
    }

    /// <summary>Pins <see cref="HartsyLocalLLMProvider.BuildRequestCore"/> — the pure tail of
    /// <c>BuildRequestAsync</c>, extracted specifically so a scalar like <see cref="TextRequest.EnableThinking"/>
    /// is verifiable against a real <see cref="TextRequest"/> the way the Round-3 addendum asked for ("reaches
    /// the engine request"), not just by reading the one-line object-initializer entry that sets it.
    /// <see cref="HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings"/> is a plain <c>AutoConfiguration</c>
    /// with field initializers for defaults — constructs fine with no SwarmUI host, confirmed by every test
    /// below actually running (not skipping).</summary>
    public class BuildRequestCoreTests
    {
        private static ExtendedLLMInput Input(bool? enableThinking = null) => new()
        {
            Model = "qwen3-4b",
            Temperature = 0.8,
            TopP = 0.9,
            MaxTokens = 2048,
            Seed = 42,
            EnableThinking = enableThinking
        };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        [InlineData(null)]
        public void EnableThinking_ReachesTheTextRequestUnchanged(bool? enableThinking)
        {
            TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(
                Input(enableThinking), messages: [], tools: null, deviceKey: "cuda:0", settings: new());
            Assert.Equal(enableThinking, request.EnableThinking);
        }

        [Theory]
        [InlineData("auto", "auto")]
        [InlineData("GPU", "gpu")]
        [InlineData(" split ", "split")]
        [InlineData("offload", "offload")]
        [InlineData("", null)]
        [InlineData("offlaod", null)]
        public void Placement_ReachesTheTextRequest_AndAnUnknownWordFallsBackToTheEngineDefault(string setting, string expected)
        {
            TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(
                Input(), messages: [], tools: null, deviceKey: "cuda:0", settings: new() { Placement = setting });
            Assert.Equal(expected, request.Placement);
        }

        [Theory]
        [InlineData("cuda:0", 2, "cuda:0+cuda:1")]
        [InlineData("cuda:1", 2, "cuda:1+cuda:0")]
        [InlineData("cuda:0", 3, "cuda:0+cuda:1+cuda:2")]
        [InlineData("cuda:0", 1, null)]
        [InlineData("cpu", 2, null)]
        public void SplitDeviceKey_PutsThisBackendsGpuFirst(string primary, int gpus, string expected) =>
            Assert.Equal(expected, HartsyLocalLLMProvider.SplitDeviceKey(primary, gpus));

        [Fact]
        public void ScalarOverrides_AllReachTheTextRequest()
        {
            // Not just EnableThinking: the same extraction makes every other per-request override checkable
            // against a real TextRequest too, instead of only by reading BuildRequestAsync's source.
            TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(
                Input(), messages: [], tools: null, deviceKey: "cuda:1", settings: new());
            Assert.Equal(0.8, request.Temperature);
            Assert.Equal(0.9, request.TopP);
            Assert.Equal(2048, request.MaxTokens);
            Assert.Equal(42, request.Seed);
            Assert.Equal("cuda:1", request.Device);
            Assert.False(request.Greedy); // Temperature > 0
        }

        [Fact]
        public void ZeroTemperature_IsGreedy()
        {
            TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(
                new ExtendedLLMInput { Model = "qwen3-4b", Temperature = 0 }, messages: [], tools: null, deviceKey: "cuda:0", settings: new());
            Assert.True(request.Greedy);
        }

        [Fact]
        public void ProviderSettingsDefaults_FlowThroughWhenAboveTheirOffSentinel()
        {
            HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings settings = new() { TopK = 40, MinP = 0.05, RepetitionPenalty = 1.1 };
            TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(Input(), messages: [], tools: null, deviceKey: "cuda:0", settings: settings);
            Assert.Equal(40, request.TopK);
            Assert.Equal(0.05, request.MinP);
            Assert.Equal(1.1, request.RepetitionPenalty);
        }

        [Fact]
        public void ProviderSettingsAtTheirOffSentinel_LeaveTheRequestFieldNull()
        {
            HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings settings = new() { TopK = 0, MinP = 0, RepetitionPenalty = 0 };
            TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(Input(), messages: [], tools: null, deviceKey: "cuda:0", settings: settings);
            Assert.Null(request.TopK);
            Assert.Null(request.MinP);
            Assert.Null(request.RepetitionPenalty);
        }
    }
}
