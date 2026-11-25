using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Hartsy.Extensions.LLMAssistant.Services.Knowledge;

/// <summary>One piece of a document as it is stored and searched: its text, the heading it sits under (for Markdown), and its place in the document.</summary>
/// <param name="Number">Which piece of the document this is, from 0.</param>
/// <param name="Text">The text, as the model will read it.</param>
/// <param name="Heading">The nearest heading above it ("A > B" for nested ones), or empty.</param>
/// <param name="Page">The page of the file it is from (a PDF), from 1, or 0.</param>
public sealed record KnowledgeChunk(int Number, string Text, string Heading, int Page = 0);

/// <summary>Turning a document into text that can be searched: reading it as text, splitting it into pieces, and the words a search compares. Everything here is a pure function of
/// its input, with no clock, no storage and no operating-system text services, so it behaves the same on a server that runs with invariant globalization and is tested with fixed
/// strings.</summary>
public static class KnowledgeText
{
    /// <summary>The most characters in one piece (the aim; a long unbroken line is cut at a space near it).</summary>
    public const int TargetChunkChars = 900;

    /// <summary>How many characters of the end of one piece are repeated at the start of the next, so a sentence that straddles the cut is whole in one of them.</summary>
    public const int OverlapChars = 120;

    /// <summary>How long one pattern may take on one document before it is given up on. A document is the user's own, but a page with thousands of unclosed tags can make a lazy
    /// pattern slow, and a request must not tie a thread up for it.</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(2);

