using System.Text;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Unit tests for <see cref="LLMProviderBackend.AppendGenerateChunk"/>, the one-shot accumulation
/// step behind <c>Generate(ExtendedLLMInput, ct)</c> — and through it, <c>LLMDispatcher.Generate</c> and
/// <c>ChatEndpoints.LLMAssistantVoiceTurn</c>'s agentic loop, neither of which previously saw a
/// <c>native_tool_call</c> event at all. No live backend needed: this is a pure <c>StringBuilder</c> fold,
/// exercised the same way <c>GenerateLive</c> would drive it, one <see cref="JObject"/> event at a time.</summary>
public class LLMProviderBackendTests
{
    private static JObject NativeToolCall(string id, string name, JObject arguments) => new()
    {
        ["native_tool_call"] = new JObject { ["id"] = id, ["name"] = name, ["arguments"] = arguments }
    };

    [Fact]
    public void AppendGenerateChunk_PlainChunksAndResult_AccumulatesTextUnchanged()
    {
        // Today's behavior (no tools involved at all, eg LLMAssistantSendMessage/LLMAssistantTestInstruction,
        // or any provider with StructuredToolCalling/native tool calling off): this must stay byte-identical.
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
        // stopReason/status/etc -- GenerateLive emits these too; the accumulator only ever cared about
        // chunk/result, and now native_tool_call. Everything else must stay a no-op, exactly as before.
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["chunk"] = "ok" });
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["stopReason"] = "length" });
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["status"] = "loading_model" });
        Assert.Equal("ok", output.ToString());
    }

    [Fact]
    public void AppendGenerateChunk_NativeToolCall_SynthesizesATagTheExistingScannerParses()
    {
        // The fix: a native_tool_call event (Hartsy-local with StructuredToolCalling on) used to vanish
        // silently here. It now becomes the exact <tool_call>{...}</tool_call> text the tag-convention
        // scanner (ToolPromptService.ParseToolCalls) already knows how to find -- "route into the same
        // tool-dispatch path the tag scan feeds today", with zero changes to that scanner or to
        // LLMAssistantVoiceTurn's dispatch loop.
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["chunk"] = "Let me check. " });
        LLMProviderBackend.AppendGenerateChunk(output, NativeToolCall("call_1", "get_time", new JObject { ["tz"] = "UTC" }));

        string round = output.ToString();
        Assert.Contains("<tool_call>", round);
        Assert.Contains("</tool_call>", round);

        List<ToolPromptService.ParsedToolCall> calls = ToolPromptService.ParseToolCalls(round, out List<string> malformed);
        Assert.Empty(malformed);
        ToolPromptService.ParsedToolCall call = Assert.Single(calls);
        Assert.Equal("get_time", call.Name);
        Assert.Equal("UTC", call.Arguments["tz"]);

        // LLMAssistantVoiceTurn strips RawMatch out of the round's text before treating the remainder as
        // spoken prose -- confirm that round-trips too, so the tag JSON never reaches a listener.
        string prose = round.Replace(call.RawMatch, "").Trim();
        Assert.Equal("Let me check.", prose);
    }

    [Fact]
    public void AppendGenerateChunk_NativeToolCall_MissingArguments_DefaultsToAnEmptyObject()
    {
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["native_tool_call"] = new JObject { ["id"] = "c1", ["name"] = "hang_up" } });
        List<ToolPromptService.ParsedToolCall> calls = ToolPromptService.ParseToolCalls(output.ToString(), out List<string> malformed);
        Assert.Empty(malformed);
        ToolPromptService.ParsedToolCall call = Assert.Single(calls);
        Assert.Equal("hang_up", call.Name);
        Assert.Empty(call.Arguments.Properties());
    }

    [Fact]
    public void AppendGenerateChunk_AnthropicShapedNativeToolCall_AlsoParsesCorrectly()
    {
        // AnthropicLLMProvider.GenerateLive emits exactly this shape (id/name/arguments, arguments a parsed
        // JObject) from its own tool_use SSE handling -- today ALSO silently dropped by this same method on
        // the one-shot path (LLMProviderBackend.Generate is the shared base; AnthropicLLMProvider does not
        // override it). This fix is provider-agnostic: it reads the same three fields regardless of which
        // provider's GenerateLive produced them, so Anthropic's one-shot native tool calls now work too --
        // a correctness fix, not a behavior change to AnthropicLLMProvider.cs itself (untouched by this PR)
        // or to its streaming path (LLMStreamHelper, which never calls this method at all).
        JObject anthropicShaped = new()
        {
            ["native_tool_call"] = new JObject
            {
                ["id"] = "toolu_01abc",
                ["name"] = "get_weather",
                ["arguments"] = new JObject { ["city"] = "Seattle" }
            }
        };
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, anthropicShaped);
        List<ToolPromptService.ParsedToolCall> calls = ToolPromptService.ParseToolCalls(output.ToString(), out List<string> malformed);
        Assert.Empty(malformed);
        ToolPromptService.ParsedToolCall call = Assert.Single(calls);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Seattle", call.Arguments["city"]);
    }

    [Fact]
    public void AppendGenerateChunk_NoToolsOffered_NeverProducesANativeToolCallEvent_SoTextPassesThroughAsToday()
    {
        // Documents why LLMAssistantSendMessage/LLMAssistantTestInstruction (neither sets ExtendedLLMInput.Tools)
        // are unaffected: nothing upstream ever emits a native_tool_call chunk for a tool-less request in the
        // first place, with or without this fix, for any provider. Simulated here by simply never feeding one.
        StringBuilder output = new();
        LLMProviderBackend.AppendGenerateChunk(output, new JObject { ["chunk"] = "plain answer, no tools involved" });
        Assert.Equal("plain answer, no tools involved", output.ToString());
        Assert.Empty(ToolPromptService.ParseToolCalls(output.ToString(), out List<string> malformed));
        Assert.Empty(malformed);
    }
}
