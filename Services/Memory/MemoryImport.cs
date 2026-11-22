using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>What importing a file of notes would do (or did): the notes it adds, and why each of the others is left out. Nothing in it is trusted from a client: it is worked out from the file and the
/// notes as they are, every time.</summary>
public sealed class MemoryImportPlan
{
    /// <summary>The notes that would be added (already cleaned, with the ids they will have).</summary>
    public List<MemoryNote> Add = [];

    /// <summary>Entries already in memory (the same text of the same kind), or repeated in the file.</summary>
    public int Duplicates;

    /// <summary>Slot notes (name, pronouns, bio, current work) whose slot is already filled with other text: yours is kept, never replaced.</summary>
    public int Conflicts;

    /// <summary>Entries that mention a secret, or something on the user's never-save list.</summary>
    public int Blocked;

    /// <summary>Entries that are not a note with text.</summary>
    public int Invalid;

    /// <summary>Entries left out because memory is full (nothing existing is dropped to make room).</summary>
    public int NoRoom;

    /// <summary>Entries past the first <see cref="MemoryNoteSet.MaxNotes"/> in the file.</summary>
    public int Ignored;

    /// <summary>Notes whose pinned flag was not kept because <see cref="MemoryNoteSet.MaxPinned"/> would have been passed.</summary>
    public int Unpinned;

    /// <summary>Why the file could not be read at all, or empty.</summary>
    public string Error = "";

    /// <summary>The preview for a client: the counts and the first <paramref name="maxShown"/> notes that would be added, each text cut to <paramref name="maxChars"/>.</summary>
    public JObject ToClientJson(int maxShown = 100, int maxChars = 200) => new()
    {
        ["addCount"] = Add.Count,
        ["add"] = new JArray(Add.Take(maxShown).Select(n => new JObject
        {
            ["text"] = MemoryNoteSet.Truncate(n.Text, maxChars),
            ["kind"] = n.Negative ? "dislike" : n.Kind,
            ["slot"] = n.Slot,
            ["pinned"] = n.Pinned
        })),
        ["duplicates"] = Duplicates,
        ["conflicts"] = Conflicts,
        ["blocked"] = Blocked,
        ["invalid"] = Invalid,
        ["noRoom"] = NoRoom,
        ["ignored"] = Ignored,
        ["unpinned"] = Unpinned
    };
}

/// <summary>Reading a file of notes safely. Pure (the notes and the clock come in), so it is tested with fixed data; <see cref="MemoryStore"/> applies a plan under the user's lock.
/// <para>The file is untrusted text: its size and the number of entries read are capped, every text goes through <see cref="MemoryRecall.Clean"/> (so it carries no control characters or angle
/// brackets, like a note in a prompt) and the note cap, ids are made here (never taken from the file), and the origin is always <c>user</c> since the user is the one importing. It never
/// overwrites: a note already there (same kind and text) is a duplicate, a filled slot is kept, and a full memory stops the import rather than dropping an existing note.</para></summary>
public static class MemoryImport
{
    /// <summary>The most characters of a file read (the largest memory, 400 notes of 2000 characters, is about 1 MB as JSON).</summary>
    public const int MaxChars = 1_500_000;

    /// <summary>The format name written into an export.</summary>
    public const string Format = "llma-memory";

    /// <summary>The notes as an export: every note's text in full, with its kind, origin, times, pinned flag and slot (never its chat id, which means nothing elsewhere).</summary>
    public static JObject Export(MemoryNoteSet set, DateTime now) => new()
    {
        ["format"] = Format,
        ["version"] = 1,
        ["exported"] = now.ToUniversalTime().ToString("o"),
        ["notes"] = new JArray(MemoryRecall.StableOrder(set.Notes).Select(n =>
        {
            JObject o = new()
            {
                ["text"] = n.Text,
                ["kind"] = n.Kind,
                ["origin"] = n.Origin,
                ["created"] = n.Created,
                ["lastUsed"] = n.LastUsed,
                ["pinned"] = n.Pinned
            };
            if (n.Slot.Length > 0)
            {
                o["slot"] = n.Slot;
            }
            if (n.Negative)
            {
                o["negative"] = true;
            }
            return o;
        }))
    };

