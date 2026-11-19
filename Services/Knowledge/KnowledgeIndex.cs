namespace Hartsy.Extensions.LLMAssistant.Services.Knowledge;

/// <summary>One searchable piece: where it came from, its text, the words counted for keyword search, and (when the document was embedded) its vector.</summary>
public sealed class IndexedChunk
{
    /// <summary>The document it belongs to.</summary>
    public string DocId;
    /// <summary>The document's title.</summary>
    public string DocTitle;
    /// <summary>Which piece of the document this is, from 0.</summary>
    public int Number;
    /// <summary>The text the model reads.</summary>
    public string Text;
    /// <summary>The heading it sits under, or empty.</summary>
    public string Heading;
    /// <summary>How many times each word occurs in the piece, its heading and its document's title.</summary>
    public Dictionary<string, int> Terms = [];
    /// <summary>The number of words counted (the sum of <see cref="Terms"/>).</summary>
    public int Length;
    /// <summary>The embedding, unit length, or null when the piece has none.</summary>
    public float[] Vector;
    /// <summary>What the vector was made with (<see cref="KnowledgeVectors.Key"/>); vectors with different keys are never compared.</summary>
    public string VectorKey;

    /// <summary>Counts the words of a piece for keyword search: its text, its heading and its document's title (a match in the title or heading says something about the piece).</summary>
    public static IndexedChunk Make(string docId, string docTitle, int number, string text, string heading, Func<string, string> intern = null)
    {
        IndexedChunk chunk = new() { DocId = docId, DocTitle = docTitle, Number = number, Text = text, Heading = heading ?? "" };
        foreach (string word in KnowledgeText.Tokens($"{docTitle} {heading} {text}"))
        {
            // The same word is in thousands of pieces: with a pool, one string stands for all of them, which is most of what a library's index would otherwise hold in memory.
            string token = intern is null ? word : intern(word);
            chunk.Terms[token] = chunk.Terms.GetValueOrDefault(token) + 1;
            chunk.Length++;
        }
        return chunk;
    }
}

/// <summary>A search hit: the piece, its place in the ranking, and how it was found.</summary>
/// <param name="Chunk">The piece.</param>
/// <param name="Score">The combined score (higher is better); only meaningful for ordering.</param>
/// <param name="Keyword">Whether the keyword search found it.</param>
/// <param name="Semantic">Whether the vector search found it.</param>
public sealed record KnowledgeHit(IndexedChunk Chunk, double Score, bool Keyword, bool Semantic);

/// <summary>Searching a set of pieces: keyword ranking (BM25) always, vector similarity too when the query and the pieces were embedded by the same model, and the two rankings
/// combined by reciprocal rank fusion, which needs no tuning of scales. Keyword search alone is the baseline: it needs no model, and it is what runs when embedding is switched off,
/// failed, or was made by another model than the query. Pure, so it is tested with fixed pieces.</summary>
public static class KnowledgeIndex
{
    /// <summary>BM25's term-frequency saturation.</summary>
    private const double K1 = 1.2;

    /// <summary>BM25's length normalisation.</summary>
    private const double B = 0.75;

    /// <summary>The constant in reciprocal rank fusion: a larger one flattens the difference between ranks.</summary>
    private const double RrfK = 60;

    /// <summary>How many pieces of one document a search may return, so one long document does not fill every place.</summary>
    public const int MaxPerDocument = 3;

    /// <summary>The best <paramref name="limit"/> pieces for a query, best first. <paramref name="queryVector"/> (unit length) and <paramref name="vectorKey"/> are those of the embedded
    /// query, or null when there is none; only pieces with a vector of the same key take part in the vector ranking.</summary>
    public static List<KnowledgeHit> Search(IReadOnlyList<IndexedChunk> chunks, string query, float[] queryVector, string vectorKey, int limit)
    {
        if (chunks.Count == 0 || limit < 1)
        {
            return [];
        }
        List<string> queryTerms = [.. KnowledgeText.Tokens(query).Distinct()];
        Dictionary<IndexedChunk, double> keyword = RankKeyword(chunks, queryTerms);
        Dictionary<IndexedChunk, double> semantic = queryVector is null || vectorKey is null ? [] : RankSemantic(chunks, queryVector, vectorKey);
        Dictionary<IndexedChunk, double> fused = [];
        AddRanks(fused, keyword);
        AddRanks(fused, semantic);
        List<KnowledgeHit> hits = [];
        Dictionary<string, int> perDocument = [];
        foreach (KeyValuePair<IndexedChunk, double> entry in fused.OrderByDescending(e => e.Value).ThenBy(e => e.Key.DocId, StringComparer.Ordinal).ThenBy(e => e.Key.Number))
        {
            int taken = perDocument.GetValueOrDefault(entry.Key.DocId);
            if (taken >= MaxPerDocument)
            {
                continue;
            }
            perDocument[entry.Key.DocId] = taken + 1;
            hits.Add(new KnowledgeHit(entry.Key, entry.Value, keyword.ContainsKey(entry.Key), semantic.ContainsKey(entry.Key)));
            if (hits.Count == limit)
            {
                break;
            }
        }
        return hits;
    }

