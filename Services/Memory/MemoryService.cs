using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.Knowledge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>A user's atomic memory notes: where they are kept, the one way they are written, and how they reach a prompt. Strictly per user: every call is scoped to the
/// <see cref="User"/> it is given, and there is no shared or admin layer.
/// <para>Notes are stored like the old profile was (a generic-data value of the user's account, through <see cref="GenericDataCache"/>), under their own key. The first time a user's notes are
/// read and none exist, the old profile blob is turned into notes (see <see cref="MemoryNoteSet.FromLegacyProfile"/>); the blob is left where it is, and the existence of the notes key is
/// the "migrated" marker, so clearing memory (which saves an empty set) does not bring the old profile back.</para>
/// <para>Recall by meaning is optional and reuses the knowledge library's embedding setting (<see cref="KnowledgeService.GetSettings"/>, any backend that offers embeddings through
/// <see cref="IEmbeddingProvider"/>): with none set, or when embedding fails, notes are ranked by keywords alone and nothing is sent anywhere. Vectors are kept in a separate value so the
/// notes stay small, and are only read when a set is too big to render whole.</para></summary>
public static class MemoryService
{
    /// <summary>The generic-data name the notes live under (shared with the rest of the extension's per-user data).</summary>
    public const string DataName = "llmassistant";

    /// <summary>The key of the notes.</summary>
    public const string NotesKey = "memory_notes";

    /// <summary>The key of the notes' embeddings.</summary>
    public const string VectorsKey = "memory_vectors";

    /// <summary>The key the old profile blob was kept under.</summary>
    public const string LegacyProfileKey = "user_profile";

    /// <summary>The most characters of a turn's text used as the recall query.</summary>
    private const int MaxQueryChars = 1000;

    /// <summary>Serializes read-modify-write of one process's notes (the cache underneath has its own lock for single calls).</summary>
    private static readonly object Lock = new();

    // ---- Reading ----

    /// <summary>The user's notes (migrating the old profile on first use), as a copy that changes nothing until it is saved.</summary>
    public static MemoryNoteSet Load(User user)
    {
        if (user is null)
        {
            return new MemoryNoteSet();
        }
        lock (Lock)
        {
            return LoadLocked(user);
        }
    }

    /// <summary>Loads the notes while holding <see cref="Lock"/>.</summary>
    private static MemoryNoteSet LoadLocked(User user)
    {
        string raw = GenericDataCache.Get(user, DataName, NotesKey);
        MemoryNoteSet set = MemoryNoteSet.Parse(raw);
        if (set is not null)
        {
            return set;
        }
        if (string.IsNullOrEmpty(raw))
        {
            // Never migrated: turn the old profile (if there is one) into notes, and save them, which marks the migration done.
            set = MemoryNoteSet.FromLegacyProfile(ReadLegacyProfile(user), DateTime.UtcNow);
            SaveLocked(user, set);
            return set;
        }
        // Stored text that does not read as notes is never replaced with a migration (it might be repaired by hand); memory is empty until it is.
        Logs.Warning($"[LLMAssistant] The saved memory notes of {user.UserID} could not be read; memory is empty until they are repaired or cleared.");
        return new MemoryNoteSet();
    }

