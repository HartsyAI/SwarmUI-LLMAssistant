using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins <see cref="ChatEndpoints.ReadPositiveInt"/>, which reads the <c>loops</c> body field on the websocket chat and
/// voice routes and the saved <c>loops</c> parameter: a positive JSON integer is the value; anything else (absent, null, zero,
/// negative, fractional, a string, or an integer beyond <see cref="int"/>) means "not set", so the next default in line applies
/// (the saved parameter, then the local backend's <c>RecurrentLoops</c>). This deliberately differs from the engine's own OpenAI
/// route, which rejects a bad value with an error.</summary>
public class ChatEndpointsReadPositiveIntTests
{
    [Theory]
    [InlineData("{}", 0)]
    [InlineData("{\"other\": 2}", 0)]
    [InlineData("{\"loops\": null}", 0)]
    [InlineData("{\"loops\": 0}", 0)]
    [InlineData("{\"loops\": -1}", 0)]
    [InlineData("{\"loops\": 2.5}", 0)]
    [InlineData("{\"loops\": \"2\"}", 0)]
    [InlineData("{\"loops\": 99999999999}", 0)]
    [InlineData("{\"loops\": 1}", 1)]
    [InlineData("{\"loops\": 4}", 4)]
    public void TakesOnlyAPositiveIntegerThatFits(string json, int expected)
    {
        Assert.Equal(expected, ChatEndpoints.ReadPositiveInt(JObject.Parse(json), "loops"));
    }

    [Fact]
    public void NullObjectMeansNotSet()
    {
        Assert.Equal(0, ChatEndpoints.ReadPositiveInt(null, "loops"));
    }
}
