using System.IO;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Knowledge;

/// <summary>Which provider and model a user's documents are embedded with. Both empty means the library is searched by keywords alone, which needs no model and sends nothing anywhere.</summary>
public sealed class KnowledgeSettings
{
    /// <summary>The embedding provider's id (<see cref="IEmbeddingProvider.EmbeddingProviderId"/>).</summary>
    public string ProviderId = "";
    /// <summary>The model id to embed with, as the provider knows it.</summary>
    public string Model = "";

    /// <summary>Whether embedding is switched on.</summary>
    [JsonIgnore]
    public bool Enabled => !string.IsNullOrWhiteSpace(ProviderId) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>What a search found and how: the best pieces, whether vectors took part (<c>hybrid</c>) or keywords alone did (<c>keyword</c>), and why embedding the query failed when it did.</summary>
public sealed record KnowledgeSearchResult(List<KnowledgeHit> Hits, string Mode, string EmbedError, int Documents, int Chunks);

/// <summary>A user's knowledge library: documents they added, cut into pieces and searched by keywords, and by meaning too when they picked an embedding model. The model (and the provider
/// that serves it) is the user's own setting, kept apart from the chat model, and any provider that offers embeddings can be used (<see cref="IEmbeddingProvider"/>); with none picked,
/// or when embedding fails, search still works by keywords, and a failed embedding never fails adding a document or a search.</summary>
public static class KnowledgeService
{
    /// <summary>How many pieces are sent to the embedding model in one request.</summary>
    private const int EmbedBatch = 32;

    /// <summary>The most characters of a piece (with its heading) sent to the embedding model.</summary>
    private const int MaxEmbedChars = 2000;

    /// <summary>The most results one search returns.</summary>
    public const int MaxResults = 10;

    /// <summary>The generic-data name and key a user's settings are kept under.</summary>
    private const string SettingsData = "llmassistant_knowledge", SettingsKey = "settings";

    /// <summary>The library folder of a user (null when the user has no name to make one from). Replaceable for tests.</summary>
    public static Func<User, string> RootFor = user =>
    {
        string key = Utilities.StrictFilenameClean(user?.UserID ?? "").Trim('.', ' ');
        return key.Length == 0 ? null : Path.Combine(Program.DataDir, "LLMAssistant", "knowledge", "users", key);
    };

    /// <summary>The folder of the user's library, or an error when there is none to use.</summary>
    private static string Root(User user) => RootFor(user) ?? throw new KnowledgeLimitException("This user has no library.");

    // ---- Settings and providers ----

    /// <summary>The user's embedding setting.</summary>
    public static KnowledgeSettings GetSettings(User user)
    {
        try
        {
            string raw = user.GetGenericData(SettingsData, SettingsKey);
            return string.IsNullOrEmpty(raw) ? new KnowledgeSettings() : JsonConvert.DeserializeObject<KnowledgeSettings>(raw) ?? new KnowledgeSettings();
        }
        catch (JsonException)
        {
            return new KnowledgeSettings();
        }
    }

    /// <summary>Saves the user's embedding setting.</summary>
    public static void SaveSettings(User user, KnowledgeSettings settings)
        => user.SaveGenericData(SettingsData, SettingsKey, JsonConvert.SerializeObject(settings));

    /// <summary>The providers that can embed, for the setting's list.</summary>
    public static List<IEmbeddingProvider> Providers() => [.. LLMProviderRegistry.All.OfType<IEmbeddingProvider>().Where(p => p.SupportsEmbeddings)];

    /// <summary>The provider an id names, if it can embed.</summary>
    public static IEmbeddingProvider ResolveProvider(string id)
        => string.IsNullOrWhiteSpace(id) ? null : Providers().FirstOrDefault(p => p.EmbeddingProviderId == id);