    /// <summary>BM25 scores for the pieces that contain at least one query word.</summary>
    private static Dictionary<IndexedChunk, double> RankKeyword(IReadOnlyList<IndexedChunk> chunks, List<string> queryTerms)
    {
        Dictionary<IndexedChunk, double> scores = [];
        if (queryTerms.Count == 0)
        {
            return scores;
        }
        double averageLength = Math.Max(1, chunks.Average(c => (double)c.Length));
        foreach (string term in queryTerms)
        {
            int containing = chunks.Count(c => c.Terms.ContainsKey(term));
            if (containing == 0)
            {
                continue;
            }
            double idf = Math.Log(1 + (chunks.Count - containing + 0.5) / (containing + 0.5));
            foreach (IndexedChunk chunk in chunks)
            {
                if (!chunk.Terms.TryGetValue(term, out int frequency))
                {
                    continue;
                }
                double normal = frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * chunk.Length / averageLength));
                scores[chunk] = scores.GetValueOrDefault(chunk) + idf * normal;
            }
        }
        return scores;
    }

    /// <summary>Cosine similarity (a dot product, as the vectors are unit length) for the pieces embedded with the same key as the query. A piece with no similarity above zero is not a match.</summary>
    private static Dictionary<IndexedChunk, double> RankSemantic(IReadOnlyList<IndexedChunk> chunks, float[] queryVector, string vectorKey)
    {
        Dictionary<IndexedChunk, double> scores = [];
        foreach (IndexedChunk chunk in chunks)
        {
            if (chunk.Vector is null || chunk.VectorKey != vectorKey || chunk.Vector.Length != queryVector.Length)
            {
                continue;
            }
            double similarity = KnowledgeVectors.Dot(chunk.Vector, queryVector);
            if (similarity > 0)
            {
                scores[chunk] = similarity;
            }
        }
        return scores;
    }

    /// <summary>Adds each piece's reciprocal-rank share (from its place in <paramref name="scores"/>, best first) to the fused total.</summary>
    private static void AddRanks(Dictionary<IndexedChunk, double> fused, Dictionary<IndexedChunk, double> scores)
    {
        int rank = 0;
        foreach (KeyValuePair<IndexedChunk, double> entry in scores.OrderByDescending(e => e.Value).ThenBy(e => e.Key.DocId, StringComparer.Ordinal).ThenBy(e => e.Key.Number))
        {
            rank++;
            fused[entry.Key] = fused.GetValueOrDefault(entry.Key) + 1.0 / (RrfK + rank);
        }
    }
}

/// <summary>The arithmetic of embeddings: unit length, the compact stored form, and the dot product.</summary>
public static class KnowledgeVectors
{
    /// <summary>What a vector was made with: the backend, the model and the number of numbers, so vectors from different models (which mean different things) are never compared.</summary>
    public static string Key(string providerId, string model, int dimension) => $"{providerId}|{model}|{dimension}";

    /// <summary>A copy of the vector scaled to length one, or null when it is empty, has no length or is not finite (a model that returned NaN).</summary>
    public static float[] Normalize(float[] vector)
    {
        if (vector is null || vector.Length == 0)
        {
            return null;
        }
        double sum = 0;
        foreach (float v in vector)
        {
            if (!float.IsFinite(v))
            {
                return null;
            }
            sum += (double)v * v;
        }
        if (sum <= 0)
        {
            return null;
        }
        float scale = (float)(1 / Math.Sqrt(sum));
        return [.. vector.Select(v => v * scale)];
    }

    /// <summary>The dot product of two vectors of the same length.</summary>
    public static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }
        return sum;
    }

    /// <summary>A vector as text for storage: its floats, 4 bytes each, in base64.</summary>
    public static string Pack(float[] vector)
    {
        byte[] bytes = new byte[vector.Length * 4];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>A stored vector back, or null when the text is not one.</summary>
    public static float[] Unpack(string packed)
    {
        if (string.IsNullOrEmpty(packed))
        {
            return null;
        }
        try
        {
            byte[] bytes = Convert.FromBase64String(packed);
            if (bytes.Length == 0 || bytes.Length % 4 != 0)
            {
                return null;
            }
            float[] vector = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            return vector;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
