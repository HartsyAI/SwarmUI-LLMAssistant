using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
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

    [Fact]
    public void AnEncodedNullCharacterNamesNoFileAndNothingThrows()
    {
        string root = Path.Combine(Path.GetTempPath(), "llma-output-urls");
        Func<string, string> underRoot = url => OutputUrls.ToFullPath(url, root);
        Assert.Equal(Path.Combine(root, "a%00.png"), OutputUrls.LocalPath("View/a%00.png", underRoot, _ => false));
        Assert.Null(OutputUrls.ToFullPath("View/a\0.png", root));
        // Encoded, in a message's attachment: it is read as written, found missing and skipped.
        List<LLMMediaAttachment> media = [new LLMMediaAttachment { Type = "url", Data = "View/a%00.png", MediaType = "image/png" }];
        Assert.Null(MediaResolver.ResolveForLLM(media, url => OutputUrls.LocalPath(url, underRoot, File.Exists)));
    }

    // ---- whose files are read ----

    private static readonly string OutputRoot = Path.Combine(Path.GetTempPath(), "llma-output-root");

    [Theory]
    [InlineData("View/alice/raw/a.png", true)]
    [InlineData("Output/alice/raw/a.png", true)]
    [InlineData("View/alice/raw/../a.png", true)]
    [InlineData("View/bob/raw/a.png", false)]
    [InlineData("Output/bob/raw/a.png", false)]
    [InlineData("View/alice/../bob/raw/a.png", false)]
    [InlineData("View/alice2/a.png", false)]
    [InlineData("View/Alice/a.png", false)]
    [InlineData("View/a.png", false)]
    [InlineData("View/alice", false)]
    [InlineData("View/alice/../../a.png", false)]
    [InlineData("View//etc/passwd", false)]
    public void AUserWithoutTheRightToViewOthersReadsOnlyTheirOwnFolder(string url, bool allowed)
        => Assert.Equal(allowed, OutputUrls.ViewablePath(url, OutputRoot, "alice", folderPerUser: true, viewsOthers: false) is not null);

    [Fact]
    public void TheRightToViewOthersOrOneSharedFolderOpensTheOutputRootAndNothingOutsideIt()
    {
        Assert.NotNull(OutputUrls.ViewablePath("View/bob/a.png", OutputRoot, "alice", folderPerUser: true, viewsOthers: true));
        Assert.NotNull(OutputUrls.ViewablePath("View/raw/a.png", OutputRoot, "alice", folderPerUser: false, viewsOthers: false));
        Assert.Null(OutputUrls.ViewablePath("View/../a.png", OutputRoot, "alice", folderPerUser: true, viewsOthers: true));
        Assert.Null(OutputUrls.ViewablePath("View/../a.png", OutputRoot, "alice", folderPerUser: false, viewsOthers: false));
        // A user with no name has no folder of their own.
        Assert.Null(OutputUrls.ViewablePath("View/bob/a.png", OutputRoot, "", folderPerUser: true, viewsOthers: false));
    }

    [Fact]
    public async Task WithNoUserNoOutputFileIsRead()
    {
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ImageInputResolver.ResolveAsync("View/bob/raw/a.png"));
        Assert.Contains("you may view", refused.Message);
        Assert.Null(MediaResolver.ResolveForLLM([new LLMMediaAttachment { Type = "url", Data = "View/bob/raw/a.png", MediaType = "image/png" }], (User)null));
    }

    // ---- the model of the chat behind an image ----

    [Fact]
    public void TheChatsModelIsKeptUnderTheCallsIdUntilTheCallEnds()
    {
        string id = Guid.NewGuid().ToString("N");
        RewriteChatModels.Put(id, "chat-model");
        // Every image of the call asks, so reading does not use it up.
        Assert.Equal("chat-model", RewriteChatModels.For(id));
        Assert.Equal("chat-model", RewriteChatModels.For(id));
        Assert.Null(RewriteChatModels.For(Guid.NewGuid().ToString("N")));
        Assert.Null(RewriteChatModels.For(null));
        RewriteChatModels.Remove(id);
        Assert.Null(RewriteChatModels.For(id));
        RewriteChatModels.Put(id, " ");
        Assert.Null(RewriteChatModels.For(id));
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

    private static JObject Chat(int messages, string content = "Short.")
    {
        JArray list = [];
        for (int i = 0; i < messages; i++)
        {
            list.Add(new JObject { ["id"] = $"m{i}", ["parentId"] = i == 0 ? JValue.CreateNull() : $"m{i - 1}", ["role"] = i % 2 == 0 ? "user" : "assistant", ["content"] = content });
        }
        return new JObject { ["id"] = "t", ["messages"] = list, ["activeLeafId"] = $"m{messages - 1}" };
    }

    private static JObject Window(int tokens) => new() { ["parameters"] = new JObject { ["compactThreshold"] = 0.8, ["contextWindow"] = tokens } };

    /// <summary>A message of about 500 tokens: a chat of eight would have its oldest four, about 2,000 tokens, folded into a summary.</summary>
    private static readonly string LongText = new('w', 2000);

    [Fact]
    public void ToolDefinitionsCountTowardSummarizingWhenEnoughOldTextWouldBeFoldedAway()
    {
        // 4,000 tokens of messages in a 10,000 window: not due by themselves, due with 4,500 tokens of tools.
        Assert.False(ChatEndpoints.CompactionDue(Chat(8, LongText), Window(10_000), null, "You are helpful."));
        Assert.True(ChatEndpoints.CompactionDue(Chat(8, LongText), Window(10_000), null, "You are helpful.", 4500));
        // A new chat: nothing old enough to summarize, however much the tools take.
        Assert.False(ChatEndpoints.CompactionDue(Chat(3, LongText), Window(4000), null, "You are helpful.", 3000));
        // Tools that fill the window by themselves: no summary would make the request fit, so none is written for it.
        Assert.False(ChatEndpoints.CompactionDue(Chat(8, LongText), Window(10_000), null, "You are helpful.", 10_000));
    }

    [Fact]
    public void AShortHistoryIsNotSummarizedForTheSakeOfTheTools()
    {
        // Eight short messages: a summary (up to 1,500 tokens) could be far longer than the few tokens it replaced.
        Assert.False(ChatEndpoints.CompactionDue(Chat(8), Window(4000), null, "You are helpful.", 3300));
        // The oldest four here come to 1,200 tokens, still less than a summary may be; with 1,500 tokens it is worth writing.
        Assert.False(ChatEndpoints.CompactionDue(Chat(8, new string('w', 1200)), Window(8000), null, "You are helpful.", 4500));
        Assert.True(ChatEndpoints.CompactionDue(Chat(8, new string('w', 1500)), Window(8000), null, "You are helpful.", 4500));
    }

    [Fact]
    public void AReportedCountAlreadyHoldsTheTools()
    {
        JObject chat = Chat(8);
        chat["messages"][7]["meta"] = new JObject { ["usage"] = new JObject { ["contextTokens"] = 1000 } };
        Assert.False(ChatEndpoints.CompactionDue(chat, Window(4000), null, "You are helpful.", 3300));
    }
}
