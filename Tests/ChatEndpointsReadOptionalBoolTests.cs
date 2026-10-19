using Hartsy.Extensions.LLMAssistant.WebAPI;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins <see cref="ChatEndpoints.ReadOptionalBool"/>, which reads <c>enableThinking</c> on the one-shot HTTP route: <c>true</c> or
/// <c>false</c> in any case (SwarmUI hands a JSON boolean over as "True"/"False") is the value; anything else leaves the template's own
/// default, so a request that never names it behaves as before.</summary>
public class ChatEndpointsReadOptionalBoolTests
{
    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData(" true ", true)]
    [InlineData("True", true)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("no", null)]
    public void ReadOptionalBool_TrueOrFalseIsTheValue_AnythingElseIsUnset(string input, bool? expected)
    {
        Assert.Equal(expected, ChatEndpoints.ReadOptionalBool(input));
    }
}
