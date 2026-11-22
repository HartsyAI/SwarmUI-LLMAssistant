using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>Where one user's memory values are kept: a tiny key-value store with no way to list keys (listing a user's generic data reads every row of it, chat threads included, under the
/// database lock, so nothing here may need it: <see cref="MemoryStore"/> keeps its own small index of the keys it made). In the extension it is the user's generic data (<see cref="GenericDataCache"/>); tests use a dictionary.</summary>
public interface IMemoryStorage
{
    /// <summary>The value under <paramref name="key"/>, or null when there is none.</summary>
    string Get(string key);

    /// <summary>Saves a value.</summary>
    void Save(string key, string value);

    /// <summary>Deletes a value (nothing happens when there is none).</summary>
    void Delete(string key);
}

/// <summary>One user's notes on top of an <see cref="IMemoryStorage"/>: every read-modify-write under that user's own lock, the first-read migration of the old profile, and the rules that
/// keep a bad stored value from being lost.
/// <para><b>Never destructive.</b> A stored value that does not read cleanly is never silently replaced: before any overwrite (a write, or Clear) its raw text is kept under a
/// <c>memory_notes_backup_&lt;time&gt;</c> key, and a value written by a newer version of the extension is read-only (writes are refused, nothing is overwritten).</para>
/// <para><b>Vectors</b> (optional embeddings) are one small value per note (<c>memory_vec_&lt;id&gt;</c>), not one blob, so a write never parses or rewrites the others, and a vector is only
/// accepted for the text it was made from.</para></summary>
public sealed partial class MemoryStore
{
    /// <summary>The key of the notes.</summary>
    public const string NotesKey = "memory_notes";

    /// <summary>The key the old profile blob was kept under.</summary>
    public const string LegacyProfileKey = "user_profile";

    /// <summary>The start of the key a note's embedding is kept under.</summary>
    public const string VectorPrefix = "memory_vec_";

    /// <summary>The start of the key a backup of an unreadable value is kept under.</summary>
    public const string BackupPrefix = "memory_notes_backup_";

    /// <summary>The key of the index of the vector and backup keys this store made (a JSON array of key names), so deleting them never needs a listing of the user's data.</summary>
    public const string IndexKey = "memory_keys";

    /// <summary>The most numbers in a vector that are kept (a larger one is not stored, which bounds the storage a user's notes can take: 400 notes at about 22 KB each).</summary>
    public const int MaxVectorDims = 4096;

    /// <summary>How many backups of unreadable values are kept (the newest).</summary>
    public const int MaxBackups = 3;

    /// <summary>How old a backup may be before it is deleted.</summary>
    public static readonly TimeSpan BackupMaxAge = TimeSpan.FromDays(30);

    /// <summary>The state last logged for a user's notes, so an unclean store is warned about once, not on every read. Held per gate (that is, per user).</summary>
    private sealed class LoggedState
    {
        /// <summary>What the last read said.</summary>
        public MemoryLoadStatus Status = MemoryLoadStatus.Ok;
    }

    /// <summary>The logged state of each user's notes, by the user's gate.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, LoggedState> Logged = new();

    /// <summary>Where the values are kept.</summary>
    private readonly IMemoryStorage Storage;

    /// <summary>Serializes this user's reads and writes (shared by every store made for the same user in this process).</summary>
    private readonly object Gate;

    /// <summary>The time, replaceable for tests.</summary>
    public Func<DateTime> Clock = () => DateTime.UtcNow;

    /// <summary>Receives a line for a problem worth logging (an unreadable value that was backed up, a refused write); null for none.</summary>
    public Action<string> Log;

    /// <summary>A store over <paramref name="storage"/>. Stores for one user must share <paramref name="gate"/>.</summary>
    public MemoryStore(IMemoryStorage storage, object gate = null)
    {
        Storage = storage;
        Gate = gate ?? new object();
    }

    /// <summary>What a read found.</summary>
    private sealed record Loaded(MemoryNoteSet Set, MemoryLoadStatus Status, string Raw);

