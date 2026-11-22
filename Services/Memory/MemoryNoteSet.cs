using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>What saving a note did.</summary>
/// <param name="Saved">Whether the set holds the note now (true also for a note that was already there).</param>
/// <param name="Summary">A short sentence for the model to relay to the user.</param>
/// <param name="Note">The note, or null when it was refused.</param>
/// <param name="Added">Whether a new note was added (false for a repeat, a replaced slot or a refusal).</param>
/// <param name="Evicted">The ids of notes dropped to make room (their vectors go with them).</param>
public sealed record MemoryWriteResult(bool Saved, string Summary, MemoryNote Note, bool Added, IReadOnlyList<string> Evicted = null);

/// <summary>How stored notes read.</summary>
public enum MemoryLoadStatus
{
    /// <summary>Nothing is stored.</summary>
    None,
    /// <summary>Read in full.</summary>
    Ok,
    /// <summary>Read, but some notes could not be (a bad field): they are left out of the set, and the stored text must not be overwritten without a backup.</summary>
    Lossy,
    /// <summary>Stored text that does not read as notes at all.</summary>
    Corrupt,
    /// <summary>Written by a newer version of the extension: read as far as possible and never overwritten.</summary>
    Newer
}

/// <summary>A stored value read back: the notes that could be read, how it went, and how many notes were left out.</summary>
public sealed record MemoryParse(MemoryNoteSet Set, MemoryLoadStatus Status, int Skipped);

/// <summary>A user's notes as a plain value: adding with the caps and dedup, replacing a slot, migrating the old profile blob, and the views the rest of the extension reads. Pure (the
/// clock is passed in, nothing is stored here), so it is tested with fixed data; <see cref="MemoryService"/> keeps it.</summary>
public sealed class MemoryNoteSet
{
    /// <summary>The most notes a user can have. It admits everything the old profile could hold (4 fields, 50 preferences, 50 dislikes, 100 facts) with room to spare.</summary>
    public const int MaxNotes = 400;

    /// <summary>The most characters of one note's text (the old profile's per-field limit).</summary>
    public const int MaxNoteChars = 2000;

    /// <summary>The most pinned notes. A pinned note is always in the prompt, so this bounds what pinning can cost.</summary>
    public const int MaxPinned = 25;

    /// <summary>How many hours old a note's last-used time must be before it is refreshed, so recalling does not rewrite the notes on every turn.</summary>
    public const int UsedRefreshHours = 6;

    /// <summary>The on-disk format version this build writes and understands.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The on-disk format version.</summary>
    [JsonProperty("version")]
    public int Version = CurrentVersion;

    /// <summary>The notes, oldest first (the order they were added).</summary>
    [JsonProperty("notes")]
    public List<MemoryNote> Notes = [];

    /// <summary>The set as stored text.</summary>
    public string Serialize() => JsonConvert.SerializeObject(this, Formatting.None);

    /// <summary>A set from stored text, or null when the text is empty or does not read as a set at all.</summary>
    public static MemoryNoteSet Parse(string raw)
    {
        MemoryParse parsed = ParseDetailed(raw);
        return parsed.Status is MemoryLoadStatus.None or MemoryLoadStatus.Corrupt ? null : parsed.Set;
    }

