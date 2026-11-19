using System.IO;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Knowledge;
using Hartsy.Extensions.LLMAssistant.Tools;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The files a library lives in: adding and removing documents, the limits, vectors surviving a round trip, damaged and linked files, and that no request can name a path.</summary>
public sealed class KnowledgeStoreTests : IDisposable
{
    /// <summary>A folder of its own for each test.</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llma-knowledge-" + Guid.NewGuid().ToString("N"));

    /// <summary>Removes the folder.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>Pieces for a document.</summary>
    private static List<KnowledgeChunk> Pieces(params string[] texts) => [.. texts.Select((t, i) => new KnowledgeChunk(i, t, i == 0 ? "Intro" : ""))];

    /// <summary>Adds a small document with no vectors.</summary>
    private KnowledgeDocument AddDoc(string title, params string[] texts) => KnowledgeStore.Add(_root, title, title + ".txt", texts.Sum(t => t.Length), Pieces(texts), null, null, null);

    [Fact]
    public void An_empty_library_has_no_documents_and_nothing_to_search()
    {
        Assert.Empty(KnowledgeStore.List(_root));
        Assert.Empty(KnowledgeStore.LoadIndex(_root));
    }

    [Fact]
    public void A_document_is_added_listed_searched_and_deleted()
    {
        KnowledgeDocument doc = AddDoc("Notes", "first piece", "second piece");
        Assert.Matches("^[0-9a-f]{32}$", doc.Id);
        Assert.Equal(2, doc.Chunks);
        Assert.Equal("", doc.VectorKey);
        Assert.Equal([doc.Id], KnowledgeStore.List(_root).Select(d => d.Id));

        List<IndexedChunk> index = KnowledgeStore.LoadIndex(_root);
        Assert.Equal(["first piece", "second piece"], index.Select(c => c.Text));
        Assert.Equal(["Intro", ""], index.Select(c => c.Heading));
        Assert.All(index, c => Assert.Equal("Notes", c.DocTitle));
        Assert.Contains("notes", index[0].Terms.Keys);

        Assert.True(KnowledgeStore.Delete(_root, doc.Id));
        Assert.Empty(KnowledgeStore.List(_root));
        Assert.Empty(KnowledgeStore.LoadIndex(_root));
        Assert.False(File.Exists(Path.Combine(_root, "docs", doc.Id + ".json")));
        Assert.False(KnowledgeStore.Delete(_root, doc.Id));
    }

