using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>What one automatic change did to the notes.</summary>
public static class MemoryChangeTypes
{
    /// <summary>A note was added.</summary>
    public const string Added = "added";
    /// <summary>A note's text was replaced.</summary>
    public const string Updated = "updated";
    /// <summary>A note was deleted.</summary>
    public const string Removed = "removed";
    /// <summary>A note was dropped to make room for an added one (the oldest unused, unpinned note).</summary>
    public const string Evicted = "evicted";
}

/// <summary>What made a change set, when it was not the automatic extractor.</summary>
public static class MemoryChangeReasons
{
    /// <summary>Notes were forgotten because they had not been used for the number of days the user set.</summary>
    public const string Retention = "retention";
}

/// <summary>One note's change, with the note as it was and as it became (both whole, so undoing needs nothing else).</summary>
/// <param name="Type">See <see cref="MemoryChangeTypes"/>.</param>
/// <param name="NoteId">The note.</param>
/// <param name="Before">The note before (null for an added note).</param>
/// <param name="After">The note after (null for a removed or evicted note).</param>
public sealed record MemoryChange(string Type, string NoteId, JObject Before, JObject After)
{
    /// <summary>The note's text before, or empty.</summary>
    public string BeforeText => Before?["text"]?.ToString() ?? "";

    /// <summary>The note's text after, or empty.</summary>
    public string AfterText => After?["text"]?.ToString() ?? "";

    /// <summary>The change as stored.</summary>
    public JObject ToJson() => new() { ["type"] = Type, ["noteId"] = NoteId, ["before"] = Before, ["after"] = After };

    /// <summary>A change from its stored form, or null when it does not read.</summary>
    public static MemoryChange FromJson(JToken token)
    {
        if (token is not JObject o || string.IsNullOrEmpty(o["type"]?.ToString()) || string.IsNullOrEmpty(o["noteId"]?.ToString()))
        {
            return null;
        }
        return new MemoryChange(o["type"].ToString(), o["noteId"].ToString(), o["before"] as JObject, o["after"] as JObject);
    }
}

/// <summary>Everything one automatic extraction changed, kept so it can be shown and undone as a whole.</summary>
/// <param name="Id">A short random id.</param>
/// <param name="At">When (UTC, ISO 8601).</param>
/// <param name="ThreadId">The chat the notes came from.</param>
/// <param name="Changes">The changes, in the order they were made.</param>
/// <param name="Reason">What made the change: empty for the automatic extractor, <see cref="MemoryChangeReasons.Retention"/> for notes forgotten because they were unused. Absent in a set stored before it existed.</param>
public sealed record MemoryChangeSet(string Id, string At, string ThreadId, IReadOnlyList<MemoryChange> Changes, string Reason = "")
{
    /// <summary>The set as stored.</summary>
    public JObject ToJson() => new() { ["id"] = Id, ["at"] = At, ["threadId"] = ThreadId, ["reason"] = Reason, ["changes"] = new JArray(Changes.Select(c => c.ToJson())) };

    /// <summary>A set from its stored form, or null when it does not read.</summary>
    public static MemoryChangeSet FromJson(JToken token)
    {
        if (token is not JObject o || string.IsNullOrEmpty(o["id"]?.ToString()) || o["changes"] is not JArray array)
        {
            return null;
        }
        List<MemoryChange> changes = [.. array.Select(MemoryChange.FromJson).Where(c => c is not null)];
        return changes.Count == 0 ? null : new MemoryChangeSet(o["id"].ToString(), o["at"]?.ToString() ?? "", o["threadId"]?.ToString() ?? "", changes, o["reason"]?.ToString() ?? "");
    }

    /// <summary>The set for a client: what was added, updated (with what it said before), removed and dropped to make room, each note's text cleaned the way the prompt block cleans it and
    /// cut short (so the notice is small and a note cannot carry markup into it).</summary>
    public JObject ToClientJson(int maxChars = 160)
    {
        string Text(string raw) => MemoryRecall.Clean(MemoryNoteSet.Truncate(raw, maxChars));
        JArray Pick(string type, bool useBefore) => [.. Changes.Where(c => c.Type == type).Select(c => new JObject { ["id"] = c.NoteId, ["text"] = Text(useBefore ? c.BeforeText : c.AfterText) })];
        JArray updated = [.. Changes.Where(c => c.Type == MemoryChangeTypes.Updated).Select(c => new JObject { ["id"] = c.NoteId, ["text"] = Text(c.AfterText), ["previous"] = Text(c.BeforeText) })];
        return new JObject
        {
            ["id"] = Id,
            ["at"] = At,
            ["threadId"] = ThreadId,
            ["reason"] = Reason,
            ["added"] = Pick(MemoryChangeTypes.Added, false),
            ["updated"] = updated,
            ["removed"] = Pick(MemoryChangeTypes.Removed, true),
            ["evicted"] = Pick(MemoryChangeTypes.Evicted, true)
        };
    }
}

