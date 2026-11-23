using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;

/// <summary>One message's plain text, as the search reads it.</summary>
public sealed record SearchMessage(string Id, string Role, string Text);

/// <summary>What the search knows about one chat: its title and the plain text of its user and assistant messages (every branch).</summary>
public sealed class SearchDoc
{
    public string Id { get; init; }
    public string Title { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public List<SearchMessage> Messages { get; init; } = [];
    /// <summary>How many characters of message text this chat holds in the index.</summary>
    public int Chars { get; init; }
    /// <summary>True when the text of the chat was left out (the index was full), so only its title can match.</summary>
    public bool TitleOnly { get; init; }
}

/// <summary>A piece of a message around a match. <see cref="Ranges"/> are (start, length) pairs into <see cref="Text"/>, sorted and not overlapping, for the page to highlight.</summary>
public sealed record SearchSnippet(string MessageId, string Role, string Text, List<(int Start, int Length)> Ranges);

/// <summary>A chat that matched.</summary>
public sealed class SearchHit
{
    public string Id { get; init; }
    public string Title { get; init; }
    public string UpdatedAt { get; init; }
    public bool TitleMatch { get; init; }
    /// <summary>How many messages contain at least one of the words.</summary>
    public int MatchedMessages { get; init; }
    public List<SearchSnippet> Snippets { get; init; } = [];
    public int Score { get; init; }
}

/// <summary>One page of results, best first.</summary>
public sealed class SearchPage
{
    public List<SearchHit> Hits { get; init; } = [];
    /// <summary>How many chats matched in all.</summary>
    public int Total { get; init; }
    public int Offset { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>Full-text search over a user's chats: titles and the text people and the assistant wrote. Pure: it works on <see cref="SearchDoc"/>s, so it needs no user or storage.
/// <para>A query is split into words; a chat matches when every word is in its title or in some message (not necessarily the same one). Matching is a literal, case-insensitive
/// substring match (never a pattern), done on the original text, so match positions are real positions.</para></summary>
public static class ThreadSearch
{
    /// <summary>Longest query read; the rest is dropped.</summary>
    public const int MaxQueryChars = 200;
    /// <summary>Most words in a query that count; the rest are dropped.</summary>
    public const int MaxTerms = 8;
    /// <summary>Shortest query (after trimming) that searches; one character would match nearly everything.</summary>
    public const int MinQueryChars = 2;
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    /// <summary>Most chats skipped by <c>offset</c>.</summary>
    public const int MaxOffset = 5000;
    public const int MaxSnippetsPerChat = 3;
    /// <summary>Length of the text shown around a match.</summary>
    public const int SnippetChars = 160;
    /// <summary>How much of the text before the first match a snippet starts with.</summary>
    private const int SnippetLead = 40;
    /// <summary>Most characters of one message the index keeps, and of one chat.</summary>
    public const int MaxMessageChars = 20_000;
    public const int MaxChatChars = 400_000;

    /// <summary>The words of a query: split on whitespace, no repeats, each cut to 100 characters, at most <see cref="MaxTerms"/>. Null when the query is too short to search.</summary>
    public static List<string> ParseQuery(string raw)
    {
        string query = (raw ?? "").Trim();
        if (query.Length > MaxQueryChars)
        {
            query = query[..MaxQueryChars];
        }
        if (query.Length < MinQueryChars)
        {
            return null;
        }
        List<string> terms = [];
        foreach (string word in query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
        {
            string term = word.Length > 100 ? word[..100] : word;
            if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(term);
            }
            if (terms.Count == MaxTerms)
            {
                break;
            }
        }
        return terms.Count == 0 ? null : terms;
    }

    /// <summary>Reads a chat blob (the shape storage keeps) into what the search needs. Only plain text: messages whose role is user or assistant, with a string
    /// <c>content</c> and no <c>kind</c> (so reports and inbox notes from sessions are left out), never tool results, tool calls or attachments.</summary>
    public static SearchDoc BuildDoc(string id, string title, string updatedAt, JObject thread, int charBudget = MaxChatChars)
    {
        List<SearchMessage> messages = [];
        int chars = 0;
        if (thread?["messages"] is JArray array && charBudget > 0)
        {
            foreach (JToken token in array)
            {
                if (token is not JObject message)
                {
                    continue;
                }
                string role = message["role"]?.Type == JTokenType.String ? message["role"].ToString() : null;
                if (role != Roles.User && role != Roles.Assistant)
                {
                    continue;
                }
                if (message["kind"] is { Type: not JTokenType.Null } kind && !string.IsNullOrEmpty(kind.ToString()))
                {
                    continue;
                }
                if (message["content"]?.Type != JTokenType.String)
                {
                    continue;
                }
                string text = message["content"].ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }
                if (text.Length > MaxMessageChars)
                {
                    text = text[..MaxMessageChars];
                }
                int room = Math.Min(charBudget, MaxChatChars) - chars;
                if (room <= 0)
                {
                    break;
                }
                if (text.Length > room)
                {
                    text = text[..room];
                }
                messages.Add(new SearchMessage(message["id"]?.ToString() ?? "", role, text));
                chars += text.Length;
            }
        }
        return new SearchDoc { Id = id, Title = title ?? "", UpdatedAt = updatedAt ?? "", Messages = messages, Chars = chars, TitleOnly = charBudget <= 0 };
    }

    /// <summary>Searches <paramref name="docs"/>. Ranking, best first: a title that holds the whole query, then title words, then messages that hold the whole query,
    /// then how many messages hold a word; ties go to the more recently updated chat, then to the id, so the order never wavers.</summary>
    public static SearchPage Search(IEnumerable<SearchDoc> docs, string query, int limit = DefaultLimit, int offset = 0)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Clamp(offset, 0, MaxOffset);
        List<string> terms = ParseQuery(query);
        if (terms is null)
        {
            return new SearchPage { Offset = offset };
        }
        string phrase = string.Join(' ', terms);
        List<SearchHit> hits = [];
        foreach (SearchDoc doc in docs)
        {
            SearchHit hit = Match(doc, terms, phrase);
            if (hit is not null)
            {
                hits.Add(hit);
            }
        }
        hits.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }
            int byTime = string.CompareOrdinal(b.UpdatedAt, a.UpdatedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
        });
        List<SearchHit> page = hits.Skip(offset).Take(limit).ToList();
        return new SearchPage { Hits = page, Total = hits.Count, Offset = offset, HasMore = offset + page.Count < hits.Count };
    }

    private static SearchHit Match(SearchDoc doc, List<string> terms, string phrase)
    {
        string title = doc.Title ?? "";
        bool phraseInTitle = terms.Count > 0 && title.Contains(phrase, StringComparison.OrdinalIgnoreCase);
        bool[] inTitle = terms.Select(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        int titleTerms = inTitle.Count(found => found);
        // How many messages hold each word, and per message how many words it holds and whether it holds the whole query.
        int[] messageCounts = new int[terms.Count];
        int[] wordsIn = new int[doc.Messages.Count];
        bool[] phraseIn = new bool[doc.Messages.Count];
        for (int m = 0; m < doc.Messages.Count; m++)
        {
            string text = doc.Messages[m].Text;
            for (int t = 0; t < terms.Count; t++)
            {
                if (text.Contains(terms[t], StringComparison.OrdinalIgnoreCase))
                {
                    messageCounts[t]++;
                    wordsIn[m]++;
                }
            }
            phraseIn[m] = terms.Count > 1 ? wordsIn[m] == terms.Count && text.Contains(phrase, StringComparison.OrdinalIgnoreCase) : wordsIn[m] == 1;
        }
        for (int t = 0; t < terms.Count; t++)
        {
            if (!inTitle[t] && messageCounts[t] == 0)
            {
                return null;
            }
        }
        int matchedMessages = wordsIn.Count(w => w > 0);
        int phraseMessages = phraseIn.Count(p => p);
        int score = (phraseInTitle ? 10_000 : 0) + titleTerms * 1_000 + Math.Min(phraseMessages, 20) * 40 + Math.Min(matchedMessages, 50);
        List<int> order = Enumerable.Range(0, doc.Messages.Count).Where(m => wordsIn[m] > 0).ToList();
        // Best messages first: the whole query, then more of the words, then the earlier message (a stable sort).
        order = order.OrderByDescending(m => phraseIn[m]).ThenByDescending(m => wordsIn[m]).ThenBy(m => m).Take(MaxSnippetsPerChat).ToList();
        List<SearchSnippet> snippets = order.Select(m => Snippet(doc.Messages[m], terms, phrase)).ToList();
        return new SearchHit
        {
            Id = doc.Id,
            Title = title,
            UpdatedAt = doc.UpdatedAt,
            TitleMatch = titleTerms > 0,
            MatchedMessages = matchedMessages,
            Snippets = snippets,
            Score = score
        };
    }

    /// <summary>The text around the first match in a message, with every match in it marked. Whitespace is flattened to single spaces, an ellipsis marks a cut.</summary>
    public static SearchSnippet Snippet(SearchMessage message, List<string> terms, string phrase)
    {
        string text = message.Text;
        int first = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        if (first < 0)
        {
            first = int.MaxValue;
            foreach (string term in terms)
            {
                int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && at < first)
                {
                    first = at;
                }
            }
        }
        int start = Math.Max(0, first - SnippetLead);
        // Begin at a word, not in the middle of one, and never in the middle of a surrogate pair.
        if (start > 0)
        {
            int space = text.IndexOf(' ', start, Math.Min(12, first - start));
            if (space >= 0)
            {
                start = space + 1;
            }
            if (char.IsLowSurrogate(text[start]))
            {
                start++;
            }
        }
        int end = Math.Min(text.Length, start + SnippetChars);
        if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }
        string window = text[start..end];
        string flat = FlattenWhitespace(window);
        string shown = (start > 0 ? "…" : "") + flat + (end < text.Length ? "…" : "");
        int shift = start > 0 ? 1 : 0;
        List<(int Start, int Length)> ranges = [];
        foreach (string term in terms)
        {
            int from = 0;
            while (from < flat.Length)
            {
                int at = flat.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }
                ranges.Add((at + shift, term.Length));
                from = at + term.Length;
            }
        }
        return new SearchSnippet(message.Id, message.Role, shown, MergeRanges(ranges));
    }

    /// <summary>Whitespace runs become one space, trimmed. (Lengths of matches are unaffected: terms hold no whitespace.)</summary>
    private static string FlattenWhitespace(string text)
    {
        return string.Join(' ', text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<(int Start, int Length)> MergeRanges(List<(int Start, int Length)> ranges)
    {
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        List<(int Start, int Length)> merged = [];
        foreach ((int start, int length) in ranges)
        {
            if (merged.Count > 0 && start <= merged[^1].Start + merged[^1].Length)
            {
                (int lastStart, int lastLength) = merged[^1];
                merged[^1] = (lastStart, Math.Max(lastLength, start + length - lastStart));
            }
            else
            {
                merged.Add((start, length));
            }
        }
        return merged;
    }
}
