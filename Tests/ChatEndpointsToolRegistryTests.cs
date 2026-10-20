using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The model calls a tool by its id, so the engine's tool definitions are named by id; the display name is for people.</summary>
public class ChatEndpointsToolRegistryTests
{
    private static JObject Tool(string id, string name = null, string description = "desc", JObject parameters = null) => new()
    {
        ["id"] = id,
        ["name"] = name ?? id.Replace('_', ' ') + " (display)",
        ["description"] = description,
        ["parameters"] = parameters ?? new JObject(),
    };

    [Fact]
    public void EngineToolsAreNamedByIdNotDisplayName()
    {
        var defs = LLMMessageMapping.ToEngineTools([Tool("generate_image")]);
        Assert.Equal(["generate_image"], defs.Select(d => d.Name));
    }

    [Fact]
    public void ToolWithoutAnIdFallsBackToItsName()
    {
        JObject legacy = new() { ["name"] = "get_time", ["description"] = "d" };
        Assert.Equal(["get_time"], LLMMessageMapping.ToEngineTools([legacy]).Select(d => d.Name));
    }

    [Fact]
    public void SchemaIsCarriedAsJsonText()
    {
        JObject parameters = new() { ["type"] = "object", ["properties"] = new JObject() };
        string schema = LLMMessageMapping.ToEngineTools([Tool("hang_up", parameters: parameters)]).Single().JsonSchema;
        Assert.Equal(parameters.ToString(Newtonsoft.Json.Formatting.None), schema);
    }

    [Fact]
    public void NoToolsGivesNoDefinitions()
        => Assert.Empty(LLMMessageMapping.ToEngineTools([]));
}
