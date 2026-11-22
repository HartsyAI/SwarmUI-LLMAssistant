using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>What a change the user made to their notes did.</summary>
/// <param name="Ok">Whether it was done.</param>
/// <param name="Error">Why not, as a short code: <c>newer</c> (the notes were written by a newer version), <c>notfound</c>, <c>empty</c>, <c>kind</c>, <c>slot</c> (a slot note cannot change kind or be unpinned),
/// <c>duplicate</c>, <c>pinlimit</c>, <c>nothing</c>, or empty.</param>
/// <param name="Count">How many notes changed (a delete or an import).</param>
/// <param name="Note">The note after an edit.</param>
public sealed record MemoryManageResult(bool Ok, string Error, int Count = 0, MemoryNote Note = null);

/// <summary>The notes' store, with what the user does to their own notes in Settings: edit, pin, delete, export, import, and the retention sweep. Every one runs under the user's lock, refuses to
/// write over notes saved by a newer version, and (like every write) keeps a backup of a stored value that did not read cleanly before replacing it. None of it is journaled except retention
/// (the user did these themselves, on a screen that shows what they are doing); retention is undoable like the extractor's changes.</summary>
public sealed partial class MemoryStore
{
    /// <summary>The kinds a user may give a note: the ordinary kinds, and <c>dislike</c> (a preference that is a "do not").</summary>
    public static readonly string[] ManualKinds = [MemoryKinds.Preference, "dislike", MemoryKinds.Fact, MemoryKinds.Project, MemoryKinds.Style];

    /// <summary>The most notes a retention sweep forgets at once (the rest go on the next sweep), so one change set stays small.</summary>
    public const int MaxRetentionPerSweep = 100;

    /// <summary>One note in full (the list sends it cut), or null.</summary>
    public MemoryNote Get(string id)
    {
        lock (Gate)
        {
            MemoryNote note = LoadLocked().Set.Find(id);
            return note is null ? null : JObject.FromObject(note).ToObject<MemoryNote>();
        }
    }

    /// <summary>The notes and how they read, from one parse (reading them and then asking for the status would be two).</summary>
    public (MemoryNoteSet Set, MemoryLoadStatus Status) LoadWithStatus()
    {
        lock (Gate)
        {
            Loaded loaded = LoadLocked();
            return (loaded.Set, loaded.Status);
        }
    }

    /// <summary>Whether the stored notes may be written by the user's own hand: not when a newer version of the extension wrote them.</summary>
    private bool TryBeginManualWrite(out Loaded loaded, out MemoryManageResult refused)
    {
        loaded = LoadLocked();
        if (loaded.Status == MemoryLoadStatus.Newer)
        {
            Log?.Invoke("Memory was saved by a newer version of the extension; a change from Settings was refused.");
            refused = new MemoryManageResult(false, "newer");
            return false;
        }
        refused = null;
        return true;
    }

