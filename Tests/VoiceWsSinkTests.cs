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
    public async Task EngineFailureBeforeAnyTextIsSentAsOneErrorFrame()
    {
        (VoiceWsSink sink, List<JObject> frames, _) = Make();

        await sink.Done(new ToolTurnOutcome("", "error", false, null, [], Error: "boom"));

        JObject only = Assert.Single(frames);
        Assert.Equal("boom", only["error"]?.ToString());
        Assert.Null(only["done"]);
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
    public async Task ARoundLimitSendsANoticeThenReportsToolRoundLimit()
    {
        (VoiceWsSink sink, List<JObject> frames, _) = Make();
        await sink.Done(new ToolTurnOutcome("", null, true, "max_iterations", [], RoundLimit: "max_rounds=8"));
        Assert.Collection(frames,
            f => Assert.Equal("tool round limit reached (max_rounds=8); the model's last tool call was not run", f["notice"]!.ToString()),
            f => Assert.Equal("tool_round_limit", f["stopReason"]!.ToString()));
    }

    [Theory]
    [InlineData("tool_call", "tool_round_limit")]
    [InlineData("length", "length")]
    [InlineData("error", "error")]
    [InlineData("cancelled", "cancelled")]
    [InlineData(null, null)]
    public void TheDoneStopReasonNeverSaysToolCall(string stopReason, string expected)
        => Assert.Equal(expected, VoiceWsSink.DoneStopReason(new ToolTurnOutcome("x", stopReason, false, null, [])));

    [Fact]
    public async Task ACallIsSentOnlyWithItsResult()
    {
        (VoiceWsSink sink, List<JObject> frames, _) = Make();
        await sink.ToolCall(new JObject { ["id"] = "c1", ["name"] = "get_time", ["arguments"] = new JObject() });
        Assert.Empty(frames);
        await sink.ToolResult(new JObject { ["id"] = "c1", ["name"] = "get_time", ["result"] = new JObject { ["now"] = "14:05" } });
        Assert.Collection(frames,
            f => Assert.Equal("c1", f["native_tool_call"]!["id"]!.ToString()),
            f => Assert.Equal("c1", f["tool_result"]!["id"]!.ToString()));
    }

    [Fact]
    public async Task ACallThatWasNotRunIsNeverSent()
    {
        (VoiceWsSink sink, List<JObject> frames, JArray deviceCalls) = Make();
        await sink.ToolCall(new JObject { ["id"] = "d1", ["name"] = "set_volume", ["arguments"] = new JObject { ["level"] = 40 } });
        await sink.ToolNotRun(new JObject { ["id"] = "d1", ["name"] = "set_volume", ["result"] = new JObject { ["success"] = false } });
        await sink.Done(new ToolTurnOutcome("", "error", false, null, []));
        JObject done = Assert.Single(frames);
        Assert.True(done["done"]!.Value<bool>());
        Assert.Empty(deviceCalls);
    }

    [Fact]
    public async Task NothingIsSentOnceTheSocketIsGone()
    {
        List<JObject> frames = [];
        bool open = true;
        VoiceWsSink sink = new(frame => { frames.Add(frame); return Task.CompletedTask; }, () => open, []);
        await sink.Chunk("first");
        open = false;
        await sink.Chunk("second");
        await sink.Done(new ToolTurnOutcome("first second", null, false, null, []));
        Assert.Equal("first", Assert.Single(frames)["chunk"]!.ToString());
    }

    [Fact]
    public async Task VisibleTextIsForwardedAsChunkFrames()
    {
        (VoiceWsSink sink, List<JObject> frames, _) = Make();
        await sink.Reasoning("hidden");
        await sink.Chunk("Hello.");
        Assert.Single(frames);
        Assert.Equal("Hello.", frames[0]["chunk"]!.ToString());
    }

    [Fact]
    public async Task InlineThinkTextIsNotSentAsSpeechAcrossChunks()
    {
        (VoiceWsSink sink, List<JObject> frames, _) = Make();
        await sink.Chunk("<think>pla");
        await sink.Chunk("n</think>Noon.");
        Assert.Equal("Noon.", Assert.Single(frames)["chunk"]!.ToString());
    }

    [Fact]
    public async Task APartialTagHeldAtTheEndOfTheTurnIsSent()
    {
        (VoiceWsSink sink, List<JObject> frames, _) = Make();
        await sink.Chunk("a <");
        await sink.Done(new ToolTurnOutcome("a <", "stop", false, null, [], SpokenText: "a <"));
        Assert.Collection(frames,
            f => Assert.Equal("a ", f["chunk"]?.ToString()),
            f => Assert.Equal("<", f["chunk"]?.ToString()),
            f => Assert.True(f["done"]?.Value<bool>()));
    }
}