    /// <summary>Tries a setting: embeds one short text and says how many numbers a vector has, or why it did not work. Used when the user saves the setting, so a wrong model is
    /// found then and not at the first document.</summary>
    public static async Task<(int Dimension, string Error)> ProbeAsync(Session session, KnowledgeSettings settings, CancellationToken ct)
    {
        IEmbeddingProvider provider = ResolveProvider(settings.ProviderId);
        if (provider is null)
        {
            return (0, "That backend cannot embed text. Pick one from the list.");
        }
        try
        {
            float[][] vectors = await provider.EmbedAsync(settings.Model.Trim(), ["knowledge library check"], session, ct);
            float[] unit = KnowledgeVectors.Normalize(vectors[0]);
            return unit is null ? (0, "The model returned a vector that cannot be used.") : (unit.Length, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, Short(ex.Message));
        }
    }

    // ---- Documents ----

    /// <summary>The documents in the user's library.</summary>
    public static List<KnowledgeDocument> List(User user) => KnowledgeStore.List(Root(user));

    /// <summary>Removes a document.</summary>
    public static bool Delete(User user, string docId) => KnowledgeStore.Delete(Root(user), docId);

    /// <summary>Adds a document from its text. The text is tidied, cut into pieces and stored; then embedded when the user has a model set (a failure there is kept on the document, and
    /// the document is still searchable by keywords). Throws <see cref="KnowledgeLimitException"/> with the reason when it cannot be added: no text, not text, too big, library full.</summary>
    public static async Task<KnowledgeDocument> AddAsync(Session session, string fileName, string title, string rawText, CancellationToken ct)
    {
        User user = session.User;
        string name = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        // Looked at before anything reads it: a page of HTML is larger than its text, but not by a factor that makes a very large request worth parsing.
        if ((rawText?.Length ?? 0) > KnowledgeStore.MaxDocumentChars * 2)
        {
            throw new KnowledgeLimitException($"That document is too long ({rawText.Length:N0} characters; at most {KnowledgeStore.MaxDocumentChars:N0} of text). Split it into parts.");
        }
        string text;
        try
        {
            text = KnowledgeText.Normalize(rawText, name);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new KnowledgeLimitException("That document could not be read as text in a reasonable time. Is it a very large or damaged web page?");
        }
        if (text.Length == 0)
        {
            throw new KnowledgeLimitException("There is no text in that document.");
        }
        if (KnowledgeText.LooksBinary(text))
        {
            throw new KnowledgeLimitException("That does not look like a text file. Documents must be plain text, Markdown, HTML, CSV, JSON or code.");
        }
        if (text.Length > KnowledgeStore.MaxDocumentChars)
        {
            throw new KnowledgeLimitException($"That document is too long ({text.Length:N0} characters; at most {KnowledgeStore.MaxDocumentChars:N0}). Split it into parts.");
        }
        string cleanTitle = System.Text.RegularExpressions.Regex.Replace(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(name) : title, @"\s+", " ").Trim();
        if (cleanTitle.Length == 0)
        {
            cleanTitle = "Untitled";
        }
        if (cleanTitle.Length > 120)
        {
            cleanTitle = cleanTitle[..120].TrimEnd();
        }
        List<KnowledgeChunk> chunks = KnowledgeText.Chunk(text);
        KnowledgeSettings settings = GetSettings(user);
        List<float[]> vectors = null;
        string key = null;
        string error = null;
        if (settings.Enabled)
        {
            (vectors, key, error) = await EmbedAsync(session, settings, chunks, ct);
        }
        return KnowledgeStore.Add(Root(user), cleanTitle, name, text.Length, chunks, vectors, key, error);
    }