    /// <summary>Changes one note. Each of <paramref name="text"/>, <paramref name="kind"/> (one of <see cref="ManualKinds"/>) and <paramref name="pinned"/> is left as it is when null. A new text makes
    /// the note the user's own (origin <c>user</c>) and drops its vector. A slot note keeps its slot, its kind and its pin. The same duplicate rule as saving applies, and pinning stops at
    /// <see cref="MemoryNoteSet.MaxPinned"/>.</summary>
    public MemoryManageResult Update(string id, string text, string kind, bool? pinned)
    {
        lock (Gate)
        {
            if (!TryBeginManualWrite(out Loaded loaded, out MemoryManageResult refused))
            {
                return refused;
            }
            MemoryNoteSet set = loaded.Set;
            MemoryNote note = set.Find(id);
            if (note is null)
            {
                return new MemoryManageResult(false, "notfound");
            }
            string body = text is null ? note.Text : MemoryNoteSet.Truncate(text.Trim(), MemoryNoteSet.MaxNoteChars);
            if (body.Length == 0)
            {
                return new MemoryManageResult(false, "empty");
            }
            string newKind = note.Kind;
            bool newNegative = note.Negative;
            if (kind is not null)
            {
                string wanted = kind.Trim().ToLowerInvariant();
                if (!ManualKinds.Contains(wanted))
                {
                    return new MemoryManageResult(false, "kind");
                }
                (newKind, newNegative) = wanted == "dislike" ? (MemoryKinds.Preference, true) : (wanted, false);
                if (note.Slot.Length > 0 && (newKind != note.Kind || newNegative != note.Negative))
                {
                    return new MemoryManageResult(false, "slot");
                }
            }
            if (note.Slot.Length > 0 && pinned == false)
            {
                return new MemoryManageResult(false, "slot");
            }
            if (pinned == true && !note.Pinned && set.Notes.Count(n => n.Pinned && n.Slot.Length == 0) >= MemoryNoteSet.MaxPinned)
            {
                return new MemoryManageResult(false, "pinlimit");
            }
            if (note.Slot.Length == 0 && set.Notes.Any(n => n.Id != note.Id && n.Slot.Length == 0 && n.Kind == newKind && n.Negative == newNegative && MemoryNoteSet.Squash(n.Text).Equals(MemoryNoteSet.Squash(body), StringComparison.OrdinalIgnoreCase)))
            {
                return new MemoryManageResult(false, "duplicate");
            }
            bool textChanged = body != note.Text;
            bool becamePinned = pinned == true && !note.Pinned;
            if (!textChanged && newKind == note.Kind && newNegative == note.Negative && (pinned is null || pinned == note.Pinned))
            {
                return new MemoryManageResult(true, "", 0, note);
            }
            BackupIfNeeded(loaded);
            note.Text = body;
            note.Kind = newKind;
            note.Negative = newNegative;
            if (pinned is not null)
            {
                note.Pinned = pinned.Value;
            }
            if (textChanged)
            {
                note.Origin = MemoryOrigins.User;
            }
            Storage.Save(NotesKey, set.Serialize());
            if (textChanged || becamePinned)
            {
                // A changed text is no longer what the vector was made from, and a pinned note is never embedded.
                DeleteVector(note.Id);
            }
            return new MemoryManageResult(true, "", 1, note);
        }
    }

