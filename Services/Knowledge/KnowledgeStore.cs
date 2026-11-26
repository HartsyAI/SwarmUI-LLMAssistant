using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Knowledge;

/// <summary>What the library knows about one document. The text of its pieces is in the document's own file.</summary>
public sealed class KnowledgeDocument
{
    /// <summary>Its id (unguessable, made by the server).</summary>
    public string Id = "";
    /// <summary>The name shown in lists and in search results.</summary>
    public string Title = "";
    /// <summary>The file name it came from.</summary>
    public string FileName = "";
    /// <summary>When it was added (UTC, ISO 8601).</summary>
    public string AddedAt = "";
    /// <summary>The characters of text it holds.</summary>
    public int Chars;
    /// <summary>How many pieces it was cut into.</summary>
    public int Chunks;
    /// <summary>What its vectors were made with (<see cref="KnowledgeVectors.Key"/>), or empty when it has none.</summary>
    public string VectorKey = "";
    /// <summary>Why embedding failed the last time it was tried, or empty.</summary>
    public string EmbedError = "";
}

/// <summary>Why a document could not be added.</summary>
public sealed class KnowledgeLimitException(string message) : Exception(message);

/// <summary>The files a user's library lives in, under one folder: <c>library.json</c> (the list of documents) and <c>docs/&lt;id&gt;.json</c> (each document's pieces, and their vectors
/// once it was embedded). The folder is in the server's data folder and not in the user's output folder, which is where the file tools write, so the model has no way to put a
/// document into the library or change one. It works on a folder it is given, so tests can use a temporary one. Every change is made under one lock per folder and written to a
/// temporary file first, so a crash never leaves half a file; links are never followed.</summary>
public static class KnowledgeStore
{
    /// <summary>The most documents in a library.</summary>
    public const int MaxDocuments = 200;
    /// <summary>The most characters in one document (1 MB of text).</summary>
    public const int MaxDocumentChars = 1_000_000;
    /// <summary>The most pieces in a whole library, so a search stays quick and the index fits in memory.</summary>
    public const int MaxChunks = 5000;

    /// <summary>One lock per folder.</summary>
    private static readonly ConcurrentDictionary<string, object> Locks = new();

    /// <summary>The searchable pieces last built for a folder, with when, so they are rebuilt after any change.</summary>
    private static readonly ConcurrentDictionary<string, (long Stamp, List<IndexedChunk> Chunks)> Cache = new();

    /// <summary>The folders whose pieces are kept in memory at once; more than this and the cache starts over.</summary>
    private const int MaxCachedLibraries = 4;

    /// <summary>The lock for a folder.</summary>
    private static object LockFor(string root) => Locks.GetOrAdd(Path.GetFullPath(root), _ => new object());

    /// <summary>The documents in a library, oldest first.</summary>
    public static List<KnowledgeDocument> List(string root)
    {
        lock (LockFor(root))
        {
            return ReadLibrary(root, false);
        }
    }