    /// <summary>Reads the notes while holding <see cref="Gate"/>. When none were ever stored, the old profile (if any) is turned into notes and saved, which marks the migration done.</summary>
    private Loaded LoadLocked()
    {
        string raw = Storage.Get(NotesKey);
        MemoryParse parsed = MemoryNoteSet.ParseDetailed(raw);
        if (parsed.Status == MemoryLoadStatus.None)
        {
            MemoryNoteSet migrated = MemoryNoteSet.FromLegacyProfile(ReadLegacyProfile(), Clock());
            Storage.Save(NotesKey, migrated.Serialize());
            Logged.GetOrCreateValue(Gate).Status = MemoryLoadStatus.Ok;
            return new Loaded(migrated, MemoryLoadStatus.Ok, null);
        }
        // Said once per change of state (not on every read, which happens several times a turn).
        LoggedState state = Logged.GetOrCreateValue(Gate);
        if (state.Status != parsed.Status)
        {
            state.Status = parsed.Status;
            if (parsed.Status != MemoryLoadStatus.Ok)
            {
                Log?.Invoke(parsed.Status == MemoryLoadStatus.Newer
                    ? "The saved memory notes were written by a newer version of the extension; they are read-only until the extension is updated (writes are refused, nothing is overwritten)."
                    : $"The saved memory notes did not read cleanly ({parsed.Status}, {parsed.Skipped} notes skipped); they are kept as they are and backed up before anything overwrites them.");
            }
        }
        return new Loaded(parsed.Set, parsed.Status, raw);
    }

