using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The chat socket's closing frame: an engine failure before any text is the whole reply, so it goes as one error frame.</summary>
public class ChatWsSinkTests
{
    private static (ChatWsSink Sink, List<JObject> Frames) Make()
    {
        List<JObject> frames = [];
        ChatWsSink sink = new(frame => { frames.Add(frame); return Task.CompletedTask; }, () => true);
        return (sink, frames);
    }

    [Fact]
    public async Task EngineFailureBeforeAnyTextIsSentAsOneErrorFrame()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.Done(new ToolTurnOutcome("", "error", false, null, [], Error: "boom"));

        JObject only = Assert.Single(frames);
        Assert.Equal("boom", only["error"]?.ToString());
        Assert.Null(only["done"]);
    }

    [Fact]
    public async Task EngineFailureAfterTextKeepsTheDoneFrame()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.Done(new ToolTurnOutcome("partial", "error", false, null, [], SpokenText: "partial", Error: "boom"));

        JObject only = Assert.Single(frames);
        Assert.True(only["done"]?.Value<bool>());
        Assert.Equal("partial", only["full_text"]?.ToString());
        Assert.Equal("error", only["stopReason"]?.ToString());
    }

    [Fact]
    public async Task TheDoneFrameCarriesTheTurnsTokenUsageWhenThereIsSome()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();
        Hartsy.Extensions.LLMAssistant.LLMs.LLMUsage usage = new();
        usage.Add(100, 20);

        await sink.Done(new ToolTurnOutcome("hi", null, false, null, [], SpokenText: "hi", Usage: usage));

        JObject only = Assert.Single(frames);
        Assert.Equal(100, only["usage"]!.Value<long>("promptTokens"));
        Assert.Equal(120, only["usage"]!.Value<long>("contextTokens"));
    }

    [Fact]
    public async Task TheDoneFrameHasNoUsageWhenTheProviderReportedNone()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.Done(new ToolTurnOutcome("hi", null, false, null, [], SpokenText: "hi"));

        Assert.Null(Assert.Single(frames)["usage"]);
    }

    [Fact]
    public async Task ReasoningOnlyEngineFailureIsAnErrorFrame()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.Done(new ToolTurnOutcome("<think>Hmm.</think>", "error", false, null, [], SpokenText: "", Error: "boom"));

        JObject only = Assert.Single(frames);
        Assert.Equal("boom", only["error"]?.ToString());
        Assert.Null(only["done"]);
    }

    [Fact]
    public async Task EngineFailureAfterAShownToolResultKeepsTheDoneFrame()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.ToolResult(new JObject { ["id"] = "c1" });
        await sink.Done(new ToolTurnOutcome("", "error", false, null, [], SpokenText: "", Error: "boom"));

        Assert.Equal(2, frames.Count);
        Assert.True(frames[1]["done"]?.Value<bool>());
    }

    [Fact]
    public async Task PartialReplyEngineFailureKeepsItsDoneFrameWithTheMessage()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.Done(new ToolTurnOutcome("Partial.", "error", false, null, [], SpokenText: "Partial.", Error: "boom"));

        JObject only = Assert.Single(frames);
        Assert.True(only["done"]?.Value<bool>());
        Assert.Equal("Partial.", only["full_text"]?.ToString());
        Assert.Equal("error", only["stopReason"]?.ToString());
        Assert.Equal("boom", only["failure"]?.ToString());
    }

    [Fact]
    public async Task AFinishedReplyEndsWithItsDoneFrame()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.Done(new ToolTurnOutcome("Hi.", "stop", false, null, []));

        JObject only = Assert.Single(frames);
        Assert.True(only["done"]?.Value<bool>());
        Assert.Equal("stop", only["stopReason"]?.ToString());
    }

    [Fact]
    public async Task AnApprovalRequestGoesToTheClientAsAToolApprovalFrame_AndTheSocketCanAsk()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.ToolApproval(new JObject { ["approvalId"] = "a1", ["id"] = "call-1", ["name"] = "file_write" });

        Assert.True(sink.CanApprove);
        JObject only = Assert.Single(frames);
        Assert.Equal("a1", only["tool_approval"]?["approvalId"]?.ToString());
        Assert.Equal("file_write", only["tool_approval"]?["name"]?.ToString());
    }
}