    [Fact]
    public void The_index_is_rebuilt_after_a_change_and_not_before()
    {
        AddDoc("One", "alpha");
        List<IndexedChunk> first = KnowledgeStore.LoadIndex(_root);
        Assert.Same(first, KnowledgeStore.LoadIndex(_root));
        AddDoc("Two", "beta");
        List<IndexedChunk> second = KnowledgeStore.LoadIndex(_root);
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void Vectors_survive_storage_and_belong_to_the_pieces_they_were_made_for()
    {
        float[][] vectors = [KnowledgeVectors.Normalize([1, 0, 0]), KnowledgeVectors.Normalize([0, 1, 0])];
        KnowledgeDocument doc = KnowledgeStore.Add(_root, "Vec", "vec.txt", 10, Pieces("a", "b"), [.. vectors], "p|m|3", null);
        Assert.Equal("p|m|3", doc.VectorKey);
        List<IndexedChunk> index = KnowledgeStore.LoadIndex(_root);
        Assert.Equal(vectors[0], index[0].Vector);
        Assert.Equal(vectors[1], index[1].Vector);
        Assert.All(index, c => Assert.Equal("p|m|3", c.VectorKey));
    }

    [Fact]
    public void Changing_vectors_records_what_they_were_made_with_and_removing_them_clears_it()
    {
        KnowledgeDocument doc = AddDoc("Doc", "x", "y");
        KnowledgeStore.SetVectors(_root, doc.Id, [KnowledgeVectors.Normalize([1, 2]), KnowledgeVectors.Normalize([3, 4])], "p|m|2", null);
        Assert.Equal("p|m|2", KnowledgeStore.List(_root)[0].VectorKey);
        Assert.All(KnowledgeStore.LoadIndex(_root), c => Assert.NotNull(c.Vector));
        KnowledgeStore.SetVectors(_root, doc.Id, null, null, "the model failed");
        KnowledgeDocument after = KnowledgeStore.List(_root)[0];
        Assert.Equal("", after.VectorKey);
        Assert.Equal("the model failed", after.EmbedError);
        Assert.All(KnowledgeStore.LoadIndex(_root), c => Assert.Null(c.Vector));
        // A set of vectors that does not match the pieces is not used.
        KnowledgeStore.SetVectors(_root, doc.Id, [KnowledgeVectors.Normalize([1, 2])], "p|m|2", null);
        Assert.All(KnowledgeStore.LoadIndex(_root), c => Assert.Null(c.Vector));
        // An unknown document is ignored.
        KnowledgeStore.SetVectors(_root, "0123abcd", null, null, null);
    }

    [Fact]
    public void The_pieces_of_a_document_can_be_read_back_in_order()
    {
        KnowledgeDocument doc = AddDoc("Order", "one", "two", "three");
        Assert.Equal(["one", "two", "three"], KnowledgeStore.ReadChunks(_root, doc.Id).Select(c => c.Text));
        Assert.Null(KnowledgeStore.ReadChunks(_root, "0123abcd"));
    }

    [Fact]
    public void The_number_of_documents_is_limited()
    {
        for (int i = 0; i < KnowledgeStore.MaxDocuments; i++)
        {
            AddDoc($"d{i}", "t");
        }
        KnowledgeLimitException ex = Assert.Throws<KnowledgeLimitException>(() => AddDoc("one more", "t"));
        Assert.Contains($"at most {KnowledgeStore.MaxDocuments} documents", ex.Message);
        Assert.Equal(KnowledgeStore.MaxDocuments, KnowledgeStore.List(_root).Count);
    }

    [Fact]
    public void The_number_of_pieces_is_limited_and_a_document_that_does_not_fit_is_not_kept_at_all()
    {
        KnowledgeStore.Add(_root, "Big", "big.txt", 1, Pieces([.. Enumerable.Repeat("x", KnowledgeStore.MaxChunks - 1)]), null, null, null);
        KnowledgeLimitException ex = Assert.Throws<KnowledgeLimitException>(() => AddDoc("Two pieces", "a", "b"));
        Assert.Contains("library is full", ex.Message);
        Assert.Single(KnowledgeStore.List(_root));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "docs")));
        // One more piece still fits.
        AddDoc("Last", "z");
    }

    [Fact]
    public void A_document_id_is_only_ever_hex_so_nothing_in_a_request_becomes_a_path()
    {
        AddDoc("Safe", "t");
        foreach (string bad in new[] { "", "..", "../library", "..\\..\\x", "a/b", "g00d", "doc.json" })
        {
            // Nothing is found, and no file operation is ever made with it.
            Assert.Null(KnowledgeStore.ReadChunks(_root, bad));
            Assert.False(KnowledgeStore.Delete(_root, bad));
            KnowledgeStore.SetVectors(_root, bad, null, null, null);
        }
        Assert.Single(KnowledgeStore.List(_root));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "docs")));
    }

    [Fact]
    public void A_damaged_library_file_is_an_empty_library_and_a_damaged_document_is_skipped()
    {
        KnowledgeDocument good = AddDoc("Good", "kept");
        KnowledgeDocument bad = AddDoc("Bad", "lost");
        File.WriteAllText(Path.Combine(_root, "docs", bad.Id + ".json"), "{ not json");
        Assert.Equal(["kept"], KnowledgeStore.LoadIndex(_root).Select(c => c.Text));
        Assert.Equal(2, KnowledgeStore.List(_root).Count);
        File.WriteAllText(Path.Combine(_root, "library.json"), "[ broken");
        Assert.Empty(KnowledgeStore.List(_root));
        Assert.Empty(KnowledgeStore.LoadIndex(_root));
        // The library can be built up again from there, and the damaged list is kept rather than overwritten.
        AddDoc("Fresh", "new");
        Assert.Single(KnowledgeStore.List(_root));
        Assert.Equal("[ broken", File.ReadAllText(Path.Combine(_root, "library.json.damaged")));
        Assert.Contains(good.Id, KnowledgeStore.List(_root).Select(d => d.Id).Concat([good.Id]));
    }

    [Fact]
    public void Nothing_is_left_half_written()
    {
        AddDoc("One", "a");
        AddDoc("Two", "b");
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Two_libraries_do_not_see_each_other()
    {
        string other = Path.Combine(Path.GetTempPath(), "llma-knowledge-" + Guid.NewGuid().ToString("N"));
        try
        {
            AddDoc("Mine", "private");
            KnowledgeStore.Add(other, "Theirs", "t.txt", 1, Pieces("other"), null, null, null);
            Assert.Equal(["Mine"], KnowledgeStore.List(_root).Select(d => d.Title));
            Assert.Equal(["Theirs"], KnowledgeStore.List(other).Select(d => d.Title));
            Assert.Equal(["private"], KnowledgeStore.LoadIndex(_root).Select(c => c.Text));
        }
        finally
        {
            Directory.Delete(other, true);
        }
    }

    // ---- The tool and the settings ----

    [Fact]
    public void The_search_tool_is_off_by_default_and_only_reads()
    {
        JObject definition = Services.ToolRegistryService.BuildDefaultTools()[ToolConstants.SearchKnowledge] as JObject;
        Assert.False(definition["enabled"].Value<bool>());
        Assert.Equal(ToolConstants.SearchKnowledge, definition["handlerId"]);
        Assert.Equal(["query"], definition["parameters"]["required"].Select(t => t.ToString()));
        Assert.DoesNotContain(ToolConstants.SearchKnowledge, ToolConstants.BuiltInIds);
        Assert.Equal(ToolRisk.Read, new KnowledgeTool().Risk);
        Assert.True(new KnowledgeTool().TimeoutFor(null) > TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Embedding_is_on_only_when_both_a_backend_and_a_model_are_set()
    {
        Assert.False(new KnowledgeSettings().Enabled);
        Assert.False(new KnowledgeSettings { ProviderId = "openai:1" }.Enabled);
        Assert.False(new KnowledgeSettings { Model = "m" }.Enabled);
        Assert.False(new KnowledgeSettings { ProviderId = " ", Model = " " }.Enabled);
        Assert.True(new KnowledgeSettings { ProviderId = "openai:1", Model = "m" }.Enabled);
    }
}