/// <summary>One operation the extractor wants done, already checked in code (see <see cref="MemoryOpParser"/>).</summary>
/// <param name="Kind"><c>add</c>, <c>update</c> or <c>delete</c>.</param>
/// <param name="NoteId">The real id of the note an update or delete is for (empty for an add).</param>
/// <param name="BaseHash">The <see cref="MemoryStore.TextHash"/> of the text the extractor was shown for that note: the operation is dropped if the note's text is not that any more.</param>
/// <param name="NoteKind">The kind of an added note (<see cref="MemoryKinds"/>).</param>
/// <param name="Text">The new text (empty for a delete).</param>
/// <param name="Negative">Whether an added note is a dislike; for an update, a new value for the flag, or null to leave it as it is.</param>
/// <param name="Origin">The origin the note is given (<see cref="MemoryOrigins"/>), decided in code.</param>
public sealed record MemoryOp(string Kind, string NoteId, string BaseHash, string NoteKind, string Text, bool? Negative, string Origin);

/// <summary>What applying operations did.</summary>
/// <param name="Changes">The changes saved, or null when nothing was.</param>
/// <param name="Skipped">How many operations were dropped (the note had changed, was pinned, was already saved, or there was no room).</param>
/// <param name="Refused">Why nothing was written at all (<c>newer</c>, <c>unreadable</c>, <c>cancelled</c>), or empty.</param>
public sealed record MemoryApplyResult(MemoryChangeSet Changes, int Skipped, string Refused);

/// <summary>What undoing a change set did.</summary>
/// <param name="Undone">How many changes were reverted.</param>
/// <param name="Skipped">How many were left as they are, because the note was changed since (or there was no room to bring it back).</param>
/// <param name="Found">Whether the change set was still in the journal.</param>
/// <param name="Refused">Why nothing was written (<c>newer</c>, <c>unreadable</c>), or empty.</param>
public sealed record MemoryUndoResult(int Undone, int Skipped, bool Found, string Refused);

/// <summary>The notes' store, with the writes the automatic extractor makes and the journal that lets them be undone.</summary>
public sealed partial class MemoryStore
{
    /// <summary>The key of the journal of automatic changes (kept in the key index, so Clear removes it).</summary>
    public const string ChangesKey = "memory_changes";

    /// <summary>How many change sets the journal keeps (the newest).</summary>
    public const int MaxChangeSets = 10;

    /// <summary>The note as a copy.</summary>
    private static JObject NoteJson(MemoryNote note) => JObject.FromObject(note);

