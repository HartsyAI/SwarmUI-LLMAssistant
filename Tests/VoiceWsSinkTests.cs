using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The voice socket's frames, in the shape AudioLab reads: <c>native_tool_call</c>, <c>tool_result</c>, and a <c>done</c> frame
/// that lists the device actions the turn took.</summary>
public class VoiceWsSinkTests
{
    private static (VoiceWsSink Sink, List<JObject> Frames, JArray DeviceCalls) Make()
    {
        List<JObject> frames = [];
        JArray deviceCalls = [];
        VoiceWsSink sink = new(frame => { frames.Add(frame); return Task.CompletedTask; }, () => true, deviceCalls);
        return (sink, frames, deviceCalls);
    }

    [Fact]
    public async Task ToolCallAndResultUseTheVoiceFrameShapes()
    {
        (VoiceWsSink sink, List<JObject> frames, _) = Make();
        await sink.ToolCall(new JObject { ["id"] = "c1", ["name"] = "get_time", ["arguments"] = new JObject { ["tz"] = "UTC" } });
        await sink.ToolResult(new JObject { ["id"] = "c1", ["name"] = "get_time", ["result"] = new JObject { ["now"] = "14:05" } });

        JObject call = (JObject)frames[0]["native_tool_call"]!;
        Assert.Equal("c1", call["id"]!.ToString());
        Assert.Equal("UTC", call["arguments"]!["tz"]!.ToString());
        JObject result = (JObject)frames[1]["tool_result"]!;
        Assert.Equal("14:05", result["result"]!["now"]!.ToString());
    }

    [Fact]
    public async Task ASuccessfulDeviceActionIsListedInTheDoneFrame()
    {
        (VoiceWsSink sink, List<JObject> frames, JArray deviceCalls) = Make();
        await sink.ToolCall(new JObject { ["id"] = "d1", ["name"] = "set_volume", ["arguments"] = new JObject { ["level"] = 40 } });
        await sink.ToolResult(new JObject { ["id"] = "d1", ["name"] = "set_volume", ["result"] = new JObject { ["success"] = true } });
        await sink.Done(new ToolTurnOutcome("Turned it up.", "stop", false, null, []));

        JObject done = frames[^1];
        Assert.True(done["done"]!.Value<bool>());
        Assert.Equal("Turned it up.", done["full_text"]!.ToString());
        Assert.Equal("set_volume", done["toolCalls"]![0]!["name"]!.ToString());
        Assert.Equal(40, done["toolCalls"]![0]!["arguments"]!["level"]!.Value<int>());
        Assert.Single(deviceCalls);
    }

    [Fact]
    public async Task AFailedDeviceActionIsNotListed()
    {
        (VoiceWsSink sink, _, JArray deviceCalls) = Make();
        await sink.ToolCall(new JObject { ["id"] = "d1", ["name"] = "set_volume", ["arguments"] = new JObject() });
        await sink.ToolResult(new JObject { ["id"] = "d1", ["name"] = "set_volume", ["result"] = new JObject { ["success"] = false, ["error"] = "no" } });
        Assert.Empty(deviceCalls);
    }

    [Fact]
    public async Task ARoundLimitReportsAsToolCallStop()
    {
        (VoiceWsSink sink, List<JObject> frames, _) = Make();
        await sink.Done(new ToolTurnOutcome("", null, true, "max_iterations", []));
        Assert.Equal("tool_call", frames[^1]["stopReason"]!.ToString());
    }
}
