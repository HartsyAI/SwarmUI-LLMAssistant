using Hartsy.Extensions.LLMAssistant.WebAPI;
using HartsyInference.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Unit tests for <see cref="ChatEndpoints"/>'s internal tool-registry adapter and JSON-parsing
/// helpers (both exposed to this assembly via <c>InternalsVisibleTo</c>), used by
/// <c>LLMAssistantVoiceTurnWS</c>. No <c>Session</c>/live host needed: these tests only check the registry's
/// structure (names/count) and the parser's fail-soft behavior, never the dispatch path through
/// <c>ToolExecutorService</c>, which needs a real session and thread storage.</summary>
public class ChatEndpointsToolRegistryTests
{
    private static JObject Tool(string name, string description = "desc", JObject parameters = null) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["parameters"] = parameters ?? new JObject()
    };

    [Fact]
    public void BuildToolRegistry_OneTool_RegistersItByName()
    {
        ToolRegistry registry = ChatEndpoints.BuildToolRegistry([Tool("get_time")], session: null, assistantId: null, model: null);
        Assert.Equal(1, registry.Count);
        Assert.Contains("get_time", registry.Names);
        Assert.True(registry.TryGet("get_time", out IToolHandler handler));
        Assert.Equal("desc", handler.Description);
    }

    [Fact]
    public void BuildToolRegistry_SeveralTools_RegistersEachByName()
    {
        ToolRegistry registry = ChatEndpoints.BuildToolRegistry(
            [Tool("get_time"), Tool("set_volume"), Tool("hang_up")],
            session: null, assistantId: null, model: null);
        Assert.Equal(3, registry.Count);
        Assert.Equal(["get_time", "set_volume", "hang_up"], registry.Names);
    }

    [Fact]
    public void BuildToolRegistry_ToolWithNoName_IsSkippedRatherThanThrowing()
    {
        JObject noName = new() { ["description"] = "has no name field" };
        ToolRegistry registry = ChatEndpoints.BuildToolRegistry([noName, Tool("get_time")], session: null, assistantId: null, model: null);
        Assert.Equal(1, registry.Count);
        Assert.Contains("get_time", registry.Names);
    }

    [Fact]
    public void BuildToolRegistry_Empty_RegistersNothing()
    {
        ToolRegistry registry = ChatEndpoints.BuildToolRegistry([], session: null, assistantId: null, model: null);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void ParseJsonOrEmpty_ValidObject_ParsesIt()
    {
        JObject result = ChatEndpoints.ParseJsonOrEmpty("{\"success\":true,\"value\":5}");
        Assert.True(result["success"]?.Value<bool>());
        Assert.Equal(5, result["value"]?.Value<int>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{unterminated")]
    [InlineData("[1,2,3]")] // a JSON array parses but isn't a JObject -- also treated as not-usable here
    public void ParseJsonOrEmpty_NullBlankOrMalformed_ReturnsEmptyObjectInstead(string json)
    {
        JObject result = ChatEndpoints.ParseJsonOrEmpty(json);
        Assert.NotNull(result);
        Assert.Empty(result.Properties());
    }
}