    /// <summary>The journal's change sets, oldest first (an entry that does not read is left out).</summary>
    private List<MemoryChangeSet> LoadJournal()
    {
        string raw = Storage.Get(ChangesKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        try
        {
            return [.. MemoryNoteSet.ParseJsonArray(raw).Select(MemoryChangeSet.FromJson).Where(s => s is not null)];
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Saves the journal, newest <see cref="MaxChangeSets"/> only (deleting it when it is empty).</summary>
    private void SaveJournal(List<MemoryChangeSet> sets)
    {
        if (sets.Count == 0)
        {
            DeleteTracked([ChangesKey]);
            return;
        }
        SaveTracked(ChangesKey, new JArray(sets.TakeLast(MaxChangeSets).Select(s => s.ToJson())).ToString(Newtonsoft.Json.Formatting.None));
    }

    /// <summary>The journal of automatic changes, newest first.</summary>
    public IReadOnlyList<MemoryChangeSet> Changes()
    {
        lock (Gate)
        {
            return [.. LoadJournal().AsEnumerable().Reverse()];
        }
    }

    /// <summary>What a user's notes are at one moment of <see cref="Clear"/>: bumped by every Clear, so a write that was prepared before it (the extractor reads the notes, waits for a model, then
    /// saves) can tell that the memory was forgotten meanwhile and must not be written.</summary>
    private sealed class EpochBox
    {
        public int Value;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, EpochBox> Epochs = new();

    /// <summary>The user's current epoch (see <see cref="ApplyExtraction"/>).</summary>
    public int Epoch()
    {
        lock (Gate)
        {
            return Epochs.GetOrCreateValue(Gate).Value;
        }
    }

    /// <summary>Called by <see cref="Clear"/>, under the lock.</summary>
    private void BumpEpoch() => Epochs.GetOrCreateValue(Gate).Value++;

    /// <summary>Whether two stored notes are the same note apart from when it was last used (which recall refreshes without anyone changing the note).</summary>
    private static bool SameExceptUse(JObject a, JObject b)
    {
        if (a is null || b is null)
        {
            return false;
        }
        JObject x = (JObject)a.DeepClone();
        JObject y = (JObject)b.DeepClone();
        x.Remove("lastUsed");
        y.Remove("lastUsed");
        return JToken.DeepEquals(x, y);
    }

    /// <summary>Applies the extractor's operations as one change and keeps what it did in the journal. Everything is decided again here under the user's lock, against the notes as they are now:
    /// an update or delete is dropped when its note is gone, pinned, a slot note, or has other text than the extractor was shown; an add goes through the same duplicate check and room-making
    /// as every write (so a full set drops its oldest unused, unpinned note, and that is recorded too). Nothing is written when the stored notes were written by a newer version or do not
    /// read cleanly (the extractor never backs up or replaces what the user's own writes would), when <paramref name="cancelled"/> says so before the save, or when memory was cleared since
    /// <paramref name="epoch"/> was read (<see cref="Epoch"/>).</summary>
    public MemoryApplyResult ApplyExtraction(IReadOnlyList<MemoryOp> ops, string threadId, Func<bool> cancelled = null, int? epoch = null)
    {
        lock (Gate)
        {
            if (epoch is not null && epoch.Value != Epochs.GetOrCreateValue(Gate).Value)
            {
                return new MemoryApplyResult(null, ops.Count, "cleared");
            }
            Loaded loaded = LoadLocked();
            if (loaded.Status != MemoryLoadStatus.Ok)
            {
                Log?.Invoke($"The extractor saved nothing: the stored notes are {loaded.Status}.");
                return new MemoryApplyResult(null, ops.Count, loaded.Status == MemoryLoadStatus.Newer ? "newer" : "unreadable");
            }
            MemoryNoteSet set = loaded.Set;
            Dictionary<string, JObject> before = set.Notes.ToDictionary(n => n.Id, NoteJson);
            List<MemoryChange> changes = [];
            int skipped = 0;
            foreach (MemoryOp op in ops)
            {
                if (cancelled?.Invoke() == true)
                {
                    return new MemoryApplyResult(null, ops.Count, "cancelled");
                }
                if (op.Kind == "add")
                {
                    string category = op.Negative == true ? "dislike" : op.NoteKind;
                    MemoryWriteResult result = set.Write(category, op.Text, op.Origin, threadId, Clock());
                    if (!result.Saved || !result.Added || result.Note is null)
                    {
                        skipped++;
                        continue;
                    }
                    changes.Add(new MemoryChange(MemoryChangeTypes.Added, result.Note.Id, null, NoteJson(result.Note)));
                    foreach (string id in result.Evicted ?? [])
                    {
                        if (before.TryGetValue(id, out JObject gone))
                        {
                            changes.Add(new MemoryChange(MemoryChangeTypes.Evicted, id, gone, null));
                        }
                    }
                    continue;
                }
                MemoryNote note = set.Find(op.NoteId);
                if (note is null || note.Pinned || note.Slot.Length > 0 || TextHash(note.Text) != op.BaseHash)
                {
                    skipped++;
                    continue;
                }
                if (op.Kind == "delete")
                {
                    JObject was = NoteJson(note);
                    set.Remove(note.Id);
                    changes.Add(new MemoryChange(MemoryChangeTypes.Removed, note.Id, was, null));
                    continue;
                }
                string text = MemoryNoteSet.Truncate(op.Text.Trim(), MemoryNoteSet.MaxNoteChars);
                bool negative = note.Kind == MemoryKinds.Preference && op.Negative is not null ? op.Negative.Value : note.Negative;
                bool sameAsOther = set.Notes.Any(n => n.Id != note.Id && n.Slot.Length == 0 && n.Kind == note.Kind && n.Negative == negative && MemoryNoteSet.Squash(n.Text).Equals(MemoryNoteSet.Squash(text), StringComparison.OrdinalIgnoreCase));
                bool nothingNew = negative == note.Negative && MemoryNoteSet.Squash(note.Text).Equals(MemoryNoteSet.Squash(text), StringComparison.OrdinalIgnoreCase);
                if (nothingNew || sameAsOther)
                {
                    skipped++;
                    continue;
                }
                JObject old = NoteJson(note);
                note.Text = text;
                note.Negative = negative;
                // The origin is the one the parser decided: a note the user stated or asked to have saved keeps that origin only when the new text is made of the user's own words (see MemoryOpParser).
                note.Origin = op.Origin;
                note.ThreadId = threadId ?? "";
                changes.Add(new MemoryChange(MemoryChangeTypes.Updated, note.Id, old, NoteJson(note)));
            }
            if (changes.Count == 0)
            {
                return new MemoryApplyResult(null, skipped, "");
            }
            if (cancelled?.Invoke() == true)
            {
                return new MemoryApplyResult(null, ops.Count, "cancelled");
            }
            Storage.Save(NotesKey, set.Serialize());
            foreach (MemoryChange change in changes.Where(c => c.Type != MemoryChangeTypes.Added))
            {
                DeleteVector(change.NoteId);
            }
            MemoryChangeSet record = new(Guid.NewGuid().ToString("N")[..12], Clock().ToUniversalTime().ToString("o"), threadId ?? "", changes);
            List<MemoryChangeSet> journal = LoadJournal();
            journal.Add(record);
            SaveJournal(journal);
            return new MemoryApplyResult(record, skipped, "");
        }
    }

    /// <summary>Reverts one change set, change by change, and only where the note is still exactly as the extractor left it (everything but when it was last used): a note that was added is removed,
    /// one that was updated gets its old text back, one that was removed or dropped is brought back as it was (id, times, pinned flag). A note that has been changed since in any way (edited, edited
    /// back, pinned) is left alone and counted as skipped, a removed note whose words were saved again meanwhile is not brought back a second time, and so is one that cannot come back because the set
    /// is full. When nothing at all could be undone the set stays in the journal (so it can be tried again, or seen), otherwise it leaves it. Refused when the stored notes were written by a newer
    /// version or do not read cleanly.</summary>
    public MemoryUndoResult Undo(string changeSetId)
    {
        lock (Gate)
        {
            List<MemoryChangeSet> journal = LoadJournal();
            MemoryChangeSet target = journal.FirstOrDefault(s => s.Id == changeSetId);
            if (target is null)
            {
                return new MemoryUndoResult(0, 0, false, "");
            }
            Loaded loaded = LoadLocked();
            if (loaded.Status != MemoryLoadStatus.Ok)
            {
                return new MemoryUndoResult(0, 0, true, loaded.Status == MemoryLoadStatus.Newer ? "newer" : "unreadable");
            }
            MemoryNoteSet set = loaded.Set;
            int undone = 0, skipped = 0;
            List<string> touched = [];
            // First what was added or changed (so there is room for what comes back), newest change first.
            foreach (MemoryChange change in target.Changes.Reverse())
            {
                MemoryNote now = set.Find(change.NoteId);
                if (change.Type == MemoryChangeTypes.Added)
                {
                    if (now is not null && SameExceptUse(NoteJson(now), change.After))
                    {
                        set.Remove(now.Id);
                        touched.Add(now.Id);
                        undone++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else if (change.Type == MemoryChangeTypes.Updated)
                {
                    if (now is not null && SameExceptUse(NoteJson(now), change.After) && change.Before?.ToObject<MemoryNote>() is MemoryNote old)
                    {
                        now.Text = old.Text;
                        now.Origin = old.Origin;
                        now.ThreadId = old.ThreadId ?? "";
                        now.Negative = old.Negative;
                        touched.Add(now.Id);
                        undone++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
            foreach (MemoryChange change in target.Changes.Reverse().Where(c => c.Type is MemoryChangeTypes.Removed or MemoryChangeTypes.Evicted))
            {
                MemoryNote back = change.Before?.ToObject<MemoryNote>();
                if (back is null || set.Find(back.Id) is not null || set.Notes.Count >= MemoryNoteSet.MaxNotes || string.IsNullOrWhiteSpace(back.Text)
                    || set.Notes.Any(n => n.Slot.Length == 0 && back.Slot.Length == 0 && n.Kind == back.Kind && n.Negative == back.Negative && MemoryNoteSet.Squash(n.Text).Equals(MemoryNoteSet.Squash(back.Text), StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
                back.Slot ??= "";
                back.ThreadId ??= "";
                if (target.Reason == MemoryChangeReasons.Retention)
                {
                    // Brought back by the user: its unused time starts again, or the next sweep would forget it at once.
                    back.LastUsed = Clock().ToUniversalTime().ToString("o");
                }
                set.Notes.Add(back);
                undone++;
            }
            if (undone > 0)
            {
                Storage.Save(NotesKey, set.Serialize());
                foreach (string id in touched)
                {
                    DeleteVector(id);
                }
                journal.Remove(target);
                SaveJournal(journal);
            }
            return new MemoryUndoResult(undone, skipped, true, "");
        }
    }
}