    /// <summary>Adds a document made of these pieces (and their vectors, when it was embedded). Refuses what would break a limit. Returns the document.</summary>
    public static KnowledgeDocument Add(string root, string title, string fileName, int chars, List<KnowledgeChunk> chunks, List<float[]> vectors, string vectorKey, string embedError)
    {
        lock (LockFor(root))
        {
            List<KnowledgeDocument> library = ReadLibrary(root, true);
            if (library.Count >= MaxDocuments)
            {
                throw new KnowledgeLimitException($"The library holds at most {MaxDocuments} documents. Delete one first.");
            }
            if (library.Sum(d => d.Chunks) + chunks.Count > MaxChunks)
            {
                throw new KnowledgeLimitException($"The library is full: it holds at most {MaxChunks} pieces of text (about {MaxChunks * KnowledgeText.TargetChunkChars / 1_000_000} MB). Delete a document first.");
            }
            KnowledgeDocument doc = new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                FileName = fileName,
                AddedAt = DateTime.UtcNow.ToString("o"),
                Chars = chars,
                Chunks = chunks.Count,
                VectorKey = vectors is null ? "" : vectorKey ?? "",
                EmbedError = embedError ?? "",
            };
            WriteChunks(root, doc.Id, chunks, vectors);
            library.Add(doc);
            WriteLibrary(root, library);
            Cache.TryRemove(Path.GetFullPath(root), out _);
            return doc;
        }
    }

    /// <summary>Removes a document. False when there was none.</summary>
    public static bool Delete(string root, string docId)
    {
        lock (LockFor(root))
        {
            List<KnowledgeDocument> library = ReadLibrary(root, true);
            KnowledgeDocument doc = library.FirstOrDefault(d => d.Id == docId);
            if (doc is null)
            {
                return false;
            }
            library.Remove(doc);
            WriteLibrary(root, library);
            string file = ChunkFile(root, docId);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
            Cache.TryRemove(Path.GetFullPath(root), out _);
            return true;
        }
    }

    /// <summary>The pieces of one document as stored (text and heading, in order), or null when it is not there.</summary>
    public static List<KnowledgeChunk> ReadChunks(string root, string docId)
    {
        lock (LockFor(root))
        {
            return ReadChunkFile(root, docId)?.Chunks;
        }
    }

    /// <summary>Replaces a document's vectors (null removes them) and records what they were made with, or why embedding failed.</summary>
    public static void SetVectors(string root, string docId, List<float[]> vectors, string vectorKey, string embedError)
    {
        lock (LockFor(root))
        {
            List<KnowledgeDocument> library = ReadLibrary(root, true);
            KnowledgeDocument doc = library.FirstOrDefault(d => d.Id == docId);
            ChunkFileContent content = ReadChunkFile(root, docId);
            if (doc is null || content is null)
            {
                return;
            }
            WriteChunks(root, docId, content.Chunks, vectors is not null && vectors.Count == content.Chunks.Count ? vectors : null);
            doc.VectorKey = vectors is null ? "" : vectorKey ?? "";
            doc.EmbedError = embedError ?? "";
            WriteLibrary(root, library);
            Cache.TryRemove(Path.GetFullPath(root), out _);
        }
    }

    /// <summary>Every piece in the library, ready to search (words counted, vectors read). Kept in memory until the library changes.</summary>
    public static List<IndexedChunk> LoadIndex(string root)
    {
        lock (LockFor(root))
        {
            string key = Path.GetFullPath(root);
            long stamp = File.Exists(LibraryFile(root)) ? File.GetLastWriteTimeUtc(LibraryFile(root)).Ticks : 0;
            if (Cache.TryGetValue(key, out (long Stamp, List<IndexedChunk> Chunks) cached) && cached.Stamp == stamp)
            {
                return cached.Chunks;
            }
            List<IndexedChunk> all = [];
            Dictionary<string, string> words = [];
            string Intern(string word)
            {
                if (!words.TryGetValue(word, out string pooled))
                {
                    words[word] = pooled = word;
                }
                return pooled;
            }
            foreach (KnowledgeDocument doc in ReadLibrary(root, false))
            {
                ChunkFileContent content = ReadChunkFile(root, doc.Id);
                if (content is null)
                {
                    continue;
                }
                for (int i = 0; i < content.Chunks.Count; i++)
                {
                    IndexedChunk chunk = IndexedChunk.Make(doc.Id, doc.Title, content.Chunks[i].Number, content.Chunks[i].Text, content.Chunks[i].Heading, Intern);
                    chunk.Page = content.Chunks[i].Page;
                    if (content.Vectors is not null && i < content.Vectors.Count && content.Vectors[i] is { } vector)
                    {
                        chunk.Vector = vector;
                        chunk.VectorKey = doc.VectorKey;
                    }
                    all.Add(chunk);
                }
            }
            if (Cache.Count >= MaxCachedLibraries)
            {
                Cache.Clear();
            }
            Cache[key] = (stamp, all);
            return all;
        }
    }

    /// <summary>The library's list file.</summary>
    private static string LibraryFile(string root) => Path.Combine(root, "library.json");

    /// <summary>A document's file of pieces. The id is made by the server and is only ever hex, so no part of a request becomes a path.</summary>
    private static string ChunkFile(string root, string docId)
    {
        if (docId.Length == 0 || !docId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Not a document id.", nameof(docId));
        }
        return Path.Combine(root, "docs", docId + ".json");
    }

    /// <summary>How many times a read or a replace is tried when the file is busy, and how long to wait between tries (milliseconds). Another program (a virus scanner, a backup, an indexer)
    /// can hold a file for a moment, and a read that gave up and saw nothing must never be mistaken for an empty library.</summary>
    private const int FileTries = 8, FileWaitMs = 40;

    /// <summary>Reads the list of documents. A missing file is an empty library. A file that stays busy cannot be read: for display that is an empty list (and is logged), but
    /// <paramref name="forChange"/> says the list is about to be changed and written back, and then it is an error, because writing a list made from nothing over the real one would
    /// lose every document. A file that is not valid JSON is moved aside (so it is kept) and the library starts again.</summary>
    private static List<KnowledgeDocument> ReadLibrary(string root, bool forChange)
    {
        string file = LibraryFile(root);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(file) || IsLink(file))
                {
                    return [];
                }
                return JsonConvert.DeserializeObject<List<KnowledgeDocument>>(File.ReadAllText(file, Encoding.UTF8)) ?? [];
            }
            catch (JsonException ex)
            {
                Logs.Warning($"[LLMAssistant] The knowledge library list in {root} is damaged, so it is kept as library.json.damaged and the library starts again: {ex.Message}");
                if (forChange)
                {
                    File.Move(file, file + ".damaged", true);
                }
                return [];
            }
            catch (IOException ex)
            {
                if (attempt < FileTries)
                {
                    Thread.Sleep(FileWaitMs);
                    continue;
                }
                Logs.Warning($"[LLMAssistant] The knowledge library list in {root} could not be read: {ex.Message}");
                if (forChange)
                {
                    throw new KnowledgeLimitException("The library is busy; try again in a moment.");
                }
                return [];
            }
        }
    }

    /// <summary>Saves the list of documents.</summary>
    private static void WriteLibrary(string root, List<KnowledgeDocument> library) => WriteAtomic(LibraryFile(root), JsonConvert.SerializeObject(library, Formatting.None));

    /// <summary>What a document's file holds.</summary>
    private sealed class ChunkFileContent
    {
        /// <summary>The pieces, in order.</summary>
        public List<KnowledgeChunk> Chunks = [];
        /// <summary>Their vectors (unit length) in the same order, or null; an entry is null for a piece that has none.</summary>
        public List<float[]> Vectors;
    }

    /// <summary>Reads a document's file, or null when it is missing or damaged.</summary>
    private static ChunkFileContent ReadChunkFile(string root, string docId)
    {
        try
        {
            string file = ChunkFile(root, docId);
            if (!File.Exists(file) || IsLink(file))
            {
                return null;
            }
            JObject json = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
            ChunkFileContent content = new();
            foreach (JObject piece in (json["chunks"] as JArray ?? []).OfType<JObject>())
            {
                content.Chunks.Add(new KnowledgeChunk(piece["n"]?.Value<int>() ?? content.Chunks.Count, piece["text"]?.ToString() ?? "", piece["heading"]?.ToString() ?? "", piece["page"]?.Value<int>() ?? 0));
                if (piece["vec"]?.ToString() is { Length: > 0 } packed)
                {
                    content.Vectors ??= [];
                    while (content.Vectors.Count < content.Chunks.Count - 1)
                    {
                        content.Vectors.Add(null);
                    }
                    content.Vectors.Add(KnowledgeVectors.Unpack(packed));
                }
            }
            return content;
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            Logs.Warning($"[LLMAssistant] A knowledge document file could not be read, so it is skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>Saves a document's pieces and vectors.</summary>
    private static void WriteChunks(string root, string docId, List<KnowledgeChunk> chunks, List<float[]> vectors)
    {
        JArray pieces = [];
        for (int i = 0; i < chunks.Count; i++)
        {
            JObject piece = new() { ["n"] = chunks[i].Number, ["text"] = chunks[i].Text, ["heading"] = chunks[i].Heading };
            if (chunks[i].Page > 0)
            {
                piece["page"] = chunks[i].Page;
            }
            if (vectors is not null && i < vectors.Count && vectors[i] is { } vector)
            {
                piece["vec"] = KnowledgeVectors.Pack(vector);
            }
            pieces.Add(piece);
        }
        WriteAtomic(ChunkFile(root, docId), new JObject { ["chunks"] = pieces }.ToString(Formatting.None));
    }

    /// <summary>Writes a file through a temporary one that is then moved over it.</summary>
    private static void WriteAtomic(string file, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string temp = file + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, file, true);
                return;
            }
            catch (IOException) when (attempt < FileTries)
            {
                // Something else has the file open for a moment (a scanner, an indexer): wait and try again rather than lose the change.
                Thread.Sleep(FileWaitMs);
            }
        }
    }

    /// <summary>Whether a path is a symbolic link or junction (never followed).</summary>
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
