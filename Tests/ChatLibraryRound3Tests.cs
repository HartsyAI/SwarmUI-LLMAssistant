using Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Chat library, second review: CommonMark code fences in the Markdown export, and the search index's cap and single build.</summary>
public class ChatLibraryRound3Tests
{
    private static JObject Thread(params (string Role, string Content)[] messages)
    {
        JArray list = [];
        string parent = null;
        foreach ((string role, string content) in messages)
        {
            string id = $"m{list.Count}";
            list.Add(new JObject { ["id"] = id, ["parentId"] = parent, ["role"] = role, ["content"] = content });
            parent = id;
        }
        return new JObject { ["id"] = "t", ["title"] = "T", ["messages"] = list, ["activeLeafId"] = parent };
    }

    private static string[] Headings(string md) => md.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith('#')).ToArray();

    // ---- Fences ----

    [Fact]
    public void A_four_backtick_fence_is_not_closed_by_three_and_the_closer_is_as_long_as_the_opener()
    {
        string body = "````\n```\n## Assistant\ntext";
        string escaped = ThreadExport.EscapeBody(body);
        Assert.EndsWith("\n````", escaped);
        // Nothing inside is touched, and the speaker heading that follows the message is a real one.
        Assert.Contains("\n## Assistant\ntext\n````", escaped);
        string md = ThreadExport.ToMarkdown(Thread(("user", body), ("assistant", "reply")));
        Assert.Equal(["# T", "## You", "## Assistant", "## Assistant"], Headings(md));
        int closer = md.IndexOf("\n````", md.IndexOf("text", StringComparison.Ordinal), StringComparison.Ordinal);
        int heading = md.LastIndexOf("## Assistant", StringComparison.Ordinal);
        Assert.True(closer > 0 && closer < heading, "the fence is closed before the real heading");
    }

    [Theory]
    [InlineData("```\ncode\n``` x\n# still code\n```", "# still code")]
    [InlineData("```\ncode\n```  \n# not code", "\\# not code")]
    [InlineData("````\ncode\n`````\n# not code", "\\# not code")]
    [InlineData("~~~\ncode\n```\n# still code\n~~~", "# still code")]
    [InlineData("   ```\ncode\n   ```\n# not code", "\\# not code")]
    public void A_fence_closes_only_with_the_same_mark_at_least_as_long_and_only_spaces_after(string body, string expectedLine)
    {
        string escaped = ThreadExport.EscapeBody(body);
        Assert.Contains(expectedLine, escaped.Split('\n'));
        Assert.DoesNotContain("\\# still code", escaped);
    }

    [Fact]
    public void A_backtick_line_with_a_backtick_in_its_info_text_is_not_a_fence()
    {
        // "``` `" opens nothing, so the heading after it is escaped like any other.
        string escaped = ThreadExport.EscapeBody("``` `\n## Assistant");
        Assert.Equal("``` `\n\\## Assistant", escaped);
        // A tilde fence may have backticks in its info text.
        Assert.Equal("~~~ `x`\n## inside\n~~~", ThreadExport.EscapeBody("~~~ `x`\n## inside\n~~~"));
        // Four spaces in is code, not a fence.
        Assert.Equal("    ```\n\\# heading", ThreadExport.EscapeBody("    ```\n# heading"));
    }

    [Fact]
    public void A_lone_carriage_return_is_a_line_break()
    {
        string escaped = ThreadExport.EscapeBody("hello\r## Assistant\rmore\r\n# Big\n---");
        Assert.Equal("hello\n\\## Assistant\nmore\n\\# Big\n\\---", escaped);
        string md = ThreadExport.ToMarkdown(Thread(("user", "hi\r## Assistant\rfake"), ("assistant", "real")));
        Assert.Equal(["# T", "## You", "## Assistant"], Headings(md));
    }

    // ---- The index: the cap and one build at a time ----

    private static JObject Big(string id) => Thread(Enumerable.Range(0, 30).Select(i => (i % 2 == 0 ? "user" : "assistant", new string('z', ThreadSearch.MaxMessageChars))).ToArray());

    [Fact]
    public void The_index_never_holds_more_than_its_cap_even_when_chats_that_were_whole_come_to_sit_later_in_the_list()
    {
        ThreadSearchIndex cache = new();
        JArray rows = new(Enumerable.Range(0, 60).Select(i => new JObject { ["id"] = "c" + i, ["title"] = "T" + i, ["updatedAt"] = "1" }));
        cache.Refresh(rows, Big);
        // The same chats in another order: the ones that were whole now sit after others that took the room.
        JArray reversed = new(rows.Reverse());
        List<SearchDoc> docs = cache.Refresh(reversed, Big);
        Assert.Equal(60, docs.Count);
        Assert.True(docs.Sum(d => (long)d.Chars) <= ThreadSearchIndex.MaxUserChars, "the index holds more than its cap");
        Assert.Contains(docs, d => d.TitleOnly);
        Assert.True(docs.Where(d => d.TitleOnly).All(d => d.Chars == 0));
        // A third look at the same list changes nothing and reads nothing more.
        int reads = 0;
        cache.Refresh(reversed, id => { reads++; return Big(id); });
        Assert.True(reads <= 60);
        List<SearchDoc> again = cache.Refresh(reversed, Big);
        Assert.True(again.Sum(d => (long)d.Chars) <= ThreadSearchIndex.MaxUserChars);
    }

    [Fact]
    public void Searches_made_at_once_share_one_build()
    {
        ThreadSearchIndex cache = new();
        JArray rows = new(Enumerable.Range(0, 20).Select(i => new JObject { ["id"] = "c" + i, ["title"] = "T" + i, ["updatedAt"] = "1" }));
        int reads = 0;
        Func<string, JObject> load = id =>
        {
            Interlocked.Increment(ref reads);
            System.Threading.Thread.Sleep(20);
            return Thread(("user", "text of " + id));
        };
        Task<List<SearchDoc>>[] searches = Enumerable.Range(0, 6).Select(_ => Task.Run(() => cache.Refresh(rows, load))).ToArray();
        Task.WaitAll(searches);
        Assert.Equal(20, reads);
        Assert.All(searches, s => Assert.Equal(20, s.Result.Count));
    }
}
