using System.Text;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>End-to-end simulation of <c>ChatEndpoints.LLMAssistantVoiceTurn</c>'s agentic loop shape (parse →
/// dispatch → <c>ToolPromptService.FormatToolResult</c> fed back as a <c>LLMRoles.User</c> turn → ask again),
/// run against <see cref="LLMProviderBackend.AppendGenerateChunk"/> instead of a real provider/session. The
/// real endpoint needs a live <c>Session</c>, <c>ToolExecutorService</c>, thread/assistant storage, etc. that
/// have no fake here, so this mirrors its loop body directly rather than calling it, to pin the one behavior
/// that changed: whether a native tool call reaches that loop at all.</summary>
public class OneShotToolLoopSimulationTests
{
    /// <summary>One round of the real loop's shape: accumulate a scripted <c>GenerateLive</c> event sequence
    /// through <see cref="LLMProviderBackend.AppendGenerateChunk"/>, exactly as <c>Generate(ExtendedLLMInput)</c>
    /// does.</summary>
    private static string Accumulate(params JObject[] events)
    {
        StringBuilder output = new();
        foreach (JObject e in events)
        {
            LLMProviderBackend.AppendGenerateChunk(output, e);
        }
        return output.ToString();
    }

    [Fact]
    public void NativeToolCallingOn_DispatchesTheCallAndFeedsTheResultBack()
    {
        // Round 1: StructuredToolCalling is on, so the provider streams a native_tool_call event instead of
        // a literal tag. Before this fix, AppendGenerateChunk (and everything built on it) saw only "Let me
        // check. " and the call vanished -- calls.Count would have been 0 and the turn would have ended with
        // no tool ever run.
        string round1 = Accumulate(
            new JObject { ["chunk"] = "Let me check. " },
            new JObject { ["native_tool_call"] = new JObject { ["id"] = "call_1", ["name"] = "get_time", ["arguments"] = new JObject() } });

        List<ToolPromptService.ParsedToolCall> calls = ToolPromptService.ParseToolCalls(round1, out List<string> malformed);
        Assert.Empty(malformed);
        ToolPromptService.ParsedToolCall call = Assert.Single(calls);
        Assert.Equal("get_time", call.Name);

        // Same dispatch LLMAssistantVoiceTurn runs for a tag-scanned call -- a fake "tool" here instead of
        // ToolExecutorService.ExecuteTool, which needs a live session/thread store.
        JObject result = new() { ["success"] = true, ["time"] = "noon" };
        string toolResultTurn = ToolPromptService.FormatToolResult(call.Name, result);
        Assert.Equal("<tool_result name=\"get_time\">{\"success\":true,\"time\":\"noon\"}</tool_result>", toolResultTurn);

        // Round 2: the model sees the result and answers in prose, same shape as round 1 but with no call.
        string round2 = Accumulate(new JObject { ["chunk"] = "It is noon." });
        List<ToolPromptService.ParsedToolCall> round2Calls = ToolPromptService.ParseToolCalls(round2, out List<string> round2Malformed);
        Assert.Empty(round2Calls);
        Assert.Empty(round2Malformed);

        string spoken = round1.Replace(call.RawMatch, "") + round2;
        Assert.Equal("Let me check. It is noon.", spoken);
    }

    [Fact]
    public void NativeToolCallingOff_StillUsesTheTagScan_AsToday()
    {
        // StructuredToolCalling off (or a provider with no native support at all): the model emits the
        // <tool_call> tag as literal text inside ordinary "chunk" events -- there is no native_tool_call
        // event in this story at all. AppendGenerateChunk's chunk-handling branch is byte-for-byte what it
        // was before this fix, so this is a regression pin, not new behavior.
        string round1 = Accumulate(
            new JObject { ["chunk"] = "Let me check. " },
            new JObject { ["chunk"] = "<tool_call>{\"name\":\"get_time\",\"arguments\":{}}</tool_call>" });

        List<ToolPromptService.ParsedToolCall> calls = ToolPromptService.ParseToolCalls(round1, out List<string> malformed);
        Assert.Empty(malformed);
        ToolPromptService.ParsedToolCall call = Assert.Single(calls);
        Assert.Equal("get_time", call.Name);
        Assert.Equal("Let me check.", round1.Replace(call.RawMatch, "").Trim());
    }
}
