using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary><see cref="ChatEndpoints.VoiceTurnFailure"/>: what the one-shot voice route returns when a turn fails.</summary>
public class ChatEndpointsVoiceTurnFailureTests
{
    private static ToolTurnOutcome Outcome(string stopReason, string error = null) => new("", stopReason, false, null, [], Error: error);

    [Fact]
    public void AnEngineErrorKeepsItsMessage()
    {
        JObject failure = ChatEndpoints.VoiceTurnFailure(Outcome("error", "This model runs at most 4 loops."), "dory.gguf", "", []);
        Assert.False(failure["success"]!.Value<bool>());
        Assert.Equal("The model 'dory.gguf' stopped with an error: This model runs at most 4 loops.", failure["error"]!.ToString());
    }

    [Fact]
    public void AnEngineErrorWithoutAMessageStillFails()
    {
        JObject failure = ChatEndpoints.VoiceTurnFailure(Outcome("error"), "m", "Partial words", []);
        Assert.Equal("The model 'm' stopped with an error: the backend reported a failure without a message", failure["error"]!.ToString());
    }

    [Fact]
    public void ATurnWithNoSpeechAndNoDeviceActionFails()
    {
        JObject failure = ChatEndpoints.VoiceTurnFailure(Outcome(null), "m", "", []);
        Assert.StartsWith("The model produced no reply.", failure["error"]!.ToString());
    }

    [Fact]
    public void AToolOnlyTurnAndASpokenTurnSucceed()
    {
        Assert.Null(ChatEndpoints.VoiceTurnFailure(Outcome(null), "m", "", [new JObject { ["name"] = "set_volume" }]));
        Assert.Null(ChatEndpoints.VoiceTurnFailure(Outcome("length"), "m", "Cut short.", []));
    }
}
