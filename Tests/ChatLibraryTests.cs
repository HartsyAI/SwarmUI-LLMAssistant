using Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The chat library's rules that need no user or storage: full-text search (ranking, snippets, limits, the cache), pin ordering, strict import, and export.</summary>
public class ChatLibraryTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A chat the way storage keeps it: these messages on one branch.</summary>
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
        return new JObject { ["id"] = "t1", ["title"] = title, ["assistantId"] = "default", ["messages"] = list, ["activeLeafId"] = parent };
    }

    private static SearchDoc Doc(string id, string title, string updatedAt, params string[] texts)
    {
        JObject thread = Thread(title, texts.Select((t, i) => (i % 2 == 0 ? "user" : "assistant", t)).ToArray());
        return ThreadSearch.BuildDoc(id, title, updatedAt, thread);
    }

    // ---- Search: what is read ----

    [Fact]
    public void Only_user_and_assistant_text_is_read()
    {
        JObject thread = Thread("t", ("user", "hello needle"), ("assistant", "plain reply"));
        JArray messages = (JArray)thread["messages"];
        messages.Add(new JObject { ["id"] = "x1", ["role"] = "tool", ["content"] = "tool secret-needle" });
        messages.Add(new JObject { ["id"] = "x2", ["role"] = "system", ["content"] = "system needle" });
        messages.Add(new JObject { ["id"] = "x3", ["role"] = "assistant", ["content"] = "", ["toolCalls"] = new JArray(new JObject { ["name"] = "web", ["result"] = "call-needle" }) });
        messages.Add(new JObject { ["id"] = "x4", ["role"] = "assistant", ["content"] = "report needle", ["kind"] = "session_report" });
        messages.Add(new JObject { ["id"] = "x5", ["role"] = "user", ["content"] = "see image", ["media"] = new JArray(new JObject { ["url"] = "/img/media-needle.png" }) });
        messages.Add(new JObject { ["id"] = "x6", ["role"] = "user", ["content"] = new JObject { ["text"] = "object needle" } });
        SearchDoc doc = ThreadSearch.BuildDoc("t1", "t", "2026", thread);
        Assert.Equal(["m0", "m1", "x5"], doc.Messages.Select(m => m.Id));
        Assert.Empty(ThreadSearch.Search([doc], "secret-needle").Hits);
        Assert.Empty(ThreadSearch.Search([doc], "call-needle").Hits);
        Assert.Empty(ThreadSearch.Search([doc], "media-needle").Hits);
        Assert.Empty(ThreadSearch.Search([doc], "report needle").Hits);
        Assert.Single(ThreadSearch.Search([doc], "hello needle").Hits);
    }

    [Fact]
    public void Every_branch_is_read_not_only_the_one_showing()
    {
        JObject thread = Thread("t", ("user", "start"), ("assistant", "first answer"));
        ((JArray)thread["messages"]).Add(new JObject { ["id"] = "alt", ["parentId"] = "m0", ["role"] = "assistant", ["content"] = "second answer on another branch" });
        SearchDoc doc = ThreadSearch.BuildDoc("t1", "t", "2026", thread);
        SearchHit hit = Assert.Single(ThreadSearch.Search([doc], "another branch").Hits);
        Assert.Equal("alt", hit.Snippets[0].MessageId);
    }

    [Fact]
    public void Text_is_capped_per_message_and_per_chat()
    {
        string big = new string('a', ThreadSearch.MaxMessageChars + 500) + "tailword";
        SearchDoc one = ThreadSearch.BuildDoc("t1", "t", "", Thread("t", ("user", big)));
        Assert.Equal(ThreadSearch.MaxMessageChars, one.Messages[0].Text.Length);
        Assert.Empty(ThreadSearch.Search([one], "tailword").Hits);
        List<(string, string)> many = [];
        for (int i = 0; i < 100; i++)
        {
            many.Add((i % 2 == 0 ? "user" : "assistant", new string('b', ThreadSearch.MaxMessageChars)));
        }
        SearchDoc capped = ThreadSearch.BuildDoc("t2", "t", "", Thread("t", many.ToArray()));
        Assert.Equal(ThreadSearch.MaxChatChars, capped.Chars);
    }

    // ---- Search: matching and ranking ----

    [Fact]
    public void A_title_hit_and_a_body_hit_are_both_found_and_the_title_ranks_first()
    {
        SearchDoc body = Doc("body", "Gardening tips", "2026-03-02", "How do I grow tomatoes?", "Water them daily.");
        SearchDoc title = Doc("title", "Tomatoes", "2026-03-01", "hello", "world");
        SearchPage page = ThreadSearch.Search([body, title], "tomatoes");
        Assert.Equal(["title", "body"], page.Hits.Select(h => h.Id));
        Assert.True(page.Hits[0].TitleMatch);
        Assert.Empty(page.Hits[0].Snippets);
        Assert.False(page.Hits[1].TitleMatch);
        Assert.Equal("user", page.Hits[1].Snippets[0].Role);
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public void Every_word_must_be_present_somewhere_in_the_chat_but_not_in_the_same_message()
    {
        SearchDoc a = Doc("a", "x", "1", "the red kite", "flies high");
        SearchDoc b = Doc("b", "x", "1", "the red kite", "no match here");
        SearchPage page = ThreadSearch.Search([a, b], "kite  FLIES");
        Assert.Equal(["a"], page.Hits.Select(h => h.Id));
    }

    [Fact]
    public void Matching_ignores_case_and_treats_the_query_as_literal_text_not_a_pattern()
    {
        SearchDoc doc = Doc("a", "x", "1", "Price is $5.00 (maybe) [a-z]+ and C++", "ok");
        Assert.Single(ThreadSearch.Search([doc], "PRICE").Hits);
        Assert.Single(ThreadSearch.Search([doc], "$5.00").Hits);
        Assert.Single(ThreadSearch.Search([doc], "[a-z]+").Hits);
        Assert.Single(ThreadSearch.Search([doc], "c++").Hits);
        Assert.Empty(ThreadSearch.Search([doc], ".*").Hits);
        Assert.Empty(ThreadSearch.Search([doc], "5x00").Hits);
    }

    [Fact]
    public void A_message_with_the_whole_phrase_outranks_one_with_the_words_apart_and_a_title_word_outranks_both()
    {
        SearchDoc apart = Doc("apart", "x", "2026-05", "blue things", "a green sky");
        SearchDoc phrase = Doc("phrase", "x", "2026-01", "I saw the blue sky today");
        SearchDoc titled = Doc("titled", "Blue things", "2026-01", "nothing", "sky");
        SearchPage page = ThreadSearch.Search([apart, phrase, titled], "blue sky");
        Assert.Equal(["titled", "phrase", "apart"], page.Hits.Select(h => h.Id));
    }

    [Fact]
    public void Ties_go_to_the_newer_chat_then_the_id_so_the_order_is_stable()
    {
        SearchDoc old = Doc("b-old", "x", "2026-01-01", "needle");
        SearchDoc newer = Doc("a-new", "x", "2026-02-01", "needle");
        SearchDoc twinA = Doc("c-a", "x", "2026-02-01", "needle");
        SearchDoc twinB = Doc("c-b", "x", "2026-02-01", "needle");
        for (int run = 0; run < 3; run++)
        {
            List<SearchDoc> shuffled = new List<SearchDoc> { old, twinB, newer, twinA }.OrderBy(_ => Guid.NewGuid()).ToList();
            Assert.Equal(["a-new", "c-a", "c-b", "b-old"], ThreadSearch.Search(shuffled, "needle").Hits.Select(h => h.Id));
        }
    }

    [Fact]
    public void Queries_that_are_too_short_or_blank_search_nothing_and_long_ones_are_cut()
    {
        SearchDoc doc = Doc("a", "x", "1", "a b c");
        Assert.Null(ThreadSearch.ParseQuery(""));
        Assert.Null(ThreadSearch.ParseQuery("  "));
        Assert.Null(ThreadSearch.ParseQuery("a"));
        Assert.Empty(ThreadSearch.Search([doc], "a").Hits);
        Assert.Equal(["ab"], ThreadSearch.ParseQuery(" ab "));
        Assert.Equal(ThreadSearch.MaxTerms, ThreadSearch.ParseQuery(string.Join(' ', Enumerable.Range(0, 30).Select(i => "w" + i))).Count);
        Assert.Equal(["same"], ThreadSearch.ParseQuery("same SAME same"));
        Assert.True(ThreadSearch.ParseQuery(new string('x', 5000))[0].Length <= 100);
    }

    // ---- Search: snippets ----

    [Fact]
    public void A_snippet_shows_context_around_the_match_and_marks_every_word()
    {
        string text = string.Join(' ', Enumerable.Range(0, 60).Select(i => "filler" + i)) + " the quick brown fox jumps " + string.Join(' ', Enumerable.Range(0, 60).Select(i => "after" + i));
        SearchDoc doc = Doc("a", "x", "1", text);
        SearchSnippet snippet = ThreadSearch.Search([doc], "quick fox").Hits[0].Snippets[0];
        Assert.StartsWith("\u2026", snippet.Text);
        Assert.EndsWith("\u2026", snippet.Text);
        Assert.True(snippet.Text.Length <= ThreadSearch.SnippetChars + 2);
        List<string> marked = snippet.Ranges.Select(r => snippet.Text.Substring(r.Start, r.Length)).ToList();
        Assert.Equal(["quick", "fox"], marked);
        Assert.Contains("brown", snippet.Text);
    }

    [Fact]
    public void A_match_at_the_start_has_no_leading_ellipsis_and_whitespace_is_flattened()
    {
        SearchDoc doc = Doc("a", "x", "1", "needle\n\n   in   a\tshort text");
        SearchSnippet snippet = ThreadSearch.Search([doc], "needle").Hits[0].Snippets[0];
        Assert.Equal("needle in a short text", snippet.Text);
        Assert.Equal([(0, 6)], snippet.Ranges);
    }

    [Fact]
    public void Overlapping_matches_are_merged_into_one_range()
    {
        SearchDoc doc = Doc("a", "x", "1", "internationalization");
        SearchSnippet snippet = ThreadSearch.Search([doc], "national nation").Hits[0].Snippets[0];
        Assert.Equal([(5, 8)], snippet.Ranges);
    }

    [Fact]
    public void A_snippet_never_splits_a_surrogate_pair()
    {
        string text = new string('x', 200) + " \U0001F600\U0001F600\U0001F600 needle " + new string('y', 300);
        SearchSnippet snippet = ThreadSearch.Search([Doc("a", "x", "1", text)], "needle").Hits[0].Snippets[0];
        for (int i = 0; i < snippet.Text.Length; i++)
        {
            if (char.IsHighSurrogate(snippet.Text[i]))
            {
                Assert.True(i + 1 < snippet.Text.Length && char.IsLowSurrogate(snippet.Text[i + 1]));
            }
        }
    }

    [Fact]
    public void A_chat_gets_at_most_three_snippets_best_first_and_counts_all_matching_messages()
    {
        string[] texts = ["one has apple", "two", "three has apple pie", "four", "five has apple", "six", "seven has apple pie too"];
        SearchHit hit = ThreadSearch.Search([Doc("a", "x", "1", texts)], "apple pie").Hits[0];
        Assert.Equal(ThreadSearch.MaxSnippetsPerChat, hit.Snippets.Count);
        Assert.Equal("m2", hit.Snippets[0].MessageId);
        Assert.Equal("m6", hit.Snippets[1].MessageId);
        Assert.Equal(4, hit.MatchedMessages);
    }

    // ---- Search: limits and paging ----

    [Fact]
    public void Results_are_paged_with_a_total_and_a_has_more_flag_and_limits_are_clamped()
    {
        List<SearchDoc> docs = Enumerable.Range(0, 120).Select(i => Doc($"id{i:000}", "x", $"2026-01-{1 + i % 28:00}T{i % 24:00}", "needle " + i)).ToList();
        SearchPage first = ThreadSearch.Search(docs, "needle", 20, 0);
        Assert.Equal(20, first.Hits.Count);
        Assert.Equal(120, first.Total);
        Assert.True(first.HasMore);
        SearchPage second = ThreadSearch.Search(docs, "needle", 20, 20);
        Assert.Empty(first.Hits.Select(h => h.Id).Intersect(second.Hits.Select(h => h.Id)));
        SearchPage last = ThreadSearch.Search(docs, "needle", 50, 100);
        Assert.Equal(20, last.Hits.Count);
        Assert.False(last.HasMore);
        Assert.Equal(ThreadSearch.MaxLimit, ThreadSearch.Search(docs, "needle", 9999, 0).Hits.Count);
        Assert.Single(ThreadSearch.Search(docs, "needle", -5, 0).Hits);
        Assert.Empty(ThreadSearch.Search(docs, "needle", 10, 100_000).Hits);
    }

    // ---- Search: the per-user cache ----

    private static JArray IndexRows(params (string Id, string Title, string Updated)[] rows)
    {
        return new JArray(rows.Select(r => new JObject { ["id"] = r.Id, ["title"] = r.Title, ["updatedAt"] = r.Updated }));
    }

    [Fact]
    public void The_cache_reads_a_chat_only_when_it_is_new_or_changed_and_forgets_deleted_ones()
    {
        ThreadSearchIndex cache = new();
        Dictionary<string, JObject> store = new() { ["a"] = Thread("A", ("user", "alpha")), ["b"] = Thread("B", ("user", "beta")) };
        List<string> reads = [];
        Func<string, JObject> load = id => { reads.Add(id); return store.GetValueOrDefault(id); };
        cache.Refresh(IndexRows(("a", "A", "1"), ("b", "B", "1")), load);
        Assert.Equal(["a", "b"], reads);
        for (int keystroke = 0; keystroke < 20; keystroke++)
        {
            cache.Refresh(IndexRows(("a", "A", "1"), ("b", "B", "1")), load);
        }
        Assert.Equal(2, reads.Count);
        store["a"] = Thread("A", ("user", "alpha changed"));
        List<SearchDoc> docs = cache.Refresh(IndexRows(("a", "A", "2"), ("b", "B", "1")), load);
        Assert.Equal(["a", "b", "a"], reads);
        Assert.Contains("changed", docs.First(d => d.Id == "a").Messages[0].Text);
        cache.Refresh(IndexRows(("a", "A", "2")), load);
        Assert.Equal(1, cache.Count);
        // A rename (same updatedAt, new title) is noticed too.
        cache.Refresh(IndexRows(("a", "Renamed", "2")), load);
        Assert.Equal(4, reads.Count);
    }

    [Fact]
    public void The_cache_leaves_out_rows_the_caller_excludes_but_keeps_them_cached()
    {
        ThreadSearchIndex cache = new();
        JArray rows = IndexRows(("a", "A", "1"), ("b", "B", "1"));
        ((JObject)rows[1])["sessionStatus"] = "archived";
        List<SearchDoc> docs = cache.Refresh(rows, id => Thread(id, ("user", "text")), row => row["sessionStatus"]?.ToString() != "archived");
        Assert.Equal(["a"], docs.Select(d => d.Id));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Two_users_caches_never_share_text()
    {
        ThreadSearchIndex one = new();
        ThreadSearchIndex two = new();
        one.Refresh(IndexRows(("a", "A", "1")), _ => Thread("A", ("user", "private-needle")));
        List<SearchDoc> forTwo = two.Refresh(IndexRows(("z", "Z", "1")), _ => Thread("Z", ("user", "nothing")));
        Assert.Empty(ThreadSearch.Search(forTwo, "private-needle").Hits);
        Assert.Equal(1, two.Count);
    }

    [Fact]
    public void A_chat_that_cannot_be_read_is_still_found_by_title()
    {
        ThreadSearchIndex cache = new();
        List<SearchDoc> docs = cache.Refresh(IndexRows(("a", "Findable title", "1")), _ => null);
        Assert.Single(ThreadSearch.Search(docs, "findable").Hits);
    }

    [Fact]
    public void When_the_index_is_full_later_chats_are_searched_by_title_only()
    {
        SearchDoc full = ThreadSearch.BuildDoc("a", "Title", "1", Thread("Title", ("user", "text needle")), charBudget: 0);
        Assert.True(full.TitleOnly);
        Assert.Empty(ThreadSearch.Search([full], "needle").Hits);
        Assert.Single(ThreadSearch.Search([full], "title").Hits);
    }

    // ---- Pin ordering ----

    [Fact]
    public void Pinned_chats_come_first_and_each_part_keeps_its_order()
    {
        JArray index = new(
            new JObject { ["id"] = "n1" },
            new JObject { ["id"] = "p1", ["pinned"] = true },
            new JObject { ["id"] = "n2" },
            new JObject { ["id"] = "p2", ["pinned"] = true },
            new JObject { ["id"] = "n3", ["pinned"] = false });
        Assert.Equal(["p1", "p2", "n1", "n2", "n3"], ThreadOrdering.PinnedFirst(index).Select(r => r["id"].ToString()));
        Assert.Equal(["n1", "p1", "n2", "p2", "n3"], index.Select(r => r["id"].ToString()));
    }

    [Fact]
    public void A_session_stays_under_its_parent_even_with_a_pin_flag_and_a_pinned_chat_leaves_its_sessions_alone()
    {
        JArray index = new(
            new JObject { ["id"] = "kid", ["parentThreadId"] = "p", ["pinned"] = true },
            new JObject { ["id"] = "p", ["pinned"] = true },
            new JObject { ["id"] = "other" });
        Assert.Equal(["p", "kid", "other"], ThreadOrdering.PinnedFirst(index).Select(r => r["id"].ToString()));
        Assert.False(ThreadOrdering.IsPinned(new JObject { ["pinned"] = "yes" }));
        Assert.False(ThreadOrdering.IsPinned(null));
    }

    // ---- Export ----

    [Fact]
    public void The_format_names_are_checked()
    {
        Assert.Equal("json", ThreadExport.NormalizeFormat(null));
        Assert.Equal("json", ThreadExport.NormalizeFormat(" JSON "));
        Assert.Equal("markdown", ThreadExport.NormalizeFormat("md"));
        Assert.Equal("markdown", ThreadExport.NormalizeFormat("Markdown"));
        Assert.Null(ThreadExport.NormalizeFormat("pdf"));
        Assert.Null(ThreadExport.NormalizeFormat(""));
        Assert.Null(ThreadExport.Build(Thread("t"), "xml", Now).Content);
    }

    [Fact]
    public void Markdown_has_the_branch_showing_in_order_and_nothing_else()
    {
        JObject thread = Thread("My chat", ("user", "question"), ("assistant", "answer"));
        ((JArray)thread["messages"]).Add(new JObject { ["id"] = "alt", ["parentId"] = "m0", ["role"] = "assistant", ["content"] = "other branch answer" });
        ((JArray)thread["messages"]).Add(new JObject { ["id"] = "tool", ["parentId"] = "m1", ["role"] = "tool", ["content"] = "tool output" });
        string md = ThreadExport.ToMarkdown(thread);
        Assert.StartsWith("# My chat", md);
        Assert.Contains("## You", md);
        Assert.Contains("## Assistant", md);
        Assert.True(md.IndexOf("question", StringComparison.Ordinal) < md.IndexOf("answer", StringComparison.Ordinal));
        Assert.DoesNotContain("other branch answer", md);
        Assert.DoesNotContain("tool output", md);
        thread["activeLeafId"] = "alt";
        Assert.Contains("other branch answer", ThreadExport.ToMarkdown(thread));
    }

    [Fact]
    public void File_names_keep_letters_digits_and_a_few_marks_and_are_never_empty()
    {
        Assert.Equal("My_chat_2-x", ThreadExport.SafeFileStem("My chat: 2-x?"));
        Assert.Equal("chat", ThreadExport.SafeFileStem("???"));
        Assert.Equal("chat", ThreadExport.SafeFileStem(null));
        Assert.DoesNotContain("..", ThreadExport.SafeFileStem("../../etc/passwd"));
        Assert.DoesNotContain("/", ThreadExport.SafeFileStem("../../etc/passwd"));
        Assert.True(ThreadExport.SafeFileStem(new string('a', 500)).Length <= 80);
        Assert.Equal("Chat_x.json", ThreadExport.Build(Thread("Chat x"), "json", Now).Filename);
        Assert.Equal("Chat_x.md", ThreadExport.Build(Thread("Chat x"), "markdown", Now).Filename);
    }

    // ---- Import ----

    private static ThreadImportResult Import(string json, Func<string, bool> assistantOk = null)
    {
        return ThreadImport.Parse(json, assistantOk ?? (_ => true), "default", Now);
    }

    private static string ExportOf(JObject thread) => ThreadExport.Build(thread, "json", Now).Content;

    [Fact]
    public void An_exported_chat_comes_back_with_the_same_text_and_shape_and_all_new_ids()
    {
        JObject original = Thread("Round trip", ("user", "first"), ("assistant", "second"), ("user", "third"));
        ((JArray)original["messages"]).Add(new JObject { ["id"] = "alt", ["parentId"] = "m0", ["role"] = "assistant", ["content"] = "branch" });
        original["activeLeafId"] = "m2";
        ThreadImportResult result = Import(ExportOf(original));
        Assert.True(result.Ok, result.Error);
        JArray messages = (JArray)result.Thread["messages"];
        Assert.Equal(4, messages.Count);
        Assert.Equal(["first", "second", "third", "branch"], messages.Select(m => m["content"].ToString()));
        Assert.Empty(messages.Select(m => m["id"].ToString()).Intersect(((JArray)original["messages"]).Select(m => m["id"].ToString())));
        Assert.Equal(4, messages.Select(m => m["id"].ToString()).Distinct().Count());
        Assert.Equal(JTokenType.Null, messages[0]["parentId"].Type);
        Assert.Equal(messages[0]["id"], messages[1]["parentId"]);
        Assert.Equal(messages[1]["id"], messages[2]["parentId"]);
        Assert.Equal(messages[0]["id"], messages[3]["parentId"]);
        Assert.Equal(messages[2]["id"].ToString(), result.Thread["activeLeafId"].ToString());
        Assert.True(result.HasBranches);
        Assert.Equal("Round trip", result.Thread["title"].ToString());
        Assert.Equal("default", result.Thread["assistantId"].ToString());
        Assert.Equal("chat", result.Thread["mode"].ToString());
        Assert.Equal(3, result.Sample.Count);
    }

    [Fact]
    public void The_exported_text_of_a_second_round_trip_is_unchanged()
    {
        JObject original = Thread("T", ("user", "a \"quoted\" \u00e9\u4e2d line\nbreak"), ("assistant", "\U0001F600 reply"));
        JObject first = Import(ExportOf(original)).Thread;
        first["id"] = "new";
        JObject second = Import(ExportOf(first)).Thread;
        Assert.Equal(((JArray)first["messages"]).Select(m => m["content"].ToString()), ((JArray)second["messages"]).Select(m => m["content"].ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"messages\":{}}")]
    [InlineData("{\"messages\":\"x\"}")]
    [InlineData("{\"messages\":[]}")]
    [InlineData("{\"messages\":[1]}")]
    [InlineData("{\"messages\":[null]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":5}]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":{\"a\":1}}]}")]
    [InlineData("{\"messages\":[{\"role\":\"system\",\"content\":\"only system\"}]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]} trailing")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}")]
    [InlineData("{\"format\":\"something-else\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}")]
    [InlineData("{\"version\":99,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}")]
    public void Malformed_files_are_refused_with_a_message(string json)
    {
        ThreadImportResult result = Import(json);
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Null(result.Thread);
    }

    [Fact]
    public void Oversized_files_too_many_messages_and_overlong_messages_are_refused()
    {
        Assert.False(Import(new string(' ', ThreadImport.MaxFileChars + 1)).Ok);
        string Many(int n) => "{\"messages\":[" + string.Join(',', Enumerable.Range(0, n).Select(i => "{\"role\":\"user\",\"content\":\"m" + i + "\"}")) + "]}";
        Assert.True(Import(Many(ThreadImport.MaxMessages)).Ok);
        ThreadImportResult tooMany = Import(Many(ThreadImport.MaxMessages + 1));
        Assert.False(tooMany.Ok);
        Assert.Contains("Too many", tooMany.Error);
        string longMessage = "{\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', ThreadImport.MaxMessageChars + 1) + "\"}]}";
        ThreadImportResult tooLong = Import(longMessage);
        Assert.False(tooLong.Ok);
        Assert.Contains("too long", tooLong.Error);
        Assert.True(Import("{\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', ThreadImport.MaxMessageChars) + "\"}]}").Ok);
    }

    [Fact]
    public void Deeply_nested_json_is_refused_not_a_stack_overflow()
    {
        string deep = "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\",\"meta\":" + new string('[', 5000) + new string(']', 5000) + "}]}";
        Assert.False(Import(deep).Ok);
    }

    [Fact]
    public void Hostile_ids_are_refused_or_replaced()
    {
        // Repeated ids.
        Assert.False(Import("{\"messages\":[{\"id\":\"x\",\"role\":\"user\",\"content\":\"a\"},{\"id\":\"x\",\"role\":\"assistant\",\"content\":\"b\"}]}").Ok);
        // A parent that is not in the file.
        Assert.False(Import("{\"messages\":[{\"id\":\"x\",\"parentId\":\"nope\",\"role\":\"user\",\"content\":\"a\"}]}").Ok);
        // A message that is its own parent, and a longer loop.
        Assert.False(Import("{\"messages\":[{\"id\":\"x\",\"parentId\":\"x\",\"role\":\"user\",\"content\":\"a\"}]}").Ok);
        Assert.False(Import("{\"messages\":[{\"id\":\"a\",\"parentId\":\"b\",\"role\":\"user\",\"content\":\"1\"},{\"id\":\"b\",\"parentId\":\"a\",\"role\":\"assistant\",\"content\":\"2\"}]}").Ok);
        // Ids that are not short text.
        Assert.False(Import("{\"messages\":[{\"id\":5,\"role\":\"user\",\"content\":\"a\"}]}").Ok);
        Assert.False(Import("{\"messages\":[{\"id\":{\"a\":1},\"role\":\"user\",\"content\":\"a\"}]}").Ok);
        Assert.False(Import("{\"messages\":[{\"id\":\"" + new string('i', 500) + "\",\"role\":\"user\",\"content\":\"a\"}]}").Ok);
        Assert.False(Import("{\"messages\":[{\"parentId\":7,\"role\":\"user\",\"content\":\"a\"}]}").Ok);
        // A hostile file that names the key a missing id would get gives a repeated id, not a loop hole.
        Assert.False(Import("{\"messages\":[{\"id\":\"\\u0001#1\",\"role\":\"user\",\"content\":\"a\"},{\"role\":\"assistant\",\"content\":\"b\"}]}").Ok);
        // Ids that look like paths, scripts or prototype keys are just labels; they never reach the new chat.
        ThreadImportResult odd = Import("{\"id\":\"../../etc\",\"messages\":[{\"id\":\"../../x\",\"role\":\"user\",\"content\":\"a\"},{\"id\":\"__proto__\",\"parentId\":\"../../x\",\"role\":\"assistant\",\"content\":\"b\"}]}");
        Assert.True(odd.Ok, odd.Error);
        Assert.Null(odd.Thread["id"]);
        Assert.All(((JArray)odd.Thread["messages"]).Select(m => m["id"].ToString()), id => Assert.Matches("^[0-9a-f]{32}$", id));
        Assert.DoesNotContain("../", odd.Thread.ToString());
    }

    [Fact]
    public void The_thread_id_and_other_embedded_state_in_the_file_is_never_carried_over()
    {
        string json = new JObject
        {
            ["id"] = "victim-thread",
            ["title"] = "T",
            ["mode"] = "code",
            ["workspace"] = "/etc",
            ["parentThreadId"] = "someone-elses",
            ["sessionStatus"] = "running",
            ["scheduleId"] = "s1",
            ["approvalMode"] = "auto",
            ["toolsEnabled"] = true,
            ["memoryExtraction"] = false,
            ["compactions"] = new JArray(new JObject { ["summary"] = "injected summary" }),
            ["params"] = new JObject { ["model"] = "x" },
            ["parameters"] = new JObject { ["model"] = "x" },
            ["assets"] = new JArray("a"),
            ["pinned"] = true,
            ["messages"] = new JArray(new JObject
            {
                ["id"] = "m",
                ["role"] = "user",
                ["content"] = "hello",
                ["media"] = new JArray(new JObject { ["url"] = "file:///C:/secrets.png" }),
                ["toolCalls"] = new JArray(new JObject { ["name"] = "shell_exec" }),
                ["rounds"] = new JArray(),
                ["editedAt"] = "x",
                ["kind"] = null
            })
        }.ToString();
        ThreadImportResult result = Import(json);
        Assert.True(result.Ok, result.Error);
        string[] allowed = ["title", "assistantId", "mode", "messages", "activeLeafId", "importedAt"];
        Assert.Equal(allowed.OrderBy(k => k), result.Thread.Properties().Select(p => p.Name).OrderBy(k => k));
        Assert.Equal("chat", result.Thread["mode"].ToString());
        JObject message = (JObject)((JArray)result.Thread["messages"])[0];
        Assert.Equal(["id", "parentId", "role", "content", "timestamp"], message.Properties().Select(p => p.Name));
        Assert.DoesNotContain("secrets", result.Thread.ToString());
        Assert.DoesNotContain("shell_exec", result.Thread.ToString());
        Assert.Contains(result.Warnings, w => w.Contains("not imported"));
    }

    [Fact]
    public void Roles_and_message_kinds_that_are_not_conversation_are_dropped_and_their_children_move_up()
    {
        string json = "{\"messages\":["
            + "{\"id\":\"1\",\"parentId\":null,\"role\":\"user\",\"content\":\"hi\"},"
            + "{\"id\":\"2\",\"parentId\":\"1\",\"role\":\"system\",\"content\":\"sys\"},"
            + "{\"id\":\"3\",\"parentId\":\"2\",\"role\":\"tool\",\"content\":\"tool out\"},"
            + "{\"id\":\"4\",\"parentId\":\"3\",\"role\":\"assistant\",\"content\":\"reply\"},"
            + "{\"id\":\"5\",\"parentId\":\"4\",\"role\":\"user\",\"content\":\"report\",\"kind\":\"session_report\"},"
            + "{\"id\":\"6\",\"parentId\":\"5\",\"role\":\"assistant\",\"content\":\"  \"}"
            + "],\"activeLeafId\":\"6\"}";
        ThreadImportResult result = Import(json);
        Assert.True(result.Ok, result.Error);
        JArray messages = (JArray)result.Thread["messages"];
        Assert.Equal(["hi", "reply"], messages.Select(m => m["content"].ToString()));
        Assert.Equal(messages[0]["id"], messages[1]["parentId"]);
        Assert.Equal(4, result.Skipped);
        Assert.Equal(messages[1]["id"].ToString(), result.Thread["activeLeafId"].ToString());
        Assert.Contains(result.Warnings, w => w.StartsWith("4 messages left out"));
    }

    [Fact]
    public void Old_style_chats_without_ids_or_parents_become_one_line_and_the_last_message_is_the_leaf()
    {
        ThreadImportResult result = Import("{\"title\":\"Old\",\"messages\":[{\"role\":\"user\",\"content\":\"a\"},{\"role\":\"assistant\",\"content\":\"b\"},{\"role\":\"user\",\"content\":\"c\"}]}");
        Assert.True(result.Ok, result.Error);
        JArray messages = (JArray)result.Thread["messages"];
        Assert.Equal(JTokenType.Null, messages[0]["parentId"].Type);
        Assert.Equal(messages[0]["id"], messages[1]["parentId"]);
        Assert.Equal(messages[1]["id"], messages[2]["parentId"]);
        Assert.Equal(messages[2]["id"].ToString(), result.Thread["activeLeafId"].ToString());
        Assert.False(result.HasBranches);
    }

    [Fact]
    public void A_leaf_that_is_not_in_the_file_falls_back_to_the_last_message()
    {
        ThreadImportResult result = Import("{\"activeLeafId\":\"ghost\",\"messages\":[{\"id\":\"1\",\"role\":\"user\",\"content\":\"a\"},{\"id\":\"2\",\"parentId\":\"1\",\"role\":\"assistant\",\"content\":\"b\"}]}");
        Assert.True(result.Ok);
        Assert.Equal(((JArray)result.Thread["messages"])[1]["id"].ToString(), result.Thread["activeLeafId"].ToString());
    }

    [Fact]
    public void Titles_are_cleaned_and_capped_and_an_empty_one_is_left_for_saving_to_derive()
    {
        string json = new JObject { ["title"] = "  Bad\u0000 \u001b[31mtitle\n\twith   gaps  " + new string('z', 500), ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "hi" }) }.ToString();
        string title = Import(json).Thread["title"].ToString();
        Assert.True(title.Length <= ThreadImport.MaxTitleChars);
        Assert.StartsWith("Bad [31mtitle with gaps", title);
        Assert.DoesNotContain(title, char.IsControl);
        Assert.Equal("", Import("{\"title\":5,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}").Thread["title"].ToString());
    }

    [Fact]
    public void An_assistant_that_does_not_exist_here_falls_back_with_a_warning()
    {
        string json = "{\"assistantId\":\"gone\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        ThreadImportResult gone = Import(json, id => id == "default");
        Assert.Equal("default", gone.Thread["assistantId"].ToString());
        Assert.Contains(gone.Warnings, w => w.Contains("assistant"));
        ThreadImportResult present = Import(json, id => true);
        Assert.Equal("gone", present.Thread["assistantId"].ToString());
        Assert.DoesNotContain(present.Warnings, w => w.Contains("assistant"));
    }

    [Fact]
    public void Timestamps_are_kept_when_real_and_replaced_when_not()
    {
        string json = "{\"messages\":[{\"role\":\"user\",\"content\":\"a\",\"timestamp\":\"2025-06-01T10:00:00Z\"},{\"role\":\"assistant\",\"content\":\"b\",\"timestamp\":\"not a date\"},{\"role\":\"user\",\"content\":\"c\",\"timestamp\":\"0001-01-01T00:00:00Z\"}]}";
        JArray messages = (JArray)Import(json).Thread["messages"];
        Assert.StartsWith("2025-06-01T10:00:00", messages[0]["timestamp"].ToString());
        Assert.Equal(Now.ToString("o"), messages[1]["timestamp"].ToString());
        Assert.Equal(Now.ToString("o"), messages[2]["timestamp"].ToString());
    }
}
