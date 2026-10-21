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
    public async Task AFinishedReplyEndsWithItsDoneFrame()
    {
        (ChatWsSink sink, List<JObject> frames) = Make();

        await sink.Done(new ToolTurnOutcome("Hi.", "stop", false, null, []));

        JObject only = Assert.Single(frames);
        Assert.True(only["done"]?.Value<bool>());
        Assert.Equal("stop", only["stopReason"]?.ToString());
    }
}
