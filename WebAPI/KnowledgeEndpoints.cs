using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.Knowledge;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>The Knowledge tab's routes: list the library, add and delete documents, search it, and choose (and try) the embedding model. All need <c>llm_knowledge</c> and work on the caller's own
/// library only; no request names a path, and a document is only ever added from text sent in the request.</summary>
public static class KnowledgeEndpoints
{
    /// <summary>The caller's documents, the totals against the limits, the embedding setting, and the providers that can embed.</summary>
    public static Task<JObject> LLMAssistantGetKnowledge(Session session)
    {
        List<KnowledgeDocument> docs = KnowledgeService.List(session.User);
        KnowledgeSettings settings = KnowledgeService.GetSettings(session.User);
        JArray providers = [.. KnowledgeService.Providers().Select(p => new JObject { ["id"] = p.EmbeddingProviderId, ["name"] = p.EmbeddingProviderName })];
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["documents"] = new JArray(docs.Select(Describe)),
            ["settings"] = new JObject { ["providerId"] = settings.ProviderId, ["model"] = settings.Model },
            ["providers"] = providers,
            ["limits"] = new JObject { ["documents"] = KnowledgeStore.MaxDocuments, ["chunks"] = KnowledgeStore.MaxChunks, ["documentChars"] = KnowledgeStore.MaxDocumentChars },
            ["totals"] = new JObject { ["documents"] = docs.Count, ["chunks"] = docs.Sum(d => d.Chunks), ["chars"] = docs.Sum(d => (long)d.Chars) },
        });
    }

    /// <summary>One document as the tab shows it.</summary>
    private static JObject Describe(KnowledgeDocument doc) => new()
    {
        ["id"] = doc.Id,
        ["title"] = doc.Title,
        ["fileName"] = doc.FileName,
        ["addedAt"] = doc.AddedAt,
        ["chars"] = doc.Chars,
        ["chunks"] = doc.Chunks,
        ["embedded"] = !string.IsNullOrEmpty(doc.VectorKey),
        ["embedError"] = doc.EmbedError,
    };

    /// <summary>Adds a document from its text. Request: <c>{name, title?, text}</c> (the text is at most 1,000,000 characters and must be text). Reply: <c>{success, document}</c>; a document
    /// that was added but could not be embedded still succeeds, with the reason in <c>document.embedError</c>.</summary>
    public static async Task<JObject> LLMAssistantAddKnowledge(Session session, JObject rawInput)
    {
        string text = rawInput["text"]?.ToString();
        if (text is null)
        {
            return Fail("text is required.");
        }
        try
        {
            KnowledgeDocument doc = await KnowledgeService.AddAsync(session, rawInput["name"]?.ToString() ?? "", rawInput["title"]?.ToString(), text, CancellationToken.None);
            return new JObject { ["success"] = true, ["document"] = Describe(doc) };
        }
        catch (KnowledgeLimitException ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>Deletes a document.</summary>
    public static Task<JObject> LLMAssistantDeleteKnowledge(Session session, string docId)
        => Task.FromResult(!string.IsNullOrEmpty(docId) && docId.All(Uri.IsHexDigit) && KnowledgeService.Delete(session.User, docId) ? new JObject { ["success"] = true } : Fail("There is no such document."));

    /// <summary>Searches the library (the tab's test box, and the same search the tool does). Request: <c>{query, limit?}</c>. Reply: <c>{success, mode, results[{title, docId, piece, heading?, text, score, keyword, semantic}], embedError?}</c>.</summary>
    public static async Task<JObject> LLMAssistantSearchKnowledge(Session session, JObject rawInput)
    {
        string query = rawInput["query"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return Fail("query is required.");
        }
        int limit = rawInput["limit"]?.Type == JTokenType.Integer ? rawInput["limit"].Value<int>() : 5;
        KnowledgeSearchResult found = await KnowledgeService.SearchAsync(session, query, limit, CancellationToken.None);
        JObject reply = new()
        {
            ["success"] = true,
            ["mode"] = found.Mode,
            ["results"] = new JArray(found.Hits.Select(h => new JObject
            {
                ["title"] = h.Chunk.DocTitle,
                ["docId"] = h.Chunk.DocId,
                ["piece"] = h.Chunk.Number,
                ["heading"] = h.Chunk.Heading,
                ["text"] = h.Chunk.Text,
                ["score"] = Math.Round(h.Score, 4),
                ["keyword"] = h.Keyword,
                ["semantic"] = h.Semantic,
            })),
        };
        if (found.EmbedError is not null)
        {
            reply["embedError"] = found.EmbedError;
        }
        return reply;
    }

    /// <summary>Chooses the embedding model. Request: <c>{providerId, model}</c>; both empty switches embedding off (documents already embedded keep their vectors until they are re-embedded
    /// or removed, and are not used). A model is tried first (one short text is sent to it), so a wrong one is found now: the reply is <c>{success, dimension}</c>, or the reason it failed and
    /// nothing is saved.</summary>
    public static async Task<JObject> LLMAssistantSetKnowledgeSettings(Session session, JObject rawInput)
    {
        KnowledgeSettings settings = new() { ProviderId = rawInput["providerId"]?.ToString()?.Trim() ?? "", Model = rawInput["model"]?.ToString()?.Trim() ?? "" };
        if (!settings.Enabled && (settings.ProviderId.Length > 0 || settings.Model.Length > 0))
        {
            return Fail("Pick both a backend and a model, or neither.");
        }
        int dimension = 0;
        if (settings.Enabled)
        {
            (dimension, string error) = await KnowledgeService.ProbeAsync(session, settings, CancellationToken.None);
            if (error is not null)
            {
                return Fail($"That model did not work: {error}");
            }
        }
        KnowledgeService.SaveSettings(session.User, settings);
        return new JObject { ["success"] = true, ["dimension"] = dimension };
    }

    /// <summary>Embeds every document again with the current setting, or removes their vectors when there is none. Reply: <c>{success, embedded, failed}</c>.</summary>
    public static async Task<JObject> LLMAssistantReindexKnowledge(Session session)
    {
        (int embedded, int failed) = await KnowledgeService.ReindexAsync(session, CancellationToken.None);
        return new JObject { ["success"] = true, ["embedded"] = embedded, ["failed"] = failed };
    }

    /// <summary>A failed reply.</summary>
    private static JObject Fail(string error) => new() { ["success"] = false, ["error"] = error };
}