    /// <summary>Adds a note the user wrote (origin <c>user</c>): an ordinary note of one of <see cref="ManualKinds"/> (<c>fact</c> when <paramref name="kind"/> is empty), or, with <paramref name="slot"/>
    /// (name, pronouns, bio or currentWork), a pinned slot note when that slot is empty. Unlike the model's save it never drops an existing note to make room: a full memory refuses. The same duplicate
    /// rule applies, and pinning stops at <see cref="MemoryNoteSet.MaxPinned"/>. Error codes as <see cref="MemoryManageResult"/>, plus <c>full</c> and <c>slotfilled</c>.</summary>
    public MemoryManageResult Add(string text, string kind, bool pinned, string slot)
    {
        lock (Gate)
        {
            if (!TryBeginManualWrite(out Loaded loaded, out MemoryManageResult refused))
            {
                return refused;
            }
            MemoryNoteSet set = loaded.Set;
            string body = MemoryNoteSet.Truncate((text ?? "").Trim(), MemoryNoteSet.MaxNoteChars);
            if (body.Length == 0)
            {
                return new MemoryManageResult(false, "empty");
            }
            slot = (slot ?? "").Trim();
            string wanted = (kind ?? "").Trim().ToLowerInvariant();
            string newKind = MemoryKinds.Fact;
            bool negative = false;
            if (slot.Length > 0)
            {
                if (!MemorySlots.All.Contains(slot))
                {
                    return new MemoryManageResult(false, "slot");
                }
                if (set.Slot(slot) is not null)
                {
                    return new MemoryManageResult(false, "slotfilled");
                }
                newKind = slot == MemorySlots.CurrentWork ? MemoryKinds.Project : MemoryKinds.Fact;
                pinned = true;
            }
            else
            {
                if (wanted.Length > 0 && !ManualKinds.Contains(wanted))
                {
                    return new MemoryManageResult(false, "kind");
                }
                (newKind, negative) = wanted == "dislike" ? (MemoryKinds.Preference, true) : (wanted.Length == 0 ? MemoryKinds.Fact : wanted, false);
                if (set.Notes.Any(n => n.Slot.Length == 0 && n.Kind == newKind && n.Negative == negative && MemoryNoteSet.Squash(n.Text).Equals(MemoryNoteSet.Squash(body), StringComparison.OrdinalIgnoreCase)))
                {
                    return new MemoryManageResult(false, "duplicate");
                }
            }
            if (set.Notes.Count >= MemoryNoteSet.MaxNotes)
            {
                return new MemoryManageResult(false, "full");
            }
            if (pinned && slot.Length == 0 && set.Notes.Count(n => n.Pinned && n.Slot.Length == 0) >= MemoryNoteSet.MaxPinned)
            {
                return new MemoryManageResult(false, "pinlimit");
            }
            BackupIfNeeded(loaded);
            MemoryNote note = new()
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Text = body,
                Kind = newKind,
                Origin = MemoryOrigins.User,
                ThreadId = "",
                Created = Clock().ToUniversalTime().ToString("o"),
                Pinned = pinned,
                Slot = slot,
                Negative = negative
            };
            set.Notes.Add(note);
            Storage.Save(NotesKey, set.Serialize());
            return new MemoryManageResult(true, "", 1, note);
        }
    }

    /// <summary>Deletes notes by id (an id that is not there is ignored) with their vectors. Returns how many were deleted. Not journaled: it is the user's own deletion, and it is final.</summary>
    public MemoryManageResult Delete(IEnumerable<string> ids)
    {
        lock (Gate)
        {
            if (!TryBeginManualWrite(out Loaded loaded, out MemoryManageResult refused))
            {
                return refused;
            }
            HashSet<string> wanted = [.. ids.Where(i => !string.IsNullOrEmpty(i))];
            List<string> gone = [.. loaded.Set.Notes.Where(n => wanted.Contains(n.Id)).Select(n => n.Id)];
            if (gone.Count == 0)
            {
                return new MemoryManageResult(true, "", 0);
            }
            BackupIfNeeded(loaded);
            loaded.Set.Notes.RemoveAll(n => wanted.Contains(n.Id));
            Storage.Save(NotesKey, loaded.Set.Serialize());
            foreach (string id in gone)
            {
                DeleteVector(id);
            }
            return new MemoryManageResult(true, "", gone.Count);
        }
    }

    /// <summary>What importing <paramref name="data"/> would do to the notes as they are now; changes nothing.</summary>
    public MemoryImportPlan PreviewImport(string data, MemoryNeverSave never)
    {
        lock (Gate)
        {
            return MemoryImport.Plan(LoadLocked().Set, data, never, Clock());
        }
    }

    /// <summary>Adds what <see cref="MemoryImport.Plan"/> works out from <paramref name="data"/> against the notes as they are now (never what a client sent as a plan). Nothing existing is
    /// changed or dropped. Returns the plan, with <paramref name="refused"/> set when the notes were written by a newer version.</summary>
    public MemoryImportPlan Import(string data, MemoryNeverSave never, out string refused)
    {
        lock (Gate)
        {
            refused = "";
            if (!TryBeginManualWrite(out Loaded loaded, out MemoryManageResult blocked))
            {
                refused = blocked.Error;
                return new MemoryImportPlan { Error = blocked.Error };
            }
            MemoryImportPlan plan = MemoryImport.Plan(loaded.Set, data, never, Clock());
            if (plan.Error.Length == 0 && plan.Add.Count > 0)
            {
                BackupIfNeeded(loaded);
                loaded.Set.Notes.AddRange(plan.Add);
                Storage.Save(NotesKey, loaded.Set.Serialize());
            }
            return plan;
        }
    }

    /// <summary>The notes as an export (see <see cref="MemoryImport.Export"/>).</summary>
    public JObject Export()
    {
        lock (Gate)
        {
            return MemoryImport.Export(LoadLocked().Set, Clock());
        }
    }

    /// <summary>Forgets the notes that have not been used for <paramref name="days"/> days (a note never used counts from when it was saved): never a pinned or slot note, at most
    /// <see cref="MaxRetentionPerSweep"/> at a time, oldest first. What went is kept in the retention journal (<see cref="RetentionJournalKey"/>, its own, up to <see cref="MaxRetentionSets"/> sets, apart from the
    /// extractor's) as a change set with the reason <see cref="MemoryChangeReasons.Retention"/>, so it can be undone from the history. With <paramref name="lazy"/> (a sweep a reply starts) nothing is forgotten when the user has
    /// been away for longer than <paramref name="days"/>: every note's unused time starts again instead. Does nothing (returns null) when <paramref name="days"/> is not above 0, when nothing is old enough, or when the stored notes were written by a newer version
    /// or do not read cleanly (nothing is deleted from a store that cannot be trusted).</summary>
    public MemoryChangeSet ApplyRetention(int days, bool lazy = false)
    {
        if (days <= 0)
        {
            return null;
        }
        lock (Gate)
        {
            Loaded loaded = LoadLocked();
            if (loaded.Status != MemoryLoadStatus.Ok)
            {
                return null;
            }
            DateTime now = Clock().ToUniversalTime();
            DateTime cutoff = now - TimeSpan.FromDays(days);
            if (lazy && loaded.Set.Notes.Count > 0)
            {
                // A lazy sweep (one a reply starts) after the user has been away for longer than the retention time forgets nothing: the notes were not unused, memory was. Every note's unused time
                // starts again, so the first reply back can recall them. The last use of anything is the best record of when memory was last in use.
                DateTime lastActivity = loaded.Set.Notes.Max(n => n.LastUsedAt ?? (n.CreatedAt == DateTime.MinValue ? now : n.CreatedAt));
                if (now - lastActivity.ToUniversalTime() > TimeSpan.FromDays(days))
                {
                    string stamp = now.ToString("o");
                    foreach (MemoryNote n in loaded.Set.Notes)
                    {
                        n.LastUsed = stamp;
                    }
                    Storage.Save(NotesKey, loaded.Set.Serialize());
                    return null;
                }
            }
            // A note whose times do not read is not old: it is left alone rather than forgotten on a guess.
            List<MemoryNote> old = [.. loaded.Set.Notes
                .Where(n => !n.Pinned && n.Slot.Length == 0)
                .Select(n => (Note: n, Time: n.LastUsedAt ?? (n.CreatedAt == DateTime.MinValue ? (DateTime?)null : n.CreatedAt)))
                .Where(x => x.Time is not null && x.Time.Value.ToUniversalTime() < cutoff)
                .OrderBy(x => x.Time)
                .Take(MaxRetentionPerSweep)
                .Select(x => x.Note)];
            if (old.Count == 0)
            {
                return null;
            }
            List<MemoryChange> changes = [.. old.Select(n => new MemoryChange(MemoryChangeTypes.Removed, n.Id, NoteJson(n), null))];
            HashSet<string> gone = [.. old.Select(n => n.Id)];
            loaded.Set.Notes.RemoveAll(n => gone.Contains(n.Id));
            Storage.Save(NotesKey, loaded.Set.Serialize());
            foreach (string id in gone)
            {
                DeleteVector(id);
            }
            MemoryChangeSet record = new(Guid.NewGuid().ToString("N")[..12], now.ToString("o"), "", changes, MemoryChangeReasons.Retention);
            List<MemoryChangeSet> journal = LoadJournal(RetentionJournalKey);
            journal.Add(record);
            SaveJournal(journal, RetentionJournalKey, MaxRetentionSets);
            return record;
        }
    }
}
