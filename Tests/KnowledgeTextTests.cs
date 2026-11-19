using Hartsy.Extensions.LLMAssistant.Services.Knowledge;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Reading a document as text and cutting it into pieces: words, unspaced scripts, HTML, binary files, and where a long document is split.</summary>
public class KnowledgeTextTests
{
    // ---- Words ----

    [Fact]
    public void Words_are_lowercased_runs_of_letters_and_digits()
    {
        Assert.Equal(["hello", "world", "42", "x2"], KnowledgeText.Tokens("Hello, WORLD! 42 -- x2"));
        Assert.Empty(KnowledgeText.Tokens(""));
        Assert.Empty(KnowledgeText.Tokens(null));
        Assert.Empty(KnowledgeText.Tokens("... --- !!!"));
    }

    [Fact]
    public void Accented_and_non_latin_letters_are_words_too()
    {
        // The server runs with invariant globalization, which must not change what counts as a letter.
        Assert.Equal(["café", "naïve", "straße", "привет", "мир"], KnowledgeText.Tokens("Café NAÏVE straße, Привет мир"));
        Assert.Equal(["ελληνικά"], KnowledgeText.Tokens("Ελληνικά"));
        Assert.Equal(["الكلمة"], KnowledgeText.Tokens("الكلمة"));
    }

    [Fact]
    public void An_unspaced_script_is_searched_by_character_and_by_pair()
    {
        Assert.Equal(["日", "本", "日本", "語", "本語"], KnowledgeText.Tokens("日本語"));
        // A space ends the run: the pairs do not reach across it.
        Assert.Equal(["日", "本", "日本", "語"], KnowledgeText.Tokens("日本 語"));
        // Spaced words around it are ordinary.
        Assert.Equal(["tokyo", "東", "京", "東京", "city"], KnowledgeText.Tokens("Tokyo 東京 city"));
    }

    // ---- Reading ----

    [Fact]
    public void Binary_looking_text_is_recognised()
    {
        Assert.True(KnowledgeText.LooksBinary("abc\0def"));
        Assert.True(KnowledgeText.LooksBinary(new string('�', 100) + "text"));
        Assert.False(KnowledgeText.LooksBinary("An ordinary document, with an é and a � in it once."));
        Assert.False(KnowledgeText.LooksBinary(""));
        Assert.False(KnowledgeText.LooksBinary(null));
    }

    [Fact]
    public void A_document_is_tidied_as_it_is_stored()
    {
        Assert.Equal("one\ntwo\n\nthree", KnowledgeText.Normalize("﻿one  \r\ntwo\r\n\r\n\r\n\r\n\r\nthree\r\n", "notes.txt"));
        Assert.Equal("", KnowledgeText.Normalize(null, "x.txt"));
        Assert.Equal("a\nb", KnowledgeText.Normalize("a\rb", null));
    }

    [Fact]
    public void A_web_page_keeps_its_visible_text_only()
    {
        string html = "<html><head><title>Ignore</title><style>p { color: red }</style></head><body><h1>Title &amp; more</h1><p>First   paragraph.</p><script>alert(1)</script><p>Second &lt;one&gt;.</p></body></html>";
        string text = KnowledgeText.Normalize(html, "page.HTML");
        Assert.Contains("Title & more", text);
        Assert.Contains("First   paragraph.", text);
        Assert.Contains("Second <one>.", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("Ignore", text);
        Assert.DoesNotContain("<", text.Replace("<one>", ""));
        // Only an HTML file is read as a page: the same text in a .txt stays as written.
        Assert.Contains("<script>", KnowledgeText.Normalize(html, "page.txt"));
    }

    // ---- Pieces ----

    [Fact]
    public void A_short_document_is_one_piece()
    {
        List<KnowledgeChunk> chunks = KnowledgeText.Chunk("Just a short note.");
        KnowledgeChunk only = Assert.Single(chunks);
        Assert.Equal("Just a short note.", only.Text);
        Assert.Equal(0, only.Number);
        Assert.Equal("", only.Heading);
        Assert.Empty(KnowledgeText.Chunk(""));
        Assert.Empty(KnowledgeText.Chunk("   \n\n  "));
    }

    [Fact]
    public void A_long_document_is_split_along_paragraphs_into_numbered_pieces_that_overlap()
    {
        string paragraph = string.Join(' ', Enumerable.Range(1, 60).Select(i => $"word{i}"));
        string text = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"P{i} {paragraph}"));
        List<KnowledgeChunk> chunks = KnowledgeText.Chunk(text, 700, 100);
        Assert.True(chunks.Count >= 4);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Number));
        // Pieces stay near the target, and every paragraph's marker is somewhere.
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 700 + 100 + 10, c.Text.Length.ToString()));
        string all = string.Join(' ', chunks.Select(c => c.Text));
        for (int i = 1; i <= 6; i++)
        {
            Assert.Contains($"P{i} ", all);
        }
        // The start of a piece repeats the end of the one before it.
        string tail = chunks[0].Text[^40..];
        string firstWords = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        Assert.Contains(firstWords, chunks[1].Text);
    }

    [Fact]
    public void A_long_sentence_with_no_stop_is_cut_at_spaces_and_a_word_with_none_is_cut_where_it_stands()
    {
        string text = string.Join(' ', Enumerable.Range(1, 400).Select(i => $"w{i}"));
        List<KnowledgeChunk> chunks = KnowledgeText.Chunk(text, 300, 0);
        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 300));
        // No word is cut in two.
        Assert.Equal(text.Split(' '), chunks.SelectMany(c => c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray());

        List<KnowledgeChunk> solid = KnowledgeText.Chunk(new string('x', 2500), 1000, 0);
        Assert.Equal(3, solid.Count);
        Assert.Equal(2500, solid.Sum(c => c.Text.Length));
    }

    [Fact]
    public void A_markdown_heading_is_kept_as_the_heading_of_what_follows_and_sections_are_not_mixed()
    {
        string text = "# Guide\n\nIntro text.\n\n## Install\n\nRun the installer.\n\n### Windows\n\nUse the exe.\n\n## Usage\n\nOpen the app.";
        List<KnowledgeChunk> chunks = KnowledgeText.Chunk(text);
        Assert.Equal(["Guide", "Guide > Install", "Guide > Install > Windows", "Guide > Usage"], chunks.Select(c => c.Heading));
        Assert.Equal(["Intro text.", "Run the installer.", "Use the exe.", "Open the app."], chunks.Select(c => c.Text));
    }

    [Fact]
    public void A_heading_with_nothing_under_it_is_not_a_piece_and_hashes_are_trimmed()
    {
        List<KnowledgeChunk> chunks = KnowledgeText.Chunk("## Empty ##\n\n## Next\n\nBody.");
        KnowledgeChunk only = Assert.Single(chunks);
        Assert.Equal("Next", only.Heading);
        Assert.Equal("Body.", only.Text);
    }

    [Fact]
    public void Chunking_is_deterministic()
    {
        string text = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i}. " + new string('a', 200)));
        Assert.Equal(KnowledgeText.Chunk(text).Select(c => c.Text), KnowledgeText.Chunk(text).Select(c => c.Text));
    }
}