    /// <summary>The old profile blob, or null when there is none or it is unreadable.</summary>
    private JObject ReadLegacyProfile()
    {
        string raw = Storage.Get(LegacyProfileKey);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        try
        {
            return MemoryNoteSet.ParseJson(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- The index of keys ----

    /// <summary>The keys in the index.</summary>
    private HashSet<string> LoadIndex()
    {
        string raw = Storage.Get(IndexKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        try
        {
            return [.. JArray.Parse(raw).Select(t => t.ToString())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Saves the index (deleting it when it is empty).</summary>
    private void SaveIndex(HashSet<string> keys)
    {
        if (keys.Count == 0)
        {
            Storage.Delete(IndexKey);
        }
        else
        {
            Storage.Save(IndexKey, new JArray(keys.OrderBy(k => k, StringComparer.Ordinal)).ToString(Formatting.None));
        }
    }

    /// <summary>Saves a value and records its key in the index.</summary>
    private void SaveTracked(string key, string value)
    {
        Storage.Save(key, value);
        HashSet<string> index = LoadIndex();
        if (index.Add(key))
        {
            SaveIndex(index);
        }
    }

    /// <summary>Deletes values and takes their keys out of the index.</summary>
    private void DeleteTracked(IEnumerable<string> keys)
    {
        HashSet<string> index = LoadIndex();
        bool changed = false;
        foreach (string key in keys)
        {
            Storage.Delete(key);
            changed |= index.Remove(key);
        }
        if (changed)
        {
            SaveIndex(index);
        }
    }

    /// <summary>Deletes a note's vector when it has one (a cheap check first, so an ordinary write touches nothing).</summary>
    private void DeleteVector(string noteId)
    {
        string key = VectorPrefix + noteId;
        if (Storage.Get(key) is not null)
        {
            DeleteTracked([key]);
        }
    }

    /// <summary>Keeps the raw text of a value that did not read cleanly (or was written by a newer version), so overwriting it loses nothing. Only when a backup is made are the backups
    /// pruned (the newest <see cref="MaxBackups"/> are kept, none older than <see cref="BackupMaxAge"/>), so an ordinary write does none of this.</summary>
    private void BackupIfNeeded(Loaded loaded)
    {
        if (loaded.Status is MemoryLoadStatus.Lossy or MemoryLoadStatus.Corrupt or MemoryLoadStatus.Newer && !string.IsNullOrEmpty(loaded.Raw))
        {
            string key = BackupPrefix + Clock().ToUniversalTime().ToString("yyyyMMddHHmmssfff");
            SaveTracked(key, loaded.Raw);
            Log?.Invoke($"Backed up the unreadable memory notes under '{key}' before replacing them.");
            PruneBackups();
        }
    }

    /// <summary>Deletes the backups beyond the newest <see cref="MaxBackups"/> and any older than <see cref="BackupMaxAge"/>. The key's time is the backup's time.</summary>
    private void PruneBackups()
    {
        DateTime now = Clock().ToUniversalTime();
        int kept = 0;
        List<string> drop = [];
        foreach (string key in LoadIndex().Where(k => k.StartsWith(BackupPrefix, StringComparison.Ordinal)).OrderByDescending(k => k, StringComparer.Ordinal))
        {
            bool parsed = DateTime.TryParseExact(key[BackupPrefix.Length..], "yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime when);
            if (kept >= MaxBackups || parsed && now - when > BackupMaxAge)
            {
                drop.Add(key);
            }
            else
            {
                kept++;
            }
        }
        if (drop.Count > 0)
        {
            DeleteTracked(drop);
        }
    }

    /// <summary>The user's notes, as a copy that changes nothing until written back through this store.</summary>
    public MemoryNoteSet Load()
    {
        lock (Gate)
        {
            return LoadLocked().Set;
        }
    }

    /// <summary>How the stored notes read (see <see cref="MemoryLoadStatus"/>).</summary>
    public MemoryLoadStatus Status()
    {
        lock (Gate)
        {
            return LoadLocked().Status;
        }
    }

    /// <summary>Saves a note (see <see cref="MemoryNoteSet.Write"/>). Refused when the stored notes were written by a newer version; an unreadable stored value is backed up first.</summary>
    public MemoryWriteResult Write(string category, string text, string origin, string threadId)
    {
        lock (Gate)
        {
            Loaded loaded = LoadLocked();
            if (loaded.Status == MemoryLoadStatus.Newer)
            {
                Log?.Invoke("Memory was saved by a newer version of the extension; a write was refused.");
                return new MemoryWriteResult(false, "Memory was saved by a newer version of this extension, so nothing was saved. Update the extension to change it.", null, false);
            }
            BackupIfNeeded(loaded);
            MemoryWriteResult result = loaded.Set.Write(category, text, origin, threadId, Clock());
            if (result.Saved)
            {
                Storage.Save(NotesKey, loaded.Set.Serialize());
                foreach (string id in result.Evicted ?? [])
                {
                    DeleteVector(id);
                }
                // A slot note (name, pronouns, bio, current work) is always in the prompt and never ranked, so it has no vector; one left from before, or from an earlier value, goes.
                if (result.Note is { Slot.Length: > 0 })
                {
                    DeleteVector(result.Note.Id);
                }
            }
            return result;
        }
    }

    /// <summary>Forgets everything: every note, every vector, every backup of an unreadable value, and the old profile blob. The empty set is saved (not deleted), so the old profile is
    /// not migrated in again. This is the user asking for their memory to be gone, so nothing is kept back, not even a backup of a value that could not be read. Irreversible.</summary>
    public void Clear()
    {
        lock (Gate)
        {
            BumpEpoch();
            Storage.Save(NotesKey, new MemoryNoteSet().Serialize());
            Logged.GetOrCreateValue(Gate).Status = MemoryLoadStatus.Ok;
            // Every vector and backup this store made is in the index (which is all there is to go through: the user's data is never listed).
            DeleteTracked(LoadIndex());
            Storage.Delete(IndexKey);
            Storage.Delete(LegacyProfileKey);
        }
    }

    /// <summary>Marks notes as used (see <see cref="MemoryNoteSet.MarkUsed"/>), writing only when something changed and only when the stored notes read cleanly.</summary>
    public bool MarkUsed(IEnumerable<string> ids)
    {
        lock (Gate)
        {
            Loaded loaded = LoadLocked();
            if (loaded.Status != MemoryLoadStatus.Ok || !loaded.Set.MarkUsed(ids, Clock()))
            {
                return false;
            }
            Storage.Save(NotesKey, loaded.Set.Serialize());
            return true;
        }
    }

    /// <summary>What a vector is a vector of: a short hash of the note's text.</summary>
    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")))[..16];

    /// <summary>Keeps the embedding of a note, but only when the note still exists with the same text it was made from (a slower embedding of an older text must not replace the vector of a
    /// newer one, or outlive its note) and the vector is not larger than <see cref="MaxVectorDims"/>. Returns whether it was kept.</summary>
    public bool SaveVector(string noteId, string embeddedText, string vectorKey, float[] unitVector)
    {
        if (unitVector is null || unitVector.Length == 0 || unitVector.Length > MaxVectorDims)
        {
            return false;
        }
        lock (Gate)
        {
            MemoryNote note = LoadLocked().Set.Find(noteId);
            if (note is null || TextHash(note.Text) != TextHash(embeddedText))
            {
                return false;
            }
            SaveTracked(VectorPrefix + noteId, new JObject { ["k"] = vectorKey, ["h"] = TextHash(embeddedText), ["d"] = Knowledge.KnowledgeVectors.Pack(unitVector) }.ToString(Formatting.None));
            return true;
        }
    }

    /// <summary>The embeddings of <paramref name="set"/>'s notes that have a current one (made from the note's present text), by note id.</summary>
    public Dictionary<string, (float[] Vector, string Key)> LoadVectors(MemoryNoteSet set)
    {
        Dictionary<string, (float[] Vector, string Key)> result = [];
        foreach (MemoryNote note in set.Notes)
        {
            string raw = Storage.Get(VectorPrefix + note.Id);
            if (string.IsNullOrEmpty(raw))
            {
                continue;
            }
            try
            {
                JObject entry = JObject.Parse(raw);
                float[] vector = Knowledge.KnowledgeVectors.Unpack(entry["d"]?.ToString());
                string key = entry["k"]?.ToString();
                if (vector is not null && !string.IsNullOrEmpty(key) && entry["h"]?.ToString() == TextHash(note.Text))
                {
                    result[note.Id] = (vector, key);
                }
            }
            catch (JsonException)
            {
                // A vector that does not read is simply not used.
            }
        }
        return result;
    }
}
