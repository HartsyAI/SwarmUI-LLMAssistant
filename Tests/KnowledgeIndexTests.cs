using Hartsy.Extensions.LLMAssistant.Services.Knowledge;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Ranking pieces for a query: keyword search, vector search, their combination, and the rules that keep one document from filling the answer.</summary>
public class KnowledgeIndexTests
{
    /// <summary>A piece.</summary>
    private static IndexedChunk Piece(string doc, int number, string text, string title = null, float[] vector = null, string key = null)
    {
        IndexedChunk chunk = IndexedChunk.Make(doc, title ?? doc, number, text, "");
        if (vector is not null)
        {
            chunk.Vector = KnowledgeVectors.Normalize(vector);
            chunk.VectorKey = key;
        }
        return chunk;
    }

    /// <summary>A small library.</summary>
    private static List<IndexedChunk> Library() =>
    [
        Piece("cats", 0, "Cats purr when they are content and sleep most of the day."),
        Piece("dogs", 0, "Dogs bark at strangers and love to fetch a ball in the park."),
        Piece("cars", 0, "The engine of the car needs oil, and the tyres need air."),
        Piece("cars", 1, "A car engine burns fuel; electric cars have a battery instead."),
    ];

    // ---- Keyword search ----

    [Fact]
    public void A_word_finds_the_pieces_that_contain_it_best_first()
    {
        List<KnowledgeHit> hits = KnowledgeIndex.Search(Library(), "engine oil", null, null, 5);
        Assert.Equal("cars", hits[0].Chunk.DocId);
        Assert.Equal(0, hits[0].Chunk.Number);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.Keyword && !h.Semantic));
    }

    [Fact]
    public void A_rarer_word_counts_for_more_than_a_common_one()
    {
        List<IndexedChunk> chunks =
        [
            Piece("a", 0, "the the the the quokka"),
            Piece("b", 0, "the the the the the the the the the the"),
            Piece("c", 0, "the plain words only here"),
        ];
        Assert.Equal("a", KnowledgeIndex.Search(chunks, "the quokka", null, null, 3)[0].Chunk.DocId);
    }

    [Fact]
    public void A_match_in_the_title_or_heading_counts()
    {
        IndexedChunk titled = IndexedChunk.Make("d1", "Warranty terms", 0, "Contact us within thirty days.", "Returns");
        IndexedChunk other = Piece("d2", 0, "Contact us within thirty days of the order.");
        List<KnowledgeHit> hits = KnowledgeIndex.Search([other, titled], "warranty returns", null, null, 2);
        Assert.Single(hits);
        Assert.Equal("d1", hits[0].Chunk.DocId);
    }

    [Fact]
    public void Nothing_matches_when_no_word_does_and_an_empty_query_or_library_gives_nothing()
    {
        Assert.Empty(KnowledgeIndex.Search(Library(), "zeppelin", null, null, 5));
        Assert.Empty(KnowledgeIndex.Search(Library(), "", null, null, 5));
        Assert.Empty(KnowledgeIndex.Search(Library(), "   ", null, null, 5));
        Assert.Empty(KnowledgeIndex.Search([], "cats", null, null, 5));
        Assert.Empty(KnowledgeIndex.Search(Library(), "cats", null, null, 0));
    }

    [Fact]
    public void Case_and_accents_in_the_query_are_read_like_the_documents()
    {
        List<IndexedChunk> chunks = [Piece("menu", 0, "Crème brûlée and café au lait are served.")];
        Assert.Single(KnowledgeIndex.Search(chunks, "CAFÉ", null, null, 3));
        Assert.Single(KnowledgeIndex.Search([Piece("jp", 0, "東京は日本の首都です")], "東京", null, null, 3));
    }

    [Fact]
    public void The_limit_is_respected_and_one_document_gives_at_most_three_pieces()
    {
        List<IndexedChunk> chunks = [.. Enumerable.Range(0, 10).Select(i => Piece("big", i, $"shared term number {i}")), Piece("small", 0, "shared term elsewhere")];
        List<KnowledgeHit> hits = KnowledgeIndex.Search(chunks, "shared term", null, null, 10);
        Assert.Equal(KnowledgeIndex.MaxPerDocument + 1, hits.Count);
        Assert.Equal(KnowledgeIndex.MaxPerDocument, hits.Count(h => h.Chunk.DocId == "big"));
        Assert.Single(KnowledgeIndex.Search(chunks, "shared term", null, null, 1));
    }

    [Fact]
    public void Ranking_is_the_same_every_time_even_for_ties()
    {
        List<IndexedChunk> chunks = [Piece("b", 0, "same words"), Piece("a", 0, "same words"), Piece("c", 0, "same words")];
        string first = string.Join(",", KnowledgeIndex.Search(chunks, "same words", null, null, 3).Select(h => h.Chunk.DocId));
        Assert.Equal("a,b,c", first);
        Assert.Equal(first, string.Join(",", KnowledgeIndex.Search([.. chunks.AsEnumerable().Reverse()], "same words", null, null, 3).Select(h => h.Chunk.DocId)));
    }

    // ---- Vectors ----

    [Fact]
    public void A_vector_is_made_unit_length_and_a_bad_one_is_refused()
    {
        float[] unit = KnowledgeVectors.Normalize([3, 4]);
        Assert.Equal(0.6f, unit[0], 4);
        Assert.Equal(0.8f, unit[1], 4);
        Assert.Null(KnowledgeVectors.Normalize([]));
        Assert.Null(KnowledgeVectors.Normalize(null));
        Assert.Null(KnowledgeVectors.Normalize([0, 0, 0]));
        Assert.Null(KnowledgeVectors.Normalize([1, float.NaN]));
        Assert.Null(KnowledgeVectors.Normalize([1, float.PositiveInfinity]));
    }

    [Fact]
    public void A_vector_is_stored_compactly_and_comes_back_exactly()
    {
        float[] vector = [0.25f, -1.5f, 3.14159f, 0f];
        string packed = KnowledgeVectors.Pack(vector);
        Assert.Equal(vector, KnowledgeVectors.Unpack(packed));
        Assert.Equal(24, packed.Length);
        Assert.Null(KnowledgeVectors.Unpack(null));
        Assert.Null(KnowledgeVectors.Unpack(""));
        Assert.Null(KnowledgeVectors.Unpack("not base64!"));
        Assert.Null(KnowledgeVectors.Unpack(Convert.ToBase64String([1, 2, 3])));
    }

    [Fact]
    public void A_vector_finds_a_piece_the_words_do_not()
    {
        const string key = "p|model|2";
        List<IndexedChunk> chunks =
        [
            Piece("felines", 0, "Whiskered hunters that curl up in sunny windows.", vector: [1, 0.1f], key: key),
            Piece("canines", 0, "Loyal companions that chase thrown balls.", vector: [0.1f, 1], key: key),
        ];
        // No word of the query is in either piece, but the query's vector points at the first.
        Assert.Empty(KnowledgeIndex.Search(chunks, "kitten", null, null, 3));
        List<KnowledgeHit> hits = KnowledgeIndex.Search(chunks, "kitten", KnowledgeVectors.Normalize([1, 0]), key, 3);
        Assert.Equal("felines", hits[0].Chunk.DocId);
        Assert.True(hits[0].Semantic && !hits[0].Keyword);
    }

    [Fact]
    public void Words_and_vectors_agreeing_rank_a_piece_above_one_found_only_one_way()
    {
        const string key = "p|model|2";
        List<IndexedChunk> chunks =
        [
            Piece("both", 0, "battery charging guide", vector: [1, 0], key: key),
            Piece("words", 0, "battery battery battery", vector: [0, 1], key: key),
            Piece("meaning", 0, "power cell upkeep", vector: [0.9f, 0.1f], key: key),
        ];
        List<KnowledgeHit> hits = KnowledgeIndex.Search(chunks, "battery", KnowledgeVectors.Normalize([1, 0]), key, 3);
        Assert.Equal("both", hits[0].Chunk.DocId);
        Assert.True(hits[0].Keyword && hits[0].Semantic);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Vectors_made_by_another_model_or_of_another_size_are_never_compared()
    {
        List<IndexedChunk> chunks =
        [
            Piece("other-model", 0, "alpha", vector: [1, 0], key: "p|old|2"),
            Piece("other-size", 0, "beta", vector: [1, 0, 0], key: "p|model|3"),
            Piece("none", 0, "gamma"),
        ];
        Assert.Empty(KnowledgeIndex.Search(chunks, "zzz", KnowledgeVectors.Normalize([1, 0]), "p|model|2", 3));
        // The words still work for all of them.
        Assert.Single(KnowledgeIndex.Search(chunks, "gamma", KnowledgeVectors.Normalize([1, 0]), "p|model|2", 3));
    }

    [Fact]
    public void The_key_names_the_provider_the_model_and_the_size()
    {
        Assert.Equal("openai:1|text-embedding-3-small|1536", KnowledgeVectors.Key("openai:1", "text-embedding-3-small", 1536));
    }
}