    /// <summary>A Markdown heading line.</summary>
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled, PatternTimeout);

    /// <summary>The end of a sentence: punctuation and the space after it.</summary>
    private static readonly Regex SentenceEnd = new(@"(?<=[.!?。！？])\s+", RegexOptions.Compiled, PatternTimeout);

    /// <summary>Script and style blocks, whose contents are not the page's text.</summary>
    private static readonly Regex HtmlBlocks = new(@"<(script|style|head|noscript)\b[\s\S]*?</\1\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase, PatternTimeout);

    /// <summary>A tag.</summary>
    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled, PatternTimeout);

    /// <summary>Whether a character is one of the scripts written without spaces between words (Chinese, Japanese kana, Korean), which are searched by character and pair.</summary>
    private static bool IsUnspaced(char c) => c is (>= '぀' and <= 'ヿ') or (>= '㐀' and <= '鿿') or (>= '가' and <= '힯') or (>= '豈' and <= '﫿');

    /// <summary>The words a search compares: runs of letters and digits, lowercased. A run of an unspaced script gives each character and each pair of neighbours, since there is no
    /// space to split it on. Uses only the character categories built into the runtime, not culture data, so it works under invariant globalization.</summary>
    public static List<string> Tokens(string text)
    {
        List<string> tokens = [];
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }
        StringBuilder word = new();
        char previousUnspaced = '\0';
        void Flush()
        {
            if (word.Length > 0)
            {
                tokens.Add(word.ToString());
                word.Clear();
            }
        }
        foreach (char raw in text)
        {
            if (!char.IsLetterOrDigit(raw))
            {
                Flush();
                previousUnspaced = '\0';
                continue;
            }
            char c = char.ToLowerInvariant(raw);
            if (IsUnspaced(c))
            {
                Flush();
                tokens.Add(c.ToString());
                if (previousUnspaced != '\0')
                {
                    tokens.Add(string.Concat(previousUnspaced, c));
                }
                previousUnspaced = c;
                continue;
            }
            previousUnspaced = '\0';
            word.Append(c);
        }
        Flush();
        return tokens;
    }

    /// <summary>Whether text that was read as UTF-8 is really something else: it has a NUL, or a share of replacement characters (what bytes that are not text become).</summary>
    public static bool LooksBinary(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        int sample = Math.Min(text.Length, 8000);
        int bad = 0;
        for (int i = 0; i < sample; i++)
        {
            if (text[i] == '\0')
            {
                return true;
            }
            if (text[i] == '�')
            {
                bad++;
            }
        }
        return bad * 50 > sample;
    }

    /// <summary>The text of a document as it is stored: no byte-order mark, line breaks as <c>\n</c>, trailing spaces gone, runs of blank lines cut to one, and for a page of HTML the
    /// visible text only (without its scripts, styles and tags, entities decoded).</summary>
    public static string Normalize(string raw, string fileName)
    {
        string text = (raw ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        string extension = (fileName ?? "").Contains('.') ? fileName[fileName.LastIndexOf('.')..].ToLowerInvariant() : "";
        if (extension is ".html" or ".htm")
        {
            text = HtmlBlocks.Replace(text, " ");
            // Block-level tags end a line; the rest are removed in place.
            text = Regex.Replace(text, @"</?(p|div|br|li|ul|ol|h[1-6]|tr|table|section|article|header|footer|blockquote|pre)\b[^>]*>", "\n", RegexOptions.IgnoreCase, PatternTimeout);
            text = WebUtility.HtmlDecode(HtmlTag.Replace(text, ""));
        }
        text = Regex.Replace(text, @"[ \t]+\n", "\n", RegexOptions.None, PatternTimeout);
        text = Regex.Replace(text, @"\n{3,}", "\n\n", RegexOptions.None, PatternTimeout);
        return text.Trim();
    }

    /// <summary>Splits a document into pieces of about <paramref name="target"/> characters, along its own seams: blank lines first, then sentences, then spaces. Each piece after the
    /// first starts with the end of the one before it (<paramref name="overlap"/> characters, from a word boundary). A Markdown heading is kept as the <see cref="KnowledgeChunk.Heading"/>
    /// of what follows it, not as a piece of its own.</summary>
    public static List<KnowledgeChunk> Chunk(string text, int target = TargetChunkChars, int overlap = OverlapChars)
    {
        List<KnowledgeChunk> chunks = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return chunks;
        }
        List<(int Level, string Text)> headings = [];
        StringBuilder current = new();
        string currentHeading = "";
        void Emit()
        {
            string piece = current.ToString().Trim();
            if (piece.Length > 0)
            {
                chunks.Add(new KnowledgeChunk(chunks.Count, piece, currentHeading));
            }
            // What carries over to the next piece: the tail of this one.
            current.Clear();
            if (overlap > 0 && piece.Length > overlap)
            {
                string tail = piece[^overlap..];
                int space = tail.IndexOf(' ');
                tail = space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
                current.Append(tail).Append(' ');
            }
        }
        foreach (string block in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = block.Trim();
            Match heading = HeadingLine.Match(trimmed.Split('\n')[0]);
            if (heading.Success)
            {
                int level = heading.Groups[1].Length;
                // A heading closes every open one at its own level or deeper, so "##" after "##" is a sibling, not a child.
                while (headings.Count > 0 && headings[^1].Level >= level)
                {
                    headings.RemoveAt(headings.Count - 1);
                }
                headings.Add((level, heading.Groups[2].Value.Trim()));
                // A new section starts a new piece, so a piece never mixes the end of one section with the start of the next.
                if (current.ToString().Trim().Length > 0)
                {
                    Emit();
                    current.Clear();
                }
                currentHeading = string.Join(" > ", headings.Select(h => h.Text));
                trimmed = string.Join('\n', trimmed.Split('\n').Skip(1)).Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
            }
            foreach (string part in SplitLong(trimmed, target))
            {
                if (current.Length > 0 && current.Length + part.Length + 2 > target)
                {
                    Emit();
                }
                if (current.Length > 0 && !char.IsWhiteSpace(current[^1]))
                {
                    current.Append("\n\n");
                }
                current.Append(part);
            }
        }
        Emit();
        return chunks;
    }

    /// <summary>A block that fits in a piece, or the pieces of one that does not: by sentence, and a sentence longer than a piece by space.</summary>
    private static IEnumerable<string> SplitLong(string block, int target)
    {
        if (block.Length <= target)
        {
            yield return block;
            yield break;
        }
        StringBuilder run = new();
        foreach (string sentence in SentenceEnd.Split(block))
        {
            if (sentence.Length > target)
            {
                if (run.Length > 0)
                {
                    yield return run.ToString().Trim();
                    run.Clear();
                }
                foreach (string cut in CutAtSpaces(sentence, target))
                {
                    yield return cut;
                }
                continue;
            }
            if (run.Length > 0 && run.Length + sentence.Length + 1 > target)
            {
                yield return run.ToString().Trim();
                run.Clear();
            }
            run.Append(sentence).Append(' ');
        }
        if (run.Length > 0)
        {
            yield return run.ToString().Trim();
        }
    }

    /// <summary>Cuts a long run of text at the last space before each <paramref name="target"/> characters (or exactly there when there is none).</summary>
    private static IEnumerable<string> CutAtSpaces(string text, int target)
    {
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + target, text.Length);
            if (end < text.Length)
            {
                int space = text.LastIndexOf(' ', end - 1, end - start);
                if (space > start + target / 2)
                {
                    end = space;
                }
            }
            string piece = text[start..end].Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }
            start = end;
        }
    }
}
