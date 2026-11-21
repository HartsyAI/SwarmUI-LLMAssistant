using Hartsy.Extensions.LLMAssistant.Services.Memory;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The note store's rules without storage: writing with the caps and dedup, slots that replace, the old profile turned into notes, and the stored form.</summary>
public class MemoryNoteSetTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MemoryNote Add(MemoryNoteSet set, string category, string text, DateTime? at = null)
    {
        MemoryWriteResult result = set.Write(category, text, MemoryOrigins.Explicit, "thread-1", at ?? T0);
        Assert.True(result.Saved, result.Summary);
        return result.Note;
    }

    [Fact]
    public void A_note_records_its_kind_origin_chat_and_time()
    {
        MemoryNoteSet set = new();
        MemoryWriteResult result = set.Write("preference", "  Prefers Python for scripting  ", MemoryOrigins.User, "chat-9", T0);
        Assert.True(result.Added);
        MemoryNote note = Assert.Single(set.Notes);
        Assert.Equal("Prefers Python for scripting", note.Text);
        Assert.Equal(MemoryKinds.Preference, note.Kind);
        Assert.Equal(MemoryOrigins.User, note.Origin);
        Assert.Equal("chat-9", note.ThreadId);
        Assert.Equal(T0, note.CreatedAt);
        Assert.Equal("", note.LastUsed);
        Assert.False(note.Pinned);
        Assert.Equal(12, note.Id.Length);
        Assert.Contains("Saved preference", result.Summary);
    }

    [Theory]
    [InlineData("preferred_name", "fact", "name", false)]
    [InlineData("NAME", "fact", "name", false)]
    [InlineData("pronouns", "fact", "pronouns", false)]
    [InlineData("bio", "fact", "bio", false)]
    [InlineData("current_work", "project", "currentWork", false)]
    [InlineData("currentwork", "project", "currentWork", false)]
    [InlineData("preference", "preference", "", false)]
    [InlineData("like", "preference", "", false)]
    [InlineData("dislike", "preference", "", true)]
    [InlineData("project", "project", "", false)]
    [InlineData("style", "style", "", false)]
    [InlineData("fact", "fact", "", false)]
    [InlineData("anything else", "fact", "", false)]
    public void Every_memory_write_category_maps_to_a_kind_and_slot(string category, string kind, string slot, bool negative)
    {
        Assert.Equal((kind, slot, negative), MemoryNoteSet.ParseCategory(category));
    }

    [Fact]
    public void A_slot_note_is_pinned_and_writing_the_slot_again_replaces_it()
    {
        MemoryNoteSet set = new();
        MemoryNote first = Add(set, "preferred_name", "Kaleb");
        Assert.True(first.Pinned);
        Assert.Equal(MemorySlots.Name, first.Slot);
        MemoryWriteResult again = set.Write("name", "Kal", MemoryOrigins.Explicit, "chat-2", T0.AddDays(1));
        Assert.False(again.Added);
        Assert.Single(set.Notes);
        Assert.Equal("Kal", set.UserName());
        Assert.Equal(first.Id, again.Note.Id);
        Assert.Equal("chat-2", again.Note.ThreadId);
        Assert.Contains("preferred name", again.Summary);
    }

    [Fact]
    public void The_same_note_is_not_saved_twice_ignoring_case_and_spacing()
    {
        MemoryNoteSet set = new();
        Add(set, "preference", "Likes   dark mode");
        MemoryWriteResult repeat = set.Write("preference", "likes dark MODE", MemoryOrigins.Explicit, "", T0);
        Assert.True(repeat.Saved);
        Assert.False(repeat.Added);
        Assert.StartsWith("Already remembered", repeat.Summary);
        Assert.Single(set.Notes);
        // The same words as a different kind, or as a dislike, are a different note.
        Add(set, "fact", "Likes dark mode");
        Add(set, "dislike", "Likes dark mode");
        Assert.Equal(3, set.Notes.Count);
    }

    [Fact]
    public void An_empty_note_is_refused_and_a_long_one_is_cut()
    {
        MemoryNoteSet set = new();
        Assert.False(set.Write("fact", "   ", MemoryOrigins.Explicit, "", T0).Saved);
        Assert.Empty(set.Notes);
        MemoryNote note = Add(set, "fact", new string('x', MemoryNoteSet.MaxNoteChars + 500));
        Assert.Equal(MemoryNoteSet.MaxNoteChars, note.Text.Length);
    }

    [Fact]
    public void A_full_set_drops_the_least_recently_used_ordinary_note_to_make_room_and_never_a_pinned_one()
    {
        MemoryNoteSet set = new();
        Add(set, "name", "Kaleb", T0);
        for (int i = 0; i < MemoryNoteSet.MaxNotes - 1; i++)
        {
            Add(set, "fact", $"fact number {i}", T0.AddMinutes(i));
        }
        Assert.Equal(MemoryNoteSet.MaxNotes, set.Notes.Count);
        // Using note 0 makes it the most recently used, so note 1 is the one that goes.
        set.Notes.First(n => n.Text == "fact number 0").LastUsed = T0.AddDays(30).ToString("o");
        Add(set, "fact", "one more", T0.AddDays(31));
        Assert.Equal(MemoryNoteSet.MaxNotes, set.Notes.Count);
        Assert.DoesNotContain(set.Notes, n => n.Text == "fact number 1");
        Assert.Contains(set.Notes, n => n.Text == "fact number 0");
        Assert.Equal("Kaleb", set.UserName());
    }

    [Fact]
    public void A_set_full_of_pinned_notes_refuses_a_new_one_instead_of_dropping_a_pinned_one()
    {
        MemoryNoteSet set = new();
        for (int i = 0; i < MemoryNoteSet.MaxNotes; i++)
        {
            set.Notes.Add(new MemoryNote { Id = $"id{i:D4}", Text = $"pinned {i}", Kind = MemoryKinds.Fact, Created = T0.ToString("o"), Pinned = true });
        }
        MemoryWriteResult result = set.Write("fact", "no room", MemoryOrigins.Explicit, "", T0);
        Assert.False(result.Saved);
        Assert.Contains("full", result.Summary);
        Assert.Equal(MemoryNoteSet.MaxNotes, set.Notes.Count);
    }

    [Fact]
    public void Marking_notes_used_is_throttled_so_recall_does_not_write_every_turn()
    {
        MemoryNoteSet set = new();
        MemoryNote a = Add(set, "fact", "a");
        MemoryNote b = Add(set, "fact", "b");
        Assert.True(set.MarkUsed([a.Id], T0));
        Assert.Equal(T0, a.LastUsedAt);
        Assert.Equal("", b.LastUsed);
        Assert.False(set.MarkUsed([a.Id], T0.AddHours(MemoryNoteSet.UsedRefreshHours - 1)));
        Assert.True(set.MarkUsed([a.Id, b.Id], T0.AddHours(MemoryNoteSet.UsedRefreshHours + 1)));
        Assert.Equal(T0.AddHours(MemoryNoteSet.UsedRefreshHours + 1), b.LastUsedAt);
    }

    [Fact]
    public void Notes_survive_being_stored_and_read_back()
    {
        MemoryNoteSet set = new();
        Add(set, "name", "Kaleb");
        Add(set, "dislike", "Long preambles");
        set.MarkUsed(set.Notes.Select(n => n.Id), T0);
        MemoryNoteSet back = MemoryNoteSet.Parse(set.Serialize());
        Assert.NotNull(back);
        Assert.Equal(set.Notes.Count, back.Notes.Count);
        Assert.Equal(set.Notes[1].Id, back.Notes[1].Id);
        Assert.True(back.Notes[1].Negative);
        Assert.Equal(MemorySlots.Name, back.Notes[0].Slot);
        Assert.True(back.Notes[0].Pinned);
        Assert.Equal(T0, back.Notes[0].LastUsedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":1}")]
    public void Text_that_is_not_a_set_reads_as_no_set(string raw)
    {
        Assert.Null(MemoryNoteSet.Parse(raw));
    }

    [Fact]
    public void An_empty_stored_set_reads_as_a_set_so_clearing_memory_is_not_mistaken_for_never_having_had_any()
    {
        MemoryNoteSet empty = MemoryNoteSet.Parse(new MemoryNoteSet().Serialize());
        Assert.NotNull(empty);
        Assert.Empty(empty.Notes);
    }

    // ---- The old profile ----

    private static JObject LegacyProfile()
    {
        JArray prefs = [];
        JArray dislikes = [];
        JArray facts = [];
        for (int i = 0; i < 50; i++)
        {
            prefs.Add($"preference {i}");
            dislikes.Add($"dislike {i}");
        }
        for (int i = 0; i < 100; i++)
        {
            facts.Add(new JObject { ["content"] = $"fact {i}", ["created"] = T0.AddHours(i).ToString("o") });
        }
        return new JObject
        {
            ["preferredName"] = "Kaleb",
            ["pronouns"] = "he/him",
            ["bio"] = new string('b', 2000),
            ["currentWork"] = "A SwarmUI extension",
            ["preferences"] = prefs,
            ["dislikes"] = dislikes,
            ["facts"] = facts,
            ["updated"] = T0.AddDays(-3).ToString("o")
        };
    }

    [Fact]
    public void The_old_profile_becomes_notes_without_losing_anything_even_when_it_is_full()
    {
        MemoryNoteSet set = MemoryNoteSet.FromLegacyProfile(LegacyProfile(), T0);
        Assert.Equal(4 + 50 + 50 + 100, set.Notes.Count);
        Assert.True(set.Notes.Count <= MemoryNoteSet.MaxNotes);
        Assert.Equal("Kaleb", set.UserName());
        Assert.Equal("he/him", set.Slot(MemorySlots.Pronouns).Text);
        Assert.Equal(2000, set.Slot(MemorySlots.Bio).Text.Length);
        Assert.Equal("A SwarmUI extension", set.Slot(MemorySlots.CurrentWork).Text);
        Assert.Equal(MemoryKinds.Project, set.Slot(MemorySlots.CurrentWork).Kind);
        Assert.All(set.Notes.Where(n => n.Slot.Length > 0), n => Assert.True(n.Pinned));
        Assert.Equal(50, set.Notes.Count(n => n.Kind == MemoryKinds.Preference && !n.Negative));
        Assert.Equal(50, set.Notes.Count(n => n.Negative));
        Assert.Equal(100, set.Notes.Count(n => n.Kind == MemoryKinds.Fact && n.Slot.Length == 0));
        Assert.All(set.Notes, n => Assert.Equal(MemoryOrigins.Explicit, n.Origin));
    }

    [Fact]
    public void A_migrated_fact_keeps_its_created_time_and_the_rest_take_the_profiles_updated_time()
    {
        MemoryNoteSet set = MemoryNoteSet.FromLegacyProfile(LegacyProfile(), T0);
        Assert.Equal(T0.AddHours(7), set.Notes.First(n => n.Text == "fact 7").CreatedAt);
        Assert.Equal(T0.AddDays(-3), set.Notes.First(n => n.Text == "preference 3").CreatedAt);
        Assert.Equal(T0.AddDays(-3), set.Slot(MemorySlots.Name).CreatedAt);
    }

    [Fact]
    public void Migrating_and_viewing_gives_the_profile_back_in_its_old_shape()
    {
        JObject original = LegacyProfile();
        JObject view = MemoryNoteSet.FromLegacyProfile(original, T0).ToProfileView();
        Assert.Equal("Kaleb", view["preferredName"]?.ToString());
        Assert.Equal("he/him", view["pronouns"]?.ToString());
        Assert.Equal(original["bio"]?.ToString(), view["bio"]?.ToString());
        Assert.Equal("A SwarmUI extension", view["currentWork"]?.ToString());
        Assert.Equal(original["preferences"]?.ToString(), view["preferences"]?.ToString());
        Assert.Equal(original["dislikes"]?.ToString(), view["dislikes"]?.ToString());
        Assert.Equal(100, ((JArray)view["facts"]).Count);
        Assert.Equal("fact 0", view["facts"]?[0]?["content"]?.ToString());
    }

    [Fact]
    public void An_old_profile_with_plain_string_facts_and_blank_fields_migrates_too()
    {
        JObject profile = JObject.Parse("""{"preferredName":"","facts":["likes tea", {"content":"owns a cat"}, " "],"preferences":["a","A"]}""");
        MemoryNoteSet set = MemoryNoteSet.FromLegacyProfile(profile, T0);
        Assert.Equal(["a", "likes tea", "owns a cat"], set.Notes.Select(n => n.Text).Order().ToArray());
        Assert.Null(set.Slot(MemorySlots.Name));
        Assert.Empty(MemoryNoteSet.FromLegacyProfile(null, T0).Notes);
    }

    [Fact]
    public void Project_and_style_notes_show_among_the_facts_of_the_old_profile_shape()
    {
        MemoryNoteSet set = new();
        Add(set, "project", "Building a harness");
        Add(set, "style", "Short answers");
        JArray facts = (JArray)set.ToProfileView()["facts"];
        Assert.Equal(["Building a harness", "Short answers"], facts.Select(f => f["content"]?.ToString()).ToArray());
    }
}
