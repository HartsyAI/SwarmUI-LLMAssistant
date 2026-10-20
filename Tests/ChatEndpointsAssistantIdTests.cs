using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Which assistant a request runs as, so a blank id can never skip the assistant's tool allowlist.</summary>
public class ChatEndpointsAssistantIdTests
{
    [Fact]
    public void AnAbsentIdFallsBackToTheActiveAssistant()
    {
        Assert.Equal("active", ChatEndpoints.ResolveAssistantId(null, () => "active"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankIdFallsBackToTheActiveAssistant(string blank)
    {
        Assert.Equal("active", ChatEndpoints.ResolveAssistantId(blank, () => "active"));
    }

    [Fact]
    public void AJsonNullIdFallsBackToTheActiveAssistant()
    {
        string requested = JObject.Parse("{\"assistantId\":null}")["assistantId"]?.ToString();
        Assert.Equal("active", ChatEndpoints.ResolveAssistantId(requested, () => "active"));
    }

    [Fact]
    public void ANamedIdIsKeptAndTheActiveOneIsNotLookedUp()
    {
        bool looked = false;
        Assert.Equal("chosen", ChatEndpoints.ResolveAssistantId("chosen", () => { looked = true; return "active"; }));
        Assert.False(looked);
    }

    [Fact]
    public void ABlankActiveAssistantFallsBackToTheDefault()
    {
        Assert.Equal(Hartsy.Extensions.LLMAssistant.AssistantConstants.DefaultId, ChatEndpoints.ResolveAssistantId("", () => ""));
    }
}
