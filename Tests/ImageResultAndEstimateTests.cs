using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The image address a tool result carries, and the room tool definitions take in a request.</summary>
public sealed class ImageResultAndEstimateTests
{
    // ---- the image address ----

    [Fact]
    public void AnOutputAddressIsEncodedSoItWorksAsAMarkdownLink()
    {
        Assert.Equal("View/local/raw/2026-11-26/2236001-a%20red%20fox%20%28snow%29.png", OutputUrls.ForMarkdown("View/local/raw/2026-11-26/2236001-a red fox (snow).png"));
        Assert.Equal("Output/raw/a%20%5B1%5D%20100%25%20%23%3F.png", OutputUrls.ForMarkdown("Output/raw/a [1] 100% #?.png"));
        // Letters of any script stay as they are, so the address is still readable.
        Assert.Equal("View/local/raw/café-狐.png", OutputUrls.ForMarkdown("View/local/raw/café-狐.png"));
        // A data URI (core's answer when files are not saved) and anything else is left alone.
        foreach (string other in new[] { "data:image/png;base64,iVBORw0KGgo=", "https://example.com/a b.png", "", null })
        {
            Assert.Equal(other, OutputUrls.ForMarkdown(other));
        }
    }

    [Theory]
    [InlineData("View/local/raw/a red fox (1) [2] 100% #?'.png")]
    [InlineData("View/local/raw/already%20encoded.png")]
    public void ReadingTheEncodingGivesBackTheFileName(string url) => Assert.Equal(url, Uri.UnescapeDataString(OutputUrls.ForMarkdown(url)));

    private static string FakeFullPath(string url) => url.Contains("..") ? null : "/out/" + url["View/".Length..];

    [Fact]
    public void AnAddressIsReadAsWrittenOrEncodedAndNeverOutOfTheOutputFolder()
    {
        HashSet<string> files = ["/out/a b.png", "/out/literal%20name.png"];
        Assert.Equal("/out/a b.png", OutputUrls.LocalPath("View/a b.png", FakeFullPath, files.Contains));
        Assert.Equal("/out/a b.png", OutputUrls.LocalPath("View/a%20b.png", FakeFullPath, files.Contains));
        // A file really named with a percent sequence is found under that name (an address saved before this change).
        Assert.Equal("/out/literal%20name.png", OutputUrls.LocalPath("View/literal%20name.png", FakeFullPath, files.Contains));
        // Encoded dots are checked after reading: the answer stays inside the folder (and names no file).
        Assert.Equal("/out/%2e%2e/secret.png", OutputUrls.LocalPath("View/%2e%2e/secret.png", FakeFullPath, _ => false));
        Assert.Equal("/out/%2e%2e/secret.png", OutputUrls.LocalPath("View/%2e%2e/secret.png", FakeFullPath, path => !path.Contains('%')));
        Assert.Null(OutputUrls.LocalPath("View/../secret.png", FakeFullPath, _ => true));
        Assert.Equal("/out/gone%20file.png", OutputUrls.LocalPath("View/gone%20file.png", FakeFullPath, files.Contains));
    }

    // ---- tool definitions in the estimate ----

    private static JObject Tool(string id, string description) => new()
    {
        ["id"] = id,
        ["name"] = "A name that is not sent",
        ["description"] = description,
        ["parameters"] = new JObject { ["type"] = "object" }
    };

    [Fact]
    public void ToolDefinitionsAreCountedAsAProviderIsGivenThem()
    {
        long one = "alpha".Length + 400 + "{\"type\":\"object\"}".Length;
        Assert.Equal(one, ContextWindowService.ToolDefinitionChars([Tool("alpha", new string('d', 400))]));
        Assert.Equal(one * 2, ContextWindowService.ToolDefinitionChars([Tool("alpha", new string('d', 400)), Tool("bravo", new string('d', 400))]));
        Assert.Equal((one + 3) / 4, ContextWindowService.EstimateToolTokens([Tool("alpha", new string('d', 400))]));
        Assert.Equal(0, ContextWindowService.EstimateToolTokens([]));
        Assert.Equal(0, ContextWindowService.EstimateToolTokens(null));
    }

    private static JObject Chat(int messages)
    {
        JArray list = [];
        for (int i = 0; i < messages; i++)
        {
            list.Add(new JObject { ["id"] = $"m{i}", ["parentId"] = i == 0 ? JValue.CreateNull() : $"m{i - 1}", ["role"] = i % 2 == 0 ? "user" : "assistant", ["content"] = "Short." });
        }
        return new JObject { ["id"] = "t", ["messages"] = list, ["activeLeafId"] = $"m{messages - 1}" };
    }

    private static JObject Window(int tokens) => new() { ["parameters"] = new JObject { ["compactThreshold"] = 0.8, ["contextWindow"] = tokens } };

    [Fact]
    public void ToolDefinitionsCountTowardSummarizingWhenThereIsSomethingOldToSummarize()
    {
        Assert.False(ChatEndpoints.CompactionDue(Chat(8), Window(4000), null, "You are helpful."));
        Assert.True(ChatEndpoints.CompactionDue(Chat(8), Window(4000), null, "You are helpful.", 3300));
        // A new chat on a small window: the tools alone fill it, and there is nothing to summarize.
        Assert.False(ChatEndpoints.CompactionDue(Chat(1), Window(4000), null, "You are helpful.", 3300));
        Assert.False(ChatEndpoints.CompactionDue(Chat(3), Window(4000), null, "You are helpful.", 3300));
        // Tools that fill the window by themselves: no summary would make the request fit, so none is written for it.
        Assert.False(ChatEndpoints.CompactionDue(Chat(8), Window(4000), null, "You are helpful.", 4000));
    }

    [Fact]
    public void AReportedCountAlreadyHoldsTheTools()
    {
        JObject chat = Chat(8);
        chat["messages"][7]["meta"] = new JObject { ["usage"] = new JObject { ["contextTokens"] = 1000 } };
        Assert.False(ChatEndpoints.CompactionDue(chat, Window(4000), null, "You are helpful.", 3300));
    }
}
