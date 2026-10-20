using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>How a streamed call's argument text becomes the frame and the dispatch check.</summary>
public class LLMMessageMappingTests
{
    [Fact]
    public void StreamedArguments_ParseValidJsonAndKeepAnythingElseRaw()
    {
        Assert.Equal("UTC", ((JObject)LLMMessageMapping.StreamedArguments("{\"tz\":\"UTC\"}"))["tz"]!.ToString());
        Assert.Empty((JObject)LLMMessageMapping.StreamedArguments("  "));
        Assert.Equal("{\"tz\":", ((JValue)LLMMessageMapping.StreamedArguments("{\"tz\":")).ToString());
        Assert.Equal("[1]", ((JValue)LLMMessageMapping.StreamedArguments("[1]")).ToString());
    }

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("{\"a\":", false)]
    [InlineData("[1,2]", false)]
    [InlineData("not json", false)]
    public void TryParseArguments_AcceptsOneObjectOrNothing(string json, bool expected)
    {
        Assert.Equal(expected, LLMMessageMapping.TryParseArguments(json, out JObject parsed, out string problem));
        Assert.NotNull(parsed);
        Assert.Equal(expected, problem is null);
    }
}