    /// <summary>The old profile blob, or null when there is none or it is unreadable.</summary>
    private static JObject ReadLegacyProfile(User user)
    {
        string raw = GenericDataCache.Get(user, DataName, LegacyProfileKey);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        try
        {
            return JObject.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Saves the notes. Caller holds <see cref="Lock"/>.</summary>
    private static void SaveLocked(User user, MemoryNoteSet set)
        => GenericDataCache.Save(user, DataName, NotesKey, set.Serialize());

    /// <summary>The user's notes in the old profile's shape (what the welcome screen reads), plus the notes themselves.</summary>

    /// <summary>The notes as a list for a client: each note's fields, in the fixed order.</summary>
    public static JArray NotesView(User user) => [.. MemoryRecall.StableOrder(Load(user).Notes).Select(NoteJson)];

    /// <summary>The notes of <paramref name="set"/> that match <paramref name="query"/> by keywords, best first (at most <see cref="MemoryRecall.MaxRanked"/> × 2), as a list for a client.</summary>
    public static JArray RankNotes(MemoryNoteSet set, string query)
    {
        Dictionary<string, MemoryNote> byId = set.Notes.ToDictionary(n => n.Id);
        List<IndexedChunk> chunks = [.. set.Notes.Select(n => IndexedChunk.Make(n.Id, "", 0, n.Text, n.Kind))];
        return [.. KnowledgeIndex.Search(chunks, query, null, null, MemoryRecall.MaxRanked * 2).Select(h => NoteJson(byId[h.Chunk.DocId]))];
    }

    /// <summary>One note's fields for a client.</summary>
    private static JObject NoteJson(MemoryNote note) => new()
    {
        ["id"] = note.Id,
        ["text"] = note.Text,
        ["kind"] = note.Kind,
        ["origin"] = note.Origin,
        ["threadId"] = note.ThreadId,
        ["created"] = note.Created,
        ["lastUsed"] = note.LastUsed,
        ["pinned"] = note.Pinned,
        ["slot"] = note.Slot,
        ["negative"] = note.Negative
    };

    // ---- Writing ----

    /// <summary>Saves a note for the user (the <c>memory_write</c> tool's one path). When the user has an embedding model set, the note is embedded too, so recall can find it by meaning;
    /// a failure there is logged and the note is kept without a vector. Never throws except when cancelled.</summary>
    public static async Task<MemoryWriteResult> WriteAsync(Session session, string category, string text, string origin, string threadId, CancellationToken ct)
    {
        User user = session?.User;
        if (user is null)
        {
            return new MemoryWriteResult(false, "No user context — memory not saved.", null, false);
        }
        if (string.IsNullOrWhiteSpace(category))
        {
            return new MemoryWriteResult(false, "No category provided.", null, false);
        }
        MemoryWriteResult result;
        lock (Lock)
        {
            MemoryNoteSet set = LoadLocked(user);
            result = set.Write(category, text, origin, threadId, DateTime.UtcNow);
            if (result.Saved)
            {
                SaveLocked(user, set);
            }
        }
        // A repeat of a note already kept has its vector already; a new note, or a slot given a new value, needs one.
        if (result.Saved && result.Note is not null && (result.Added || result.Note.Slot.Length > 0))
        {
            await EmbedNoteAsync(session, result.Note, ct);
        }
        return result;
    }

    /// <summary>Removes every note and every vector. The empty set is saved (not deleted), so the old profile is not migrated in again. Irreversible.</summary>
    public static void Clear(User user)
    {
        if (user is null)
        {
            return;
        }
        lock (Lock)
        {
            SaveLocked(user, new MemoryNoteSet());
            GenericDataCache.Delete(user, DataName, VectorsKey);
            GenericDataCache.Delete(user, DataName, LegacyProfileKey);
        }
    }

    // ---- Embeddings (optional) ----

    /// <summary>Embeds a note with the user's embedding model, if one is set, and keeps the vector. Best effort: any failure is logged and ignored.</summary>
    private static async Task EmbedNoteAsync(Session session, MemoryNote note, CancellationToken ct)
    {
        try
        {
            KnowledgeSettings settings = KnowledgeService.GetSettings(session.User);
            IEmbeddingProvider provider = settings.Enabled ? KnowledgeService.ResolveProvider(settings.ProviderId) : null;
            if (provider is null)
            {
                return;
            }
            float[][] vectors = await provider.EmbedAsync(settings.Model.Trim(), [note.Text.Length > 2000 ? note.Text[..2000] : note.Text], session, ct);
            float[] unit = vectors is { Length: > 0 } ? KnowledgeVectors.Normalize(vectors[0]) : null;
            if (unit is null)
            {
                return;
            }
            string key = KnowledgeVectors.Key(settings.ProviderId, settings.Model.Trim(), unit.Length);
            lock (Lock)
            {
                JObject store = ReadVectors(session.User);
                store[note.Id] = new JObject { ["k"] = key, ["d"] = KnowledgeVectors.Pack(unit) };
                HashSet<string> live = [.. LoadLocked(session.User).Notes.Select(n => n.Id)];
                foreach (string stale in store.Properties().Select(p => p.Name).Where(id => !live.Contains(id)).ToList())
                {
                    store.Remove(stale);
                }
                GenericDataCache.Save(session.User, DataName, VectorsKey, store.ToString(Formatting.None));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Warning($"[LLMAssistant] Embedding a memory note failed, so it is found by keywords: {ex.Message}");
        }
    }

    /// <summary>The stored vectors as <c>{ noteId: { k, d } }</c> (empty when there are none).</summary>
    private static JObject ReadVectors(User user)
    {
        try
        {
            string raw = GenericDataCache.Get(user, DataName, VectorsKey);
            return string.IsNullOrEmpty(raw) ? [] : JObject.Parse(raw);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The stored vectors by note id.</summary>
    private static Dictionary<string, (float[] Vector, string Key)> LoadVectors(User user)
    {
        Dictionary<string, (float[] Vector, string Key)> result = [];
        foreach (KeyValuePair<string, JToken> entry in ReadVectors(user))
        {
            float[] vector = KnowledgeVectors.Unpack(entry.Value?["d"]?.ToString());
            string key = entry.Value?["k"]?.ToString();
            if (vector is not null && !string.IsNullOrEmpty(key))
            {
                result[entry.Key] = (vector, key);
            }
        }
        return result;
    }

    // ---- Recall into a prompt ----

    /// <summary>The recall request for the next reply in <paramref name="thread"/>: the user's latest message as the query, and (only when the notes are too many to render whole and the
    /// user has an embedding model set) that message embedded. A dry run records no use and embeds nothing. Never throws except when cancelled; any failure leaves keywords alone.</summary>
    public static async Task<MemoryRecallRequest> PrepareRecallAsync(Session session, JObject thread, bool dryRun, CancellationToken ct)
    {
        string query = QueryFromThread(thread);
        MemoryRecallRequest keywords = new(query, null, null, !dryRun);
        if (dryRun || session?.User is null || query.Length == 0)
        {
            return keywords;
        }
        try
        {
            MemoryNoteSet set = Load(session.User);
            if (!MemoryRecall.NeedsRanking(set.Notes))
            {
                return keywords;
            }
            KnowledgeSettings settings = KnowledgeService.GetSettings(session.User);
            IEmbeddingProvider provider = settings.Enabled ? KnowledgeService.ResolveProvider(settings.ProviderId) : null;
            if (provider is null || ReadVectors(session.User).Count == 0)
            {
                return keywords;
            }
            float[][] vectors = await provider.EmbedAsync(settings.Model.Trim(), [query], session, ct);
            float[] unit = vectors is { Length: > 0 } ? KnowledgeVectors.Normalize(vectors[0]) : null;
            return unit is null ? keywords : new MemoryRecallRequest(query, unit, KnowledgeVectors.Key(settings.ProviderId, settings.Model.Trim(), unit.Length), true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logs.Warning($"[LLMAssistant] Embedding the turn for memory recall failed, so notes are found by keywords: {ex.Message}");
            return keywords;
        }
    }

    /// <summary>The text of the latest user message on the chat's active branch (cut to <see cref="MaxQueryChars"/>), or empty.</summary>
    public static string QueryFromThread(JObject thread)
    {
        if (thread is null)
        {
            return "";
        }
        List<JObject> path = ThreadStorageService.GetActivePath(thread);
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (path[i]["role"]?.ToString() == Roles.User)
            {
                string text = path[i]["content"]?.ToString() ?? "";
                return text.Length > MaxQueryChars ? text[..MaxQueryChars] : text;
            }
        }
        return "";
    }

    /// <summary>The prompt block of notes for <paramref name="user"/> (empty when there are none). With <paramref name="recall"/> null the notes are chosen without a query (all of them while
    /// they fit the budget, which is what keeps the prompt stable) and no use is recorded.</summary>
    public static string RenderForPrompt(User user, MemoryRecallRequest recall = null) => user is null ? "" : RenderForPrompt(user, Load(user), recall);

    /// <summary>The prompt block for notes already loaded.</summary>
    private static string RenderForPrompt(User user, MemoryNoteSet set, MemoryRecallRequest recall)
    {
        Dictionary<string, (float[] Vector, string Key)> vectors = recall?.QueryVector is not null && MemoryRecall.NeedsRanking(set.Notes) ? LoadVectors(user) : null;
        List<MemoryNote> chosen = MemoryRecall.Select(set.Notes, recall, vectors);
        if (recall?.RecordUse == true && chosen.Count > 0)
        {
            lock (Lock)
            {
                MemoryNoteSet fresh = LoadLocked(user);
                if (fresh.MarkUsed(chosen.Select(n => n.Id), DateTime.UtcNow))
                {
                    SaveLocked(user, fresh);
                }
            }
        }
        return MemoryRecall.Render(chosen);
    }

    /// <summary>The variables an instruction's <c>{{...}}</c> placeholders are filled from. <c>{{userProfile}}</c> is the notes block (the name kept for the assistants and saved
    /// prompts that use it). Called when assembling a system prompt.</summary>
    public static Dictionary<string, string> BuildPromptVariables(User user, JObject assistant, MemoryRecallRequest recall = null)
    {
        MemoryNoteSet set = Load(user);
        string name = set.UserName();
        return new Dictionary<string, string>
        {
            ["userName"] = string.IsNullOrWhiteSpace(name) ? "" : MemoryNoteSet.Squash(name),
            ["userProfile"] = user is null ? "" : RenderForPrompt(user, set, recall),
            ["currentDate"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["assistantName"] = assistant?["name"]?.ToString() ?? "Assistant"
        };
    }
}
