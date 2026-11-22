using Newtonsoft.Json;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>The kinds of note. A kind says what a note is about, not how it is written; it groups notes in the prompt.</summary>
public static class MemoryKinds
{
    /// <summary>How the user likes things done (or does not).</summary>
    public const string Preference = "preference";
    /// <summary>A durable fact about the user.</summary>
    public const string Fact = "fact";
    /// <summary>Something the user is working on.</summary>
    public const string Project = "project";
    /// <summary>How the user wants replies written (tone, format, length).</summary>
    public const string Style = "style";

    /// <summary>Every kind, in the order notes are grouped in the prompt.</summary>
    public static readonly string[] All = [Preference, Fact, Project, Style];

    /// <summary>Whether <paramref name="kind"/> is a known kind.</summary>
    public static bool IsValid(string kind) => All.Contains(kind);

    /// <summary>The place of a kind in the prompt's order (unknown kinds last).</summary>
    public static int Rank(string kind)
    {
        int index = Array.IndexOf(All, kind);
        return index < 0 ? All.Length : index;
    }
}

/// <summary>Where a note came from: who said it. Recorded on every note so a note can be traced and judged (a note the user typed outranks one the assistant inferred).</summary>
public static class MemoryOrigins
{
    /// <summary>The user said it in their own message.</summary>
    public const string User = "user";
    /// <summary>It was drawn from something the assistant wrote.</summary>
    public const string Assistant = "assistant";
    /// <summary>It was saved on purpose with the memory tool ("remember this"), by the user's request or the model's own call.</summary>
    public const string Explicit = "explicit";

    /// <summary>Every origin.</summary>
    public static readonly string[] All = [User, Assistant, Explicit];

    /// <summary>Whether <paramref name="origin"/> is a known origin.</summary>
    public static bool IsValid(string origin) => All.Contains(origin);
}

/// <summary>The few things there is exactly one of for a user (their name, pronouns, bio, and current work): a note with a slot is replaced when the same slot is written again, which is
/// how the old profile's fixed fields behaved.</summary>
public static class MemorySlots
{
    /// <summary>The user's preferred name.</summary>
    public const string Name = "name";
    /// <summary>The user's pronouns.</summary>
    public const string Pronouns = "pronouns";
    /// <summary>A short bio.</summary>
    public const string Bio = "bio";
    /// <summary>What the user is working on now.</summary>
    public const string CurrentWork = "currentWork";

    /// <summary>Every slot, in the order they are shown.</summary>
    public static readonly string[] All = [Name, Pronouns, Bio, CurrentWork];

    /// <summary>The label a slot note is shown with in the prompt.</summary>
    public static string Label(string slot) => slot switch
    {
        Name => "Name",
        Pronouns => "Pronouns",
        Bio => "Bio",
        CurrentWork => "Currently working on",
        _ => "Note"
    };
}

/// <summary>One atomic note about a user: a single short statement, with where it came from and when.</summary>
public sealed class MemoryNote
{
    /// <summary>A stable id (a short random hex string), unique within the user's notes.</summary>
    [JsonProperty("id")]
    public string Id = "";

    /// <summary>The statement, one fact or preference in a sentence or two.</summary>
    [JsonProperty("text")]
    public string Text = "";

    /// <summary>What the note is about (<see cref="MemoryKinds"/>).</summary>
    [JsonProperty("kind")]
    public string Kind = MemoryKinds.Fact;

    /// <summary>Who said it (<see cref="MemoryOrigins"/>).</summary>
    [JsonProperty("origin")]
    public string Origin = MemoryOrigins.Explicit;

    /// <summary>The chat it was saved in, or empty (a note migrated from the old profile, or one saved from a call with no chat).</summary>
    [JsonProperty("threadId")]
    public string ThreadId = "";

    /// <summary>When it was saved (UTC, ISO 8601).</summary>
    [JsonProperty("created")]
    public string Created = "";

    /// <summary>When it was last put in a prompt (UTC, ISO 8601), or empty if never. Kept coarse (see <see cref="MemoryNoteSet.UsedRefreshHours"/>) so recall does not write on every turn.</summary>
    [JsonProperty("lastUsed")]
    public string LastUsed = "";

    /// <summary>Whether the note is always in the prompt, whatever the turn is about.</summary>
    [JsonProperty("pinned")]
    public bool Pinned;

    /// <summary>The slot the note fills (<see cref="MemorySlots"/>), or empty for an ordinary note.</summary>
    [JsonProperty("slot", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string Slot = "";

    /// <summary>True for something the user dislikes (a <see cref="MemoryKinds.Preference"/> note that is a "do not"). Shown as a dislike so it is not read the wrong way round.</summary>
    [JsonProperty("negative", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool Negative;

    /// <summary>The time the note was created, or <see cref="DateTime.MinValue"/> when it has none that reads.</summary>
    [JsonIgnore]
    public DateTime CreatedAt => DateTime.TryParse(Created, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t) ? t : DateTime.MinValue;

    /// <summary>The time the note was last used, or null.</summary>
    [JsonIgnore]
    public DateTime? LastUsedAt => DateTime.TryParse(LastUsed, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t) ? t : null;
}