    /// <summary>Embeds pieces with the user's model: the vectors (unit length, in order) and what they were made with, or the reason it failed. Never throws except when cancelled.</summary>
    private static async Task<(List<float[]> Vectors, string Key, string Error)> EmbedAsync(Session session, KnowledgeSettings settings, List<KnowledgeChunk> chunks, CancellationToken ct)
    {
        IEmbeddingProvider provider = ResolveProvider(settings.ProviderId);
        if (provider is null)
        {
            return (null, null, "The backend chosen for embeddings is not available.");
        }
        List<float[]> vectors = [];
        try
        {
            for (int start = 0; start < chunks.Count; start += EmbedBatch)
            {
                List<string> inputs = [.. chunks.Skip(start).Take(EmbedBatch).Select(c => EmbedInput(c))];
                float[][] batch = await provider.EmbedAsync(settings.Model.Trim(), inputs, session, ct);
                foreach (float[] vector in batch)
                {
                    float[] unit = KnowledgeVectors.Normalize(vector);
                    if (unit is null || (vectors.Count > 0 && vectors[0].Length != unit.Length))
                    {
                        return (null, null, "The model returned vectors that cannot be used (empty, not a number, or of different sizes).");
                    }
                    vectors.Add(unit);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Warning($"[LLMAssistant] Embedding failed, so the document is searched by keywords: {ex.Message}");
            return (null, null, Short(ex.Message));
        }
        return vectors.Count == 0 ? (null, null, null) : (vectors, KnowledgeVectors.Key(settings.ProviderId, settings.Model.Trim(), vectors[0].Length), null);
    }

    /// <summary>The text sent to the embedding model for a piece: its heading (which says what it is about) and the text, bounded.</summary>
    private static string EmbedInput(KnowledgeChunk chunk)
    {
        string text = string.IsNullOrEmpty(chunk.Heading) ? chunk.Text : $"{chunk.Heading}\n{chunk.Text}";
        return text.Length > MaxEmbedChars ? text[..MaxEmbedChars] : text;
    }

    /// <summary>Embeds every document again with the current setting (or removes their vectors when there is none), for after the model was changed. Returns how many documents were
    /// embedded and how many failed.</summary>
    public static async Task<(int Embedded, int Failed)> ReindexAsync(Session session, CancellationToken ct)
    {
        string root = Root(session.User);
        KnowledgeSettings settings = GetSettings(session.User);
        int embedded = 0;
        int failed = 0;
        foreach (KnowledgeDocument doc in KnowledgeStore.List(root))
        {
            List<KnowledgeChunk> chunks = KnowledgeStore.ReadChunks(root, doc.Id);
            if (chunks is null)
            {
                continue;
            }
            if (!settings.Enabled)
            {
                KnowledgeStore.SetVectors(root, doc.Id, null, null, null);
                continue;
            }
            (List<float[]> vectors, string key, string error) = await EmbedAsync(session, settings, chunks, ct);
            KnowledgeStore.SetVectors(root, doc.Id, vectors, key, error);
            if (vectors is null)
            {
                failed++;
            }
            else
            {
                embedded++;
            }
        }
        return (embedded, failed);
    }

    // ---- Search ----

    /// <summary>Searches the user's library. Keywords always; vectors too when the user has a model set, the query could be embedded, and some pieces were embedded by the same model.</summary>
    public static async Task<KnowledgeSearchResult> SearchAsync(Session session, string query, int limit, CancellationToken ct)
    {
        string root = Root(session.User);
        List<IndexedChunk> index = KnowledgeStore.LoadIndex(root);
        int documents = index.Select(c => c.DocId).Distinct().Count();
        limit = Math.Clamp(limit, 1, MaxResults);
        KnowledgeSettings settings = GetSettings(session.User);
        float[] queryVector = null;
        string vectorKey = null;
        string error = null;
        if (settings.Enabled && index.Any(c => c.Vector is not null))
        {
            IEmbeddingProvider provider = ResolveProvider(settings.ProviderId);
            if (provider is null)
            {
                error = "The backend chosen for embeddings is not available.";
            }
            else
            {
                try
                {
                    float[][] vectors = await provider.EmbedAsync(settings.Model.Trim(), [query ?? ""], session, ct);
                    queryVector = KnowledgeVectors.Normalize(vectors[0]);
                    vectorKey = queryVector is null ? null : KnowledgeVectors.Key(settings.ProviderId, settings.Model.Trim(), queryVector.Length);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logs.Warning($"[LLMAssistant] Embedding the query failed, so the search uses keywords: {ex.Message}");
                    error = Short(ex.Message);
                }
            }
        }
        List<KnowledgeHit> hits = KnowledgeIndex.Search(index, query, queryVector, vectorKey, limit);
        string mode = queryVector is not null && hits.Any(h => h.Semantic) ? "hybrid" : "keyword";
        return new KnowledgeSearchResult(hits, mode, error, documents, index.Count);
    }

    /// <summary>A message cut to a length fit for a list.</summary>
    private static string Short(string message) => string.IsNullOrEmpty(message) ? "Embedding failed." : message.Length > 300 ? message[..300] : message;
}