    /// <summary>Reads stored text without ever throwing and without deciding what to do about a problem: a note with a bad field is skipped (and counted) instead of failing the whole value,
    /// text that is not a set is <see cref="MemoryLoadStatus.Corrupt"/>, and a version newer than <see cref="CurrentVersion"/> is <see cref="MemoryLoadStatus.Newer"/>.</summary>
    public static MemoryParse ParseDetailed(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new MemoryParse(new MemoryNoteSet(), MemoryLoadStatus.None, 0);
        }
        try
        {
            JObject root = JObject.Parse(raw);
            // A newer version is recognised before anything about the shape is checked: a newer file may have renamed or moved the notes, and it must still be left alone.
            bool newer = IsNewerVersion(root["version"]);
            if (root["notes"] is not JArray array)
            {
                return new MemoryParse(new MemoryNoteSet(), newer ? MemoryLoadStatus.Newer : MemoryLoadStatus.Corrupt, 0);
            }
            MemoryNoteSet set = new() { Version = root["version"]?.Type == JTokenType.Integer && !newer ? root["version"].Value<int>() : CurrentVersion };
            int skipped = 0;
            foreach (JToken token in array)
            {
                try
                {
                    MemoryNote note = token is JObject o ? o.ToObject<MemoryNote>() : null;
                    if (note is null || string.IsNullOrWhiteSpace(note.Text) || string.IsNullOrEmpty(note.Id) || set.Notes.Any(n => n.Id == note.Id))
                    {
                        skipped++;
                        continue;
                    }
                    note.Kind ??= MemoryKinds.Fact;
                    note.Slot ??= "";
                    note.ThreadId ??= "";
                    note.Created ??= "";
                    note.LastUsed ??= "";
                    note.Origin ??= MemoryOrigins.Explicit;
                    set.Notes.Add(note);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidCastException)
                {
                    skipped++;
                }
            }
            MemoryLoadStatus status = newer ? MemoryLoadStatus.Newer : skipped > 0 ? MemoryLoadStatus.Lossy : MemoryLoadStatus.Ok;
            return new MemoryParse(set, status, skipped);
        }
        catch (JsonException)
        {
            return new MemoryParse(new MemoryNoteSet(), MemoryLoadStatus.Corrupt, 0);
        }
    }

    /// <summary>Whether a stored <c>version</c> is one this build does not know: a number (or a numeric string) above <see cref="CurrentVersion"/>, or anything that is not a number at all
    /// (a format this build cannot have written). An absent or null version is not newer.</summary>
    public static bool IsNewerVersion(JToken version)
    {
        if (version is null || version.Type == JTokenType.Null)
        {
            return false;
        }
        switch (version.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                return version.Value<double>() > CurrentVersion;
            case JTokenType.String:
                return !double.TryParse(version.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) || number > CurrentVersion;
            default:
                return true;
        }
    }

    /// <summary>The first <paramref name="max"/> characters of <paramref name="text"/>, without cutting a surrogate pair in half (a cut one character earlier when it would).</summary>
    public static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text ?? "";
        }
        int end = max;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            end--;
        }
        return text[..end];
    }

    /// <summary>The note with this id, or null.</summary>
    public MemoryNote Find(string id) => string.IsNullOrEmpty(id) ? null : Notes.FirstOrDefault(n => n.Id == id);

    /// <summary>The note filling a slot, or null.</summary>
    public MemoryNote Slot(string slot) => Notes.FirstOrDefault(n => n.Slot == slot);

    /// <summary>The user's preferred name, or empty.</summary>
    public string UserName() => Slot(MemorySlots.Name)?.Text ?? "";

    /// <summary>Removes a note. Returns whether it was there.</summary>
    public bool Remove(string id) => Notes.RemoveAll(n => n.Id == id) > 0;

    /// <summary>Whether two texts say the same thing: equal ignoring case and spacing.</summary>
    private static bool SameText(string a, string b) => string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A text with every run of whitespace as one space, trimmed.</summary>
    public static string Squash(string text) => string.IsNullOrEmpty(text) ? "" : System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>Maps a <c>memory_write</c> category to what it makes: a slot (and its kind), or a kind, and whether it is a dislike. An unknown category is a fact, as it always was.</summary>
    public static (string Kind, string Slot, bool Negative) ParseCategory(string category)
    {
        switch ((category ?? "").Trim().ToLowerInvariant())
        {
            case "preferred_name":
            case "name":
                return (MemoryKinds.Fact, MemorySlots.Name, false);
            case "pronouns":
                return (MemoryKinds.Fact, MemorySlots.Pronouns, false);
            case "bio":
                return (MemoryKinds.Fact, MemorySlots.Bio, false);
            case "current_work":
            case "currentwork":
                return (MemoryKinds.Project, MemorySlots.CurrentWork, false);
            case "preference":
            case "like":
                return (MemoryKinds.Preference, "", false);
            case "dislike":
                return (MemoryKinds.Preference, "", true);
            case "project":
                return (MemoryKinds.Project, "", false);
            case "style":
                return (MemoryKinds.Style, "", false);
            default:
                return (MemoryKinds.Fact, "", false);
        }
    }

    /// <summary>Saves a note from a <c>memory_write</c> category and text: fills its slot (replacing what was there), or adds an ordinary note unless the same one is already saved. At
    /// the cap the oldest unpinned ordinary note makes room; when there is none the note is refused.</summary>
    public MemoryWriteResult Write(string category, string text, string origin, string threadId, DateTime now)
    {
        string body = (text ?? "").Trim();
        if (body.Length == 0)
        {
            return new MemoryWriteResult(false, "No content provided.", null, false);
        }
        body = Truncate(body, MaxNoteChars);
        (string kind, string slot, bool negative) = ParseCategory(category);
        if (!MemoryOrigins.IsValid(origin))
        {
            origin = MemoryOrigins.Explicit;
        }
        string stamp = now.ToUniversalTime().ToString("o");
        List<string> evicted = [];
        if (slot.Length > 0)
        {
            MemoryNote existing = Slot(slot);
            if (existing is not null)
            {
                existing.Text = body;
                existing.Origin = origin;
                existing.ThreadId = threadId ?? "";
                return new MemoryWriteResult(true, SlotSummary(slot, body), existing, false);
            }
            if (!MakeRoom(evicted))
            {
                return new MemoryWriteResult(false, "Memory is full of pinned notes; nothing was saved.", null, false);
            }
            MemoryNote filled = New(kind, body, origin, threadId, stamp);
            filled.Slot = slot;
            filled.Pinned = true;
            Notes.Add(filled);
            return new MemoryWriteResult(true, SlotSummary(slot, body), filled, true, evicted);
        }
        MemoryNote duplicate = Notes.FirstOrDefault(n => n.Slot.Length == 0 && n.Kind == kind && n.Negative == negative && SameText(n.Text, body));
        if (duplicate is not null)
        {
            return new MemoryWriteResult(true, $"Already remembered: \"{body}\".", duplicate, false);
        }
        if (!MakeRoom(evicted))
        {
            return new MemoryWriteResult(false, "Memory is full of pinned notes; nothing was saved.", null, false);
        }
        MemoryNote note = New(kind, body, origin, threadId, stamp);
        note.Negative = negative;
        Notes.Add(note);
        string label = negative ? "dislike" : kind;
        return new MemoryWriteResult(true, $"Saved {label}: \"{body}\".", note, true, evicted);
    }

    /// <summary>The sentence for a slot that was filled.</summary>
    private static string SlotSummary(string slot, string body) => slot switch
    {
        MemorySlots.Name => $"Updated preferred name to \"{body}\".",
        MemorySlots.Pronouns => $"Updated pronouns to \"{body}\".",
        MemorySlots.Bio => "Updated user bio.",
        _ => $"Updated current work: \"{body}\"."
    };

    /// <summary>A new note with a fresh id.</summary>
    private static MemoryNote New(string kind, string text, string origin, string threadId, string stamp) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Text = text,
        Kind = kind,
        Origin = origin,
        ThreadId = threadId ?? "",
        Created = stamp
    };

    /// <summary>Makes room for one more note when the set is full, by dropping the least recently used unpinned ordinary note. Returns false when there is nothing that may be dropped.</summary>
    private bool MakeRoom(List<string> evicted)
    {
        if (Notes.Count < MaxNotes)
        {
            return true;
        }
        MemoryNote victim = null;
        DateTime victimTime = DateTime.MaxValue;
        foreach (MemoryNote candidate in Notes)
        {
            if (candidate.Pinned || candidate.Slot.Length > 0)
            {
                continue;
            }
            DateTime time = candidate.LastUsedAt ?? candidate.CreatedAt;
            if (victim is null || time < victimTime)
            {
                victim = candidate;
                victimTime = time;
            }
        }
        if (victim is null)
        {
            return false;
        }
        Notes.Remove(victim);
        evicted.Add(victim.Id);
        return true;
    }

    /// <summary>Marks notes as used at <paramref name="now"/>, but only those not marked in the last <see cref="UsedRefreshHours"/> hours. Returns whether anything changed (so the caller
    /// writes only then).</summary>
    public bool MarkUsed(IEnumerable<string> ids, DateTime now)
    {
        bool changed = false;
        HashSet<string> wanted = [.. ids];
        foreach (MemoryNote note in Notes)
        {
            if (!wanted.Contains(note.Id))
            {
                continue;
            }
            DateTime? last = note.LastUsedAt;
            if (last is null || now.ToUniversalTime() - last.Value.ToUniversalTime() >= TimeSpan.FromHours(UsedRefreshHours))
            {
                note.LastUsed = now.ToUniversalTime().ToString("o");
                changed = true;
            }
        }
        return changed;
    }

    // ---- The old profile ----

    /// <summary>Notes from the old profile blob (<c>{ preferredName, pronouns, bio, currentWork, preferences[], dislikes[], facts[{content, created}], updated }</c>), without losing
    /// anything it held: the four fields become slot notes (pinned), the lists become ordinary notes, a dislike stays a dislike. A fact keeps its created time; the rest take the time
    /// the profile was last updated (or <paramref name="now"/>). Entries beyond the caps are not dropped silently by anything here: the old caps are lower than the new ones.</summary>
    public static MemoryNoteSet FromLegacyProfile(JObject profile, DateTime now)
    {
        MemoryNoteSet set = new();
        if (profile is null)
        {
            return set;
        }
        string updated = profile["updated"]?.ToString();
        string baseStamp = DateTime.TryParse(updated, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed.ToUniversalTime().ToString("o") : now.ToUniversalTime().ToString("o");
        void Add(string category, string text, string created)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            MemoryWriteResult result = set.Write(category, text, MemoryOrigins.Explicit, "", now);
            if (!result.Added || result.Note is null)
            {
                return;
            }
            result.Note.Created = !string.IsNullOrWhiteSpace(created) && DateTime.TryParse(created, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when)
                ? when.ToUniversalTime().ToString("o")
                : baseStamp;
        }
        Add("name", profile["preferredName"]?.ToString(), null);
        Add("pronouns", profile["pronouns"]?.ToString(), null);
        Add("bio", profile["bio"]?.ToString(), null);
        Add("current_work", profile["currentWork"]?.ToString(), null);
        if (profile["preferences"] is JArray preferences)
        {
            foreach (JToken p in preferences)
            {
                Add("preference", p.ToString(), null);
            }
        }
        if (profile["dislikes"] is JArray dislikes)
        {
            foreach (JToken d in dislikes)
            {
                Add("dislike", d.ToString(), null);
            }
        }
        if (profile["facts"] is JArray facts)
        {
            foreach (JToken f in facts)
            {
                string text = f is JObject fo ? fo["content"]?.ToString() : f.ToString();
                string created = f is JObject fo2 ? fo2["created"]?.ToString() : null;
                Add("fact", text, created);
            }
        }
        return set;
    }

    /// <summary>The notes as the old profile shape, which the welcome screen reads: <c>{ preferredName, pronouns, bio, currentWork, preferences[], dislikes[], facts[{content, created}] }</c>.
    /// Notes of kinds the old profile had no list for (project, style) show among the facts.</summary>
    public JObject ToProfileView()
    {
        JArray preferences = [];
        JArray dislikes = [];
        JArray facts = [];
        foreach (MemoryNote note in Notes.Where(n => n.Slot.Length == 0))
        {
            if (note.Kind == MemoryKinds.Preference)
            {
                (note.Negative ? dislikes : preferences).Add(note.Text);
            }
            else
            {
                facts.Add(new JObject { ["content"] = note.Text, ["created"] = note.Created });
            }
        }
        return new JObject
        {
            ["preferredName"] = Slot(MemorySlots.Name)?.Text ?? "",
            ["pronouns"] = Slot(MemorySlots.Pronouns)?.Text ?? "",
            ["bio"] = Slot(MemorySlots.Bio)?.Text ?? "",
            ["currentWork"] = Slot(MemorySlots.CurrentWork)?.Text ?? "",
            ["preferences"] = preferences,
            ["dislikes"] = dislikes,
            ["facts"] = facts
        };
    }
}
