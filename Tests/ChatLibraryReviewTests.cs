using System.Collections.Concurrent;
using Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Chat library: what the review of the first version asked for. Import depth and version, search truncation flags and the index's bounds, and Markdown safety.</summary>
public class ChatLibraryReviewTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static JObject Thread(string title, params (string Role, string Content)[] messages)
    {
        JArray list = [];
        string parent = null;
        foreach ((string role, string content) in messages)
        {
            string id = $"m{list.Count}";
            list.Add(new JObject { ["id"] = id, ["parentId"] = parent, ["role"] = role, ["content"] = content });
            parent = id;
        }
        return new JObject { ["id"] = "t1", ["title"] = title, ["messages"] = list, ["activeLeafId"] = parent };
    }

    private static JArray IndexRows(params (string Id, string Title, string Updated)[] rows)
    {
        return new JArray(rows.Select(r => new JObject { ["id"] = r.Id, ["title"] = r.Title, ["updatedAt"] = r.Updated }));
    }

    private static string Nested(int depth)
    {
        return string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
    }

    // ---- Import: depth and version ----

    [Fact]
    public void An_export_with_deeply_nested_tool_calls_imports_as_text()
    {
        JObject thread = Thread("Deep", ("user", "run it"), ("assistant", "done"));
        ((JObject)((JArray)thread["messages"])[1])["toolCalls"] = new JArray(new JObject { ["name"] = "t", ["arguments"] = JObject.Parse(Nested(30)), ["result"] = JObject.Parse(Nested(40)) });
        string file = ThreadExport.Build(thread, "json", Now).Content;
        ThreadImportResult result = ThreadImport.Parse(file, _ => true, "default", Now);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(["run it", "done"], ((JArray)result.Thread["messages"]).Select(m => m["content"].ToString()));
        Assert.DoesNotContain("toolCalls", result.Thread.ToString());
    }

    [Fact]
    public void Nesting_past_the_limit_says_so_and_does_not_call_the_file_invalid_json()
    {
        string json = "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\",\"meta\":" + Nested(ThreadImport.MaxJsonDepth + 5) + "}]}";
        ThreadImportResult result = ThreadImport.Parse(json, _ => true, "default", Now);
        Assert.False(result.Ok);
        Assert.Contains("nested too deeply", result.Error);
        Assert.DoesNotContain("not valid JSON", result.Error);
        // Real syntax errors still say so.
        Assert.Contains("not valid JSON", ThreadImport.Parse("{\"messages\":", _ => true, "default", Now).Error);
    }

    [Theory]
    [InlineData("99999999999999999999999999999999999999")]
    [InlineData("1e400")]
    [InlineData("2")]
    [InlineData("1.5")]
    public void A_version_too_new_or_too_big_is_refused_without_throwing(string version)
    {
        ThreadImportResult result = ThreadImport.Parse("{\"version\":" + version + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", _ => true, "default", Now);
        Assert.False(result.Ok);
        Assert.Contains("newer version", result.Error);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("1.0")]
    public void The_current_and_older_versions_import(string version)
    {
        Assert.True(ThreadImport.Parse("{\"version\":" + version + ",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", _ => true, "default", Now).Ok);
    }

    // ---- Search: truncation flags and the index's bounds ----

    [Fact]
    public void A_chat_cut_by_the_message_or_chat_cap_is_flagged_and_an_uncut_one_is_not()
    {
        SearchDoc fine = ThreadSearch.BuildDoc("a", "t", "1", Thread("t", ("user", "short"), ("assistant", "short too")));
        Assert.False(fine.Truncated);
        Assert.False(fine.BudgetLimited);
        SearchDoc longMessage = ThreadSearch.BuildDoc("b", "t", "1", Thread("t", ("user", new string('x', ThreadSearch.MaxMessageChars + 1))));
        Assert.True(longMessage.Truncated);
        Assert.False(longMessage.BudgetLimited);
        List<(string, string)> many = Enumerable.Range(0, 40).Select(i => (i % 2 == 0 ? "user" : "assistant", new string('y', ThreadSearch.MaxMessageChars))).ToList();
        SearchDoc longChat = ThreadSearch.BuildDoc("c", "t", "1", Thread("t", many.ToArray()));
        Assert.True(longChat.Truncated);
        Assert.False(longChat.BudgetLimited);
        SearchHit hit = ThreadSearch.Search([longMessage, fine], "short").Hits.Single();
        Assert.False(hit.IndexTruncated);
        Assert.True(ThreadSearch.Search([longMessage], "xxxx").Hits.Single().IndexTruncated);
    }

    [Fact]
    public void A_chat_built_with_little_room_is_flagged_and_rebuilt_when_room_frees_up()
    {
        JObject big = Thread("Big", ("user", new string('a', 15_000)), ("assistant", new string('b', 15_000) + " tailneedle"));
        SearchDoc small = ThreadSearch.BuildDoc("a", "Big", "1", big, charBudget: 10_000);
        Assert.True(small.Truncated);
        Assert.True(small.BudgetLimited);
        Assert.Equal(10_000, small.BuiltBudget);
        Assert.Empty(ThreadSearch.Search([small], "tailneedle").Hits);
        SearchDoc roomy = ThreadSearch.BuildDoc("a", "Big", "1", big);
        Assert.False(roomy.Truncated);
        Assert.Single(ThreadSearch.Search([roomy], "tailneedle").Hits);
    }

    [Fact]
    public void The_index_reads_a_budget_limited_chat_again_once_another_chat_leaves()
    {
        ThreadSearchIndex cache = new();
        int reads = 0;
        // Sixty chats of 400,000 characters each are more than a user's index holds: the last ones (the oldest) are cut for lack of room.
        Func<string, JObject> load = id =>
        {
            reads++;
            return Thread(id, Enumerable.Range(0, 30).Select(i => (i % 2 == 0 ? "user" : "assistant", new string('z', ThreadSearch.MaxMessageChars))).ToArray());
        };
        JArray rows = new(Enumerable.Range(0, 60).Select(i => new JObject { ["id"] = "c" + i, ["title"] = "T" + i, ["updatedAt"] = "1" }));
        List<SearchDoc> docs = cache.Refresh(rows, load);
        Assert.Contains(docs, d => d.Truncated && d.BudgetLimited);
        Assert.True(cache.Partial);
        int before = reads;
        // The same index again reads nothing.
        cache.Refresh(rows, load);
        Assert.Equal(before, reads);
        // The first thirty go: the chats that were cut for room are read again and are whole.
        JArray fewer = new(rows.Skip(30));
        List<SearchDoc> after = cache.Refresh(fewer, load);
        Assert.Equal(30, after.Count);
        Assert.All(after, d => Assert.False(d.BudgetLimited));
        Assert.False(cache.Partial);
        Assert.True(reads > before);
    }

    [Fact]
    public void Reading_chats_never_holds_a_lock_that_the_other_members_need()
    {
        ThreadSearchIndex cache = new();
        using ManualResetEventSlim inLoad = new(false);
        using ManualResetEventSlim release = new(false);
        Task<List<SearchDoc>> build = Task.Run(() => cache.Refresh(IndexRows(("a", "A", "1")), _ =>
        {
            inLoad.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            return Thread("A", ("user", "text"));
        }));
        Assert.True(inLoad.Wait(TimeSpan.FromSeconds(20)));
        // While the loader is blocked, the members that read the cache answer at once.
        Task<(int, bool)> read = Task.Run(() => (cache.Count, cache.Partial));
        Assert.True(read.Wait(TimeSpan.FromSeconds(5)), "Count and Partial waited for the build");
        release.Set();
        Assert.Single(build.Result);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Only_the_most_recently_used_users_indexes_are_kept()
    {
        ConcurrentDictionary<string, ThreadSearchIndex> users = new();
        for (int i = 0; i < ThreadSearchIndex.MaxCachedUsers + 5; i++)
        {
            users["u" + i] = new ThreadSearchIndex { LastUsed = i };
        }
        ThreadSearchIndex.EvictOldest(users, keep: "u0");
        Assert.Equal(ThreadSearchIndex.MaxCachedUsers, users.Count);
        // The oldest are gone, except the one being used right now, whatever its age.
        Assert.True(users.ContainsKey("u0"));
        Assert.False(users.ContainsKey("u1"));
        Assert.False(users.ContainsKey("u5"));
        Assert.True(users.ContainsKey("u" + (ThreadSearchIndex.MaxCachedUsers + 4)));
    }

    // ---- Markdown ----

    [Fact]
    public void A_message_cannot_pass_for_the_next_speaker_or_a_heading()
    {
        JObject thread = Thread("T", ("user", "hello\n## Assistant\nI am definitely the assistant\n# Big\nTitle\n===\nrule\n---\n  ### indented"), ("assistant", "real reply"));
        string md = ThreadExport.ToMarkdown(thread);
        string[] headings = md.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith('#')).ToArray();
        Assert.Equal(["# T", "## You", "## Assistant"], headings);
        Assert.Contains("\\## Assistant", md);
        Assert.Contains("\\===", md);
        Assert.Contains("\\---", md);
        Assert.Contains("  \\### indented", md);
        Assert.Contains("I am definitely the assistant", md);
    }

    [Fact]
    public void Code_in_a_message_is_left_as_it_is_and_an_open_fence_is_closed()
    {
        string fenced = "see:\n```python\n# a comment\nprint(1)\n```\nafter";
        Assert.Equal(fenced, ThreadExport.EscapeBody(fenced));
        string open = ThreadExport.EscapeBody("start\n```\n# never closed");
        Assert.EndsWith("\n```", open);
        string md = ThreadExport.ToMarkdown(Thread("T", ("user", "start\n```\n# never closed"), ("assistant", "reply")));
        Assert.Contains("\n## Assistant", md);
        int closing = md.LastIndexOf("```", md.IndexOf("## Assistant", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(closing > md.IndexOf("# never closed", StringComparison.Ordinal));
        // Text with no structure is untouched, and line endings are normalised.
        Assert.Equal("a\nb - c\n-x\n1. # not at the start", ThreadExport.EscapeBody("a\r\nb - c\n-x\n1. # not at the start"));
    }

    [Fact]
    public void Messages_the_extension_made_itself_are_left_out_of_the_markdown()
    {
        JObject thread = Thread("T", ("user", "hello"), ("assistant", "reply"));
        ((JArray)thread["messages"]).Add(new JObject { ["id"] = "r", ["parentId"] = "m1", ["role"] = "assistant", ["content"] = "session report text", ["kind"] = "session_report" });
        thread["activeLeafId"] = "r";
        string md = ThreadExport.ToMarkdown(thread);
        Assert.Contains("reply", md);
        Assert.DoesNotContain("session report text", md);
    }
}