    /// <summary>What importing <paramref name="data"/> into <paramref name="existing"/> would do. Never throws.</summary>
    public static MemoryImportPlan Plan(MemoryNoteSet existing, string data, MemoryNeverSave never, DateTime now)
    {
        MemoryImportPlan plan = new();
        if (data is { Length: > MaxChars })
        {
            plan.Error = "too-large";
            return plan;
        }
        if (string.IsNullOrWhiteSpace(data))
        {
            plan.Error = "empty";
            return plan;
        }
        JToken root;
        try
        {
            root = MemoryNoteSet.ParseJsonToken(data);
        }
        catch (JsonException)
        {
            plan.Error = "not-json";
            return plan;
        }
        JArray entries = root as JArray ?? (root as JObject)?["notes"] as JArray;
        if (entries is null)
        {
            plan.Error = "no-notes";
            return plan;
        }
        plan.Ignored = Math.Max(0, entries.Count - MemoryNoteSet.MaxNotes);
        string stamp = now.ToUniversalTime().ToString("o");
        HashSet<string> seen = [.. existing.Notes.Where(n => n.Slot.Length == 0).Select(n => Key(n.Kind, n.Negative, n.Text))];
        HashSet<string> slotsTaken = [.. existing.Notes.Where(n => n.Slot.Length > 0).Select(n => n.Slot)];
        int pinned = existing.Notes.Count(n => n.Pinned);
        foreach (JToken entry in entries.Take(MemoryNoteSet.MaxNotes))
        {
            if (entry is not JObject o || o["text"]?.Type != JTokenType.String)
            {
                plan.Invalid++;
                continue;
            }
            string text = MemoryNoteSet.Truncate(MemoryRecall.Clean((string)o["text"]), MemoryNoteSet.MaxNoteChars).Trim();
            if (text.Length == 0)
            {
                plan.Invalid++;
                continue;
            }
            if (never?.Matches(text) == true)
            {
                plan.Blocked++;
                continue;
            }
            string kindText = o["kind"]?.Type == JTokenType.String ? ((string)o["kind"]).Trim().ToLowerInvariant() : "";
            bool negative = kindText == "dislike" || (kindText == MemoryKinds.Preference && o["negative"]?.Type == JTokenType.Boolean && (bool)o["negative"]);
            string kind = kindText == "dislike" ? MemoryKinds.Preference : MemoryKinds.IsValid(kindText) ? kindText : MemoryKinds.Fact;
            string slot = o["slot"]?.Type == JTokenType.String && MemorySlots.All.Contains((string)o["slot"]) ? (string)o["slot"] : "";
            if (slot.Length > 0)
            {
                kind = slot == MemorySlots.CurrentWork ? MemoryKinds.Project : MemoryKinds.Fact;
                negative = false;
                if (slotsTaken.Contains(slot))
                {
                    MemoryNote current = existing.Slot(slot);
                    if (current is not null && Key(kind, false, current.Text) == Key(kind, false, text))
                    {
                        plan.Duplicates++;
                    }
                    else
                    {
                        plan.Conflicts++;
                    }
                    continue;
                }
            }
            else if (seen.Contains(Key(kind, negative, text)))
            {
                plan.Duplicates++;
                continue;
            }
            if (existing.Notes.Count + plan.Add.Count >= MemoryNoteSet.MaxNotes)
            {
                plan.NoRoom++;
                continue;
            }
            bool wantsPin = slot.Length > 0 || (o["pinned"]?.Type == JTokenType.Boolean && (bool)o["pinned"]);
            bool pin = wantsPin && pinned < MemoryNoteSet.MaxPinned;
            if (wantsPin && !pin)
            {
                plan.Unpinned++;
            }
            if (pin)
            {
                pinned++;
            }
            plan.Add.Add(new MemoryNote
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Text = text,
                Kind = kind,
                Origin = MemoryOrigins.User,
                ThreadId = "",
                Created = ReadTime(o["created"], now) ?? stamp,
                // The import counts as use, so the notes are not forgotten by the retention setting the moment they arrive.
                LastUsed = stamp,
                Pinned = pin,
                Slot = slot,
                Negative = negative
            });
            if (slot.Length > 0)
            {
                slotsTaken.Add(slot);
            }
            else
            {
                seen.Add(Key(kind, negative, text));
            }
        }
        return plan;
    }

    /// <summary>What makes two notes the same: kind, dislike-ness, and the text cleaned and squashed, ignoring case.</summary>
    private static string Key(string kind, bool negative, string text)
        => $"{kind}|{negative}|{MemoryNoteSet.Squash(MemoryRecall.Clean(text)).ToLowerInvariant()}";

    /// <summary>A time from a file as stored text (UTC, ISO 8601), or null when it does not read or is in the future.</summary>
    private static string ReadTime(JToken token, DateTime now)
    {
        if (token?.Type != JTokenType.String || !DateTime.TryParse((string)token, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime when))
        {
            return null;
        }
        return when.ToUniversalTime() > now.ToUniversalTime() ? null : when.ToUniversalTime().ToString("o");
    }
}
