using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins <see cref="ChatEndpoints.ReadLoops"/>, the wire contract of the <c>loops</c> body field on the websocket chat and
/// voice routes: a positive JSON integer is the request's loop count; anything else (absent, null, zero, negative, fractional, a
/// string, or an integer beyond <see cref="int"/>) means "not set by this request", so the saved <c>loops</c> parameter and then
/// the local backend's <c>RecurrentLoops</c> default apply. This deliberately differs from the engine's own OpenAI route, which
/// rejects a bad value with an error.</summary>
public class ChatEndpointsReadLoopsTests
{
    [Theory]
    [InlineData("{}", 0)]
    [InlineData("{\"loops\": null}", 0)]
    [InlineData("{\"loops\": 0}", 0)]
    [InlineData("{\"loops\": -1}", 0)]
    [InlineData("{\"loops\": 2.5}", 0)]
    [InlineData("{\"loops\": \"2\"}", 0)]
    [InlineData("{\"loops\": 99999999999}", 0)]
    [InlineData("{\"loops\": 1}", 1)]
    [InlineData("{\"loops\": 4}", 4)]
    public void ReadLoops_TakesOnlyAPositiveInteger(string json, int expected)
    {
        Assert.Equal(expected, ChatEndpoints.ReadLoops(JObject.Parse(json)));
    }

    [Fact]
    public void ReadLoops_NullInputMeansNotSet()
    {
        Assert.Equal(0, ChatEndpoints.ReadLoops(null));
    }
}
