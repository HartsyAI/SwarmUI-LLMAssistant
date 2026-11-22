using System.Collections.Concurrent;
using Hartsy.Extensions.LLMAssistant.Services.Memory;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>What the user does to their own notes in Settings (edit, pin, delete, export, import), retention and its undo, the pause and admin controls, and that one user's notes are never
/// touched by another's.</summary>
public class MemoryControlTests
{
    private sealed class Storage : IMemoryStorage
    {
        public readonly ConcurrentDictionary<string, string> Values = new();
        public string Get(string key) => Values.TryGetValue(key, out string v) ? v : null;
        public void Save(string key, string value) => Values[key] = value;
        public void Delete(string key) => Values.TryRemove(key, out _);
    }

    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MemoryStore StoreOf(Storage storage, Func<DateTime> clock = null) => new(storage) { Clock = clock ?? (() => T0) };

    private static MemoryNote Add(MemoryStore store, string category, string text, string origin = MemoryOrigins.Assistant)
    {
        MemoryWriteResult result = store.Write(category, text, origin, "thread1");
        Assert.True(result.Saved);
        return result.Note;
    }

    private static readonly MemoryNeverSave NoNever = new([]);

    // ---- Edit and pin ----

    [Fact]
    public void Editing_the_text_makes_the_note_the_users_own_and_drops_its_vector()
    {
        Storage storage = new();
        MemoryStore store = StoreOf(storage);
        MemoryNote note = Add(store, "fact", "Lives in Paris");
        Assert.True(store.SaveVector(note.Id, "Lives in Paris", "k", [1f, 0f]));
        Assert.NotNull(storage.Get(MemoryStore.VectorPrefix + note.Id));

        MemoryManageResult result = store.Update(note.Id, "  Lives in Lyon  ", null, null);

        Assert.True(result.Ok);
        MemoryNote after = store.Load().Find(note.Id);
        Assert.Equal("Lives in Lyon", after.Text);
        Assert.Equal(MemoryOrigins.User, after.Origin);
        Assert.Null(storage.Get(MemoryStore.VectorPrefix + note.Id));
    }

    [Fact]
    public void A_long_note_is_kept_in_full_by_Get_and_cut_to_the_cap_by_an_edit()
    {
        MemoryStore store = StoreOf(new Storage());
        MemoryNote note = Add(store, "fact", "short");
        store.Update(note.Id, new string('x', MemoryNoteSet.MaxNoteChars + 500), null, null);
        Assert.Equal(MemoryNoteSet.MaxNoteChars, store.Get(note.Id).Text.Length);
        Assert.Null(store.Get("nothere"));
    }

    [Fact]
    public void Kind_changes_use_the_known_kinds_and_dislike_is_a_negative_preference()
    {
        MemoryStore store = StoreOf(new Storage());
        MemoryNote note = Add(store, "fact", "Spicy food");
        Assert.True(store.Update(note.Id, null, "dislike", null).Ok);
        MemoryNote after = store.Load().Find(note.Id);
        Assert.Equal(MemoryKinds.Preference, after.Kind);
        Assert.True(after.Negative);

        Assert.True(store.Update(note.Id, null, "style", null).Ok);
        after = store.Load().Find(note.Id);
        Assert.Equal(MemoryKinds.Style, after.Kind);
        Assert.False(after.Negative);

        Assert.Equal("kind", store.Update(note.Id, null, "banana", null).Error);
        Assert.Equal("empty", store.Update(note.Id, "   ", null, null).Error);
        Assert.Equal("notfound", store.Update("nothere", "x", null, null).Error);
    }

    [Fact]
    public void An_edit_that_makes_a_duplicate_is_refused()
    {
        MemoryStore store = StoreOf(new Storage());
        Add(store, "fact", "Likes tea");
        MemoryNote other = Add(store, "fact", "Likes coffee");
        Assert.Equal("duplicate", store.Update(other.Id, "likes   TEA", null, null).Error);
        // The same words as another kind are not a duplicate.
        Assert.True(store.Update(other.Id, "Likes tea", "preference", null).Ok);
    }

    [Fact]
    public void A_slot_note_keeps_its_kind_and_its_pin_but_its_text_can_change()
    {
        MemoryStore store = StoreOf(new Storage());
        MemoryNote name = Add(store, "name", "Sam");
        Assert.Equal("slot", store.Update(name.Id, null, "style", null).Error);
        Assert.Equal("slot", store.Update(name.Id, null, null, false).Error);
        Assert.True(store.Update(name.Id, "Samantha", null, null).Ok);
        Assert.Equal("Samantha", store.Load().Slot(MemorySlots.Name).Text);
        Assert.True(store.Load().Slot(MemorySlots.Name).Pinned);
    }

    [Fact]
    public void Pinning_stops_at_the_limit_and_unpinning_always_works()
    {
        MemoryStore store = StoreOf(new Storage());
        List<MemoryNote> notes = [.. Enumerable.Range(0, MemoryNoteSet.MaxPinned + 1).Select(i => Add(store, "fact", $"Fact number {i}"))];
        foreach (MemoryNote n in notes.Take(MemoryNoteSet.MaxPinned))
        {
            Assert.True(store.Update(n.Id, null, null, true).Ok);
        }
        Assert.Equal("pinlimit", store.Update(notes[^1].Id, null, null, true).Error);
        Assert.True(store.Update(notes[0].Id, null, null, false).Ok);
        Assert.True(store.Update(notes[^1].Id, null, null, true).Ok);
    }

    [Fact]
    public void Nothing_is_written_when_an_edit_changes_nothing()
    {
        Storage storage = new();
        MemoryStore store = StoreOf(storage);
        MemoryNote note = Add(store, "fact", "Same");
        string before = storage.Get(MemoryStore.NotesKey);
        MemoryManageResult result = store.Update(note.Id, "Same", "fact", false);
        Assert.True(result.Ok);
        Assert.Equal(0, result.Count);
        Assert.Equal(before, storage.Get(MemoryStore.NotesKey));
    }

    [Fact]
    public void Manual_changes_are_refused_over_notes_written_by_a_newer_version_and_change_nothing()
    {
        Storage storage = new();
        string newer = "{\"version\":99,\"notes\":[{\"id\":\"a\",\"text\":\"Hello\",\"kind\":\"fact\"}]}";
        storage.Values[MemoryStore.NotesKey] = newer;
        MemoryStore store = StoreOf(storage);
        Assert.Equal("newer", store.Update("a", "x", null, null).Error);
        Assert.Equal("newer", store.Delete(["a"]).Error);
        store.Import("{\"notes\":[{\"text\":\"x\"}]}", NoNever, out string refused);
        Assert.Equal("newer", refused);
        Assert.Equal(newer, storage.Get(MemoryStore.NotesKey));
    }

    [Fact]
    public void An_unreadable_value_is_backed_up_before_a_manual_change_replaces_it()
    {
        Storage storage = new();
        storage.Values[MemoryStore.NotesKey] = "{\"version\":1,\"notes\":[{\"id\":\"ok\",\"text\":\"Fine\",\"kind\":\"fact\"},{\"id\":\"bad\",\"text\":\"Bad\",\"pinned\":\"maybe\"}]}";
        MemoryStore store = StoreOf(storage);
        Assert.Equal(MemoryLoadStatus.Lossy, store.Status());
        Assert.True(store.Update("ok", "Still fine", null, null).Ok);
        Assert.Contains(storage.Values.Keys, k => k.StartsWith(MemoryStore.BackupPrefix));
    }

    // ---- Delete ----

    [Fact]
    public void Delete_removes_the_named_notes_and_their_vectors_and_ignores_unknown_ids()
    {
        Storage storage = new();
        MemoryStore store = StoreOf(storage);
        MemoryNote a = Add(store, "fact", "Alpha fact");
        MemoryNote b = Add(store, "fact", "Beta fact");
        MemoryNote c = Add(store, "fact", "Gamma fact");
        store.SaveVector(a.Id, "Alpha fact", "k", [1f]);
        MemoryManageResult result = store.Delete([a.Id, c.Id, "unknown", "", a.Id]);
        Assert.True(result.Ok);
        Assert.Equal(2, result.Count);
        Assert.Equal([b.Id], store.Load().Notes.Select(n => n.Id));
        Assert.Null(storage.Get(MemoryStore.VectorPrefix + a.Id));
        Assert.Equal(0, store.Delete(["unknown"]).Count);
    }

    // ---- Export and import ----

    [Fact]
    public void An_export_holds_every_note_in_full_without_its_chat_and_importing_it_again_changes_nothing()
    {
        MemoryStore store = StoreOf(new Storage());
        Add(store, "name", "Sam");
        Add(store, "dislike", "Cilantro");
        Add(store, "project", new string('p', 1900));
        JObject export = store.Export();
        Assert.Equal("llma-memory", (string)export["format"]);
        JArray notes = (JArray)export["notes"];
        Assert.Equal(3, notes.Count);
        Assert.All(notes, n => Assert.Null(n["threadId"]));
        Assert.Contains(notes, n => (string)n["text"] == new string('p', 1900));

        MemoryImportPlan plan = store.PreviewImport(export.ToString(), NoNever);
        Assert.Equal("", plan.Error);
        Assert.Empty(plan.Add);
        Assert.Equal(3, plan.Duplicates);
        MemoryImportPlan applied = store.Import(export.ToString(), NoNever, out string refused);
        Assert.Equal("", refused);
        Assert.Empty(applied.Add);
        Assert.Equal(3, store.Load().Notes.Count);
    }

    [Fact]
    public void An_import_into_an_empty_memory_adds_cleaned_notes_with_fresh_ids_and_the_origin_user()
    {
        MemoryStore store = StoreOf(new Storage());
        string file = new JObject
        {
            ["notes"] = new JArray(
                new JObject { ["id"] = "evil", ["text"] = "Likes <b>tea</b>\n\nvery much", ["kind"] = "preference", ["origin"] = "explicit", ["threadId"] = "x" },
                new JObject { ["text"] = "Hates noise", ["kind"] = "dislike" },
                new JObject { ["text"] = "Odd kind", ["kind"] = "banana" })
        }.ToString();
        MemoryImportPlan plan = store.Import(file, NoNever, out _);
        Assert.Equal(3, plan.Add.Count);
        List<MemoryNote> saved = store.Load().Notes;
        Assert.Equal(3, saved.Count);
        Assert.DoesNotContain(saved, n => n.Id == "evil");
        Assert.All(saved, n => Assert.Equal(MemoryOrigins.User, n.Origin));
        Assert.All(saved, n => Assert.Equal("", n.ThreadId));
        MemoryNote tea = saved.Single(n => n.Text.StartsWith("Likes"));
        Assert.DoesNotContain("<", tea.Text);
        Assert.DoesNotContain("\n", tea.Text);
        Assert.True(saved.Single(n => n.Text == "Hates noise").Negative);
        Assert.Equal(MemoryKinds.Fact, saved.Single(n => n.Text == "Odd kind").Kind);
    }

    [Fact]
    public void An_import_never_replaces_a_filled_slot_and_never_drops_an_existing_note_when_full()
    {
        MemoryStore store = StoreOf(new Storage());
        Add(store, "name", "Sam");
        string file = new JObject
        {
            ["notes"] = new JArray(
                new JObject { ["text"] = "Samuel", ["slot"] = "name" },
                new JObject { ["text"] = "Sam", ["slot"] = "name" },
                new JObject { ["text"] = "Teaches maths", ["slot"] = "bio" })
        }.ToString();
        MemoryImportPlan plan = store.PreviewImport(file, NoNever);
        Assert.Equal(1, plan.Conflicts);
        Assert.Equal(1, plan.Duplicates);
        Assert.Single(plan.Add);
        Assert.True(plan.Add[0].Pinned);
        Assert.Equal(MemorySlots.Bio, plan.Add[0].Slot);
        store.Import(file, NoNever, out _);
        Assert.Equal("Sam", store.Load().Slot(MemorySlots.Name).Text);
        Assert.Equal("Teaches maths", store.Load().Slot(MemorySlots.Bio).Text);

        // A full memory: the import stops and says how many did not fit, and no existing note is evicted.
        MemoryStore full = StoreOf(new Storage());
        for (int i = 0; i < MemoryNoteSet.MaxNotes - 1; i++)
        {
            Add(full, "fact", $"Existing note {i}");
        }
        string many = new JObject { ["notes"] = new JArray(Enumerable.Range(0, 5).Select(i => new JObject { ["text"] = $"Incoming {i}" })) }.ToString();
        MemoryImportPlan fullPlan = full.Import(many, NoNever, out _);
        Assert.Single(fullPlan.Add);
        Assert.Equal(4, fullPlan.NoRoom);
        Assert.Equal(MemoryNoteSet.MaxNotes, full.Load().Notes.Count);
        Assert.Contains(full.Load().Notes, n => n.Text == "Existing note 0");
    }

    [Fact]
    public void An_import_reports_bad_entries_secrets_and_repeats_and_reads_a_bare_list()
    {
        MemoryStore store = StoreOf(new Storage());
        string file = new JArray(
            new JObject { ["text"] = "My password is hunter2" },
            new JObject { ["text"] = "Likes divorce lawyers" },
            new JObject { ["text"] = "" },
            new JObject { ["text"] = 5 },
            "just a string",
            new JObject { ["text"] = "Fine note" },
            new JObject { ["text"] = "fine   NOTE" }).ToString();
        MemoryImportPlan plan = store.PreviewImport(file, new MemoryNeverSave(["divorce"]));
        Assert.Equal(2, plan.Blocked);
        Assert.Equal(3, plan.Invalid);
        Assert.Equal(1, plan.Duplicates);
        Assert.Single(plan.Add);
    }

    [Fact]
    public void An_import_refuses_what_is_not_a_file_of_notes_and_caps_size_count_pins_and_times()
    {
        MemoryStore store = StoreOf(new Storage());
        Assert.Equal("empty", store.PreviewImport("  ", NoNever).Error);
        Assert.Equal("not-json", store.PreviewImport("{ nope", NoNever).Error);
        Assert.Equal("no-notes", store.PreviewImport("{\"x\":1}", NoNever).Error);
        Assert.Equal("no-notes", store.PreviewImport("42", NoNever).Error);
        Assert.Equal("too-large", store.PreviewImport(new string(' ', MemoryImport.MaxChars + 1), NoNever).Error);

        // More entries than a memory can hold: only the first MaxNotes are read.
        string huge = new JArray(Enumerable.Range(0, MemoryNoteSet.MaxNotes + 20).Select(i => new JObject { ["text"] = $"Note {i}", ["pinned"] = true })).ToString();
        MemoryImportPlan plan = store.PreviewImport(huge, NoNever);
        Assert.Equal(20, plan.Ignored);
        Assert.Equal(MemoryNoteSet.MaxNotes, plan.Add.Count);
        Assert.Equal(MemoryNoteSet.MaxPinned, plan.Add.Count(n => n.Pinned));
        Assert.Equal(MemoryNoteSet.MaxNotes - MemoryNoteSet.MaxPinned, plan.Unpinned);

        // A long text is cut; a time in the future or one that does not read is replaced by now; the import counts as a use.
        string odd = new JArray(new JObject { ["text"] = new string('z', 5000), ["created"] = "2999-01-01T00:00:00Z", ["lastUsed"] = "2020-01-01T00:00:00Z" }, new JObject { ["text"] = "Old one", ["created"] = "2025-03-04T05:06:07Z" }).ToString();
        MemoryImportPlan oddPlan = store.PreviewImport(odd, NoNever);
        Assert.Equal(MemoryNoteSet.MaxNoteChars, oddPlan.Add[0].Text.Length);
        Assert.Equal(T0.ToString("o"), oddPlan.Add[0].Created);
        Assert.Equal(T0.ToString("o"), oddPlan.Add[0].LastUsed);
        Assert.StartsWith("2025-03-04T05:06:07", oddPlan.Add[1].Created);
    }

    [Fact]
    public void A_preview_changes_nothing()
    {
        Storage storage = new();
        MemoryStore store = StoreOf(storage);
        Add(store, "fact", "One");
        string before = storage.Get(MemoryStore.NotesKey);
        store.PreviewImport("[{\"text\":\"Two\"}]", NoNever);
        Assert.Equal(before, storage.Get(MemoryStore.NotesKey));
    }

    // ---- Retention ----

    [Fact]
    public void Retention_forgets_unused_notes_but_never_pinned_or_slot_notes_and_can_be_undone()
    {
        DateTime now = T0;
        Storage storage = new();
        MemoryStore store = StoreOf(storage, () => now);
        MemoryNote stale = Add(store, "fact", "Stale one");
        MemoryNote pinned = Add(store, "fact", "Pinned stale");
        store.Update(pinned.Id, null, null, true);
        Add(store, "name", "Sam");
        MemoryNote used = Add(store, "fact", "Used lately");
        now = T0.AddDays(100);
        store.MarkUsed([used.Id]);
        MemoryNote fresh = Add(store, "fact", "Fresh");

        Assert.Null(store.ApplyRetention(0));
        MemoryChangeSet set = store.ApplyRetention(90);

        Assert.NotNull(set);
        Assert.Equal(MemoryChangeReasons.Retention, set.Reason);
        Assert.Equal([stale.Id], set.Changes.Select(c => c.NoteId));
        Assert.Equal(MemoryChangeTypes.Removed, set.Changes[0].Type);
        Assert.DoesNotContain(store.Load().Notes, n => n.Id == stale.Id);
        Assert.Equal(4, store.Load().Notes.Count);
        Assert.Contains(store.Load().Notes, n => n.Id == fresh.Id);
        Assert.Equal("retention", (string)store.Changes()[0].ToClientJson()["reason"]);

        // Nothing more is old enough, so a second sweep does nothing.
        Assert.Null(store.ApplyRetention(90));

        // Undo brings the note back and starts its unused time again (or the next sweep would forget it at once).
        MemoryUndoResult undo = store.Undo(set.Id);
        Assert.Equal(1, undo.Undone);
        MemoryNote back = store.Load().Find(stale.Id);
        Assert.NotNull(back);
        Assert.Equal(now.ToString("o"), back.LastUsed);
        Assert.Null(store.ApplyRetention(90));
    }

    [Fact]
    public void Retention_forgets_a_note_that_was_never_used_after_its_age_counted_from_when_it_was_saved_and_leaves_unreadable_times_alone()
    {
        DateTime now = T0;
        MemoryStore store = StoreOf(new Storage(), () => now);
        MemoryNote note = Add(store, "fact", "Never used");
        Assert.Null(store.ApplyRetention(30));
        now = T0.AddDays(31);
        Assert.Single(store.ApplyRetention(30).Changes);
        Assert.Empty(store.Load().Notes);

        Storage storage = new();
        storage.Values[MemoryStore.NotesKey] = "{\"version\":1,\"notes\":[{\"id\":\"a\",\"text\":\"No times\",\"kind\":\"fact\",\"created\":\"\",\"lastUsed\":\"\"}]}";
        MemoryStore odd = StoreOf(storage, () => T0.AddDays(5000));
        Assert.Null(odd.ApplyRetention(30));
        Assert.Single(odd.Load().Notes);
    }

    [Fact]
    public void Retention_deletes_nothing_from_a_store_that_cannot_be_trusted_and_forgets_at_most_the_sweep_cap()
    {
        Storage storage = new();
        storage.Values[MemoryStore.NotesKey] = "{\"version\":99,\"notes\":[{\"id\":\"a\",\"text\":\"Old\",\"kind\":\"fact\",\"created\":\"2000-01-01T00:00:00Z\"}]}";
        Assert.Null(StoreOf(storage, () => T0).ApplyRetention(30));

        DateTime now = T0;
        MemoryStore many = StoreOf(new Storage(), () => now);
        for (int i = 0; i < MemoryStore.MaxRetentionPerSweep + 10; i++)
        {
            Add(many, "fact", $"Old note {i}");
        }
        now = T0.AddDays(60);
        Assert.Equal(MemoryStore.MaxRetentionPerSweep, many.ApplyRetention(30).Changes.Count);
        Assert.Equal(10, many.Load().Notes.Count);
    }

    [Fact]
    public void The_retention_sweep_is_throttled_per_user_unless_forced()
    {
        MemoryService.ResetRetentionForTests();
        DateTime now = T0;
        MemoryStore store = StoreOf(new Storage(), () => now);
        Add(store, "fact", "Old");
        now = T0.AddDays(50);
        MemoryNote active = Add(store, "fact", "Active");
        store.MarkUsed([active.Id]);
        // The real clock is used for the throttle, the store's for the notes' ages.
        now = T0.AddDays(60);
        Assert.NotNull(MemoryService.RunRetentionIfDue(store, "u1", 30, false));
        now = T0.AddDays(75);
        MemoryNote again = Add(store, "fact", "Old again");
        now = T0.AddDays(200);
        Assert.Null(MemoryService.RunRetentionIfDue(store, "u1", 30, false));
        Assert.NotNull(store.Load().Find(again.Id));
        Assert.NotNull(MemoryService.RunRetentionIfDue(store, "u1", 30, true));
        Assert.Null(MemoryService.RunRetentionIfDue(store, "u1", 0, true));
        MemoryService.ResetRetentionForTests();
    }

    // ---- The controls ----

    [Fact]
    public void Controls_read_the_pause_and_retention_from_the_users_layer_and_the_admin_switch_from_the_shared_layer_only()
    {
        JObject user = JObject.Parse("{\"memoryPause\":{\"saving\":true,\"recall\":false},\"memoryRetention\":{\"unusedDays\":45},\"memoryAdmin\":{\"extractionDisabled\":true}}");
        JObject shared = JObject.Parse("{\"memoryPause\":{\"saving\":true,\"recall\":true},\"memoryRetention\":{\"unusedDays\":9},\"memoryAdmin\":{\"extractionDisabled\":false}}");
        MemoryControls c = MemoryControls.From(user, shared);
        Assert.True(c.SavingPaused);
        Assert.False(c.RecallPaused);
        Assert.Equal(45, c.RetentionDays);
        // A user cannot set the admin switch for themselves, and a shared pause is not a user's pause.
        Assert.False(c.AdminExtractionDisabled);
        MemoryControls shut = MemoryControls.From(new JObject(), JObject.Parse("{\"memoryAdmin\":{\"extractionDisabled\":true}}"));
        Assert.True(shut.AdminExtractionDisabled);
        Assert.False(shut.SavingPaused);
        Assert.Equal(MemoryControls.Default, MemoryControls.From(null, null));
    }

    [Fact]
    public void Controls_ignore_values_of_the_wrong_type_and_keep_retention_in_range()
    {
        MemoryControls bad = MemoryControls.From(JObject.Parse("{\"memoryPause\":{\"saving\":\"yes\",\"recall\":1},\"memoryRetention\":{\"unusedDays\":\"30\"}}"), JObject.Parse("{\"memoryAdmin\":{\"extractionDisabled\":\"true\"}}"));
        Assert.Equal(MemoryControls.Default, bad);
        Assert.Equal(MemoryControls.MinRetentionDays, MemoryControls.ClampDays(1));
        Assert.Equal(MemoryControls.MaxRetentionDays, MemoryControls.ClampDays(99999));
        Assert.Equal(0, MemoryControls.ClampDays(-5));
        Assert.Equal(0, MemoryControls.ClampDays(0));
        Assert.Equal(MemoryControls.MaxRetentionDays, MemoryControls.From(JObject.Parse("{\"memoryRetention\":{\"unusedDays\":1000000}}"), null).RetentionDays);
    }

    [Fact]
    public void An_admin_switch_or_a_pause_stops_the_extractor_whatever_the_users_own_switch_says()
    {
        Assert.True(MemoryControls.Default.ExtractionAllowed(true));
        Assert.False(MemoryControls.Default.ExtractionAllowed(false));
        Assert.False(new MemoryControls(true, false, 0, false).ExtractionAllowed(true));
        Assert.True(new MemoryControls(false, true, 0, false).ExtractionAllowed(true));
        Assert.False(new MemoryControls(false, false, 0, true).ExtractionAllowed(true));
    }

    [Fact]
    public void A_users_request_becomes_a_patch_of_only_the_fields_it_names_and_a_bad_retention_is_an_error_not_a_clamp()
    {
        Assert.Null(MemoryControls.UserPatch(new JObject()));
        Assert.Null(MemoryControls.UserPatch(new JObject { ["savingPaused"] = "yes" }));
        JObject patch = MemoryControls.UserPatch(new JObject { ["savingPaused"] = true, ["unusedDays"] = 7 }, out string error);
        Assert.Null(error);
        Assert.True((bool)patch["memoryPause"]["saving"]);
        Assert.Null(patch["memoryPause"]["recall"]);
        Assert.Equal(7, (int)patch["memoryRetention"]["unusedDays"]);
        Assert.Equal(0, (int)MemoryControls.UserPatch(new JObject { ["unusedDays"] = 0 })["memoryRetention"]["unusedDays"]);
        Assert.Equal(3650, (int)MemoryControls.UserPatch(new JObject { ["unusedDays"] = 3650 })["memoryRetention"]["unusedDays"]);
        Assert.Null(MemoryControls.UserPatch(new JObject { ["unusedDays"] = JValue.CreateNull() }));
        foreach (JToken bad in new JToken[] { 1, 6, -5, 3651, 1_000_000_000_000L, 12.5, "30", true })
        {
            Assert.Null(MemoryControls.UserPatch(new JObject { ["savingPaused"] = true, ["unusedDays"] = bad }, out string why));
            Assert.Contains("7", why);
            Assert.Contains("3650", why);
        }
        // The admin switch is not something a user's request can carry.
        Assert.Null(MemoryControls.UserPatch(new JObject { ["extractionDisabled"] = true, ["memoryAdmin"] = new JObject() }));
    }

    [Fact]
    public void The_admin_switch_is_written_into_the_shared_layer_and_the_pause_keys_are_not_ordinary_settings()
    {
        JObject shared = MemoryControls.WithAdminSwitch(new JObject { ["keep"] = 1 }, true);
        Assert.Equal(1, (int)shared["keep"]);
        Assert.True(MemoryControls.From(null, shared).AdminExtractionDisabled);
        Assert.False(MemoryControls.From(null, MemoryControls.WithAdminSwitch(shared, false)).AdminExtractionDisabled);
        // An ordinary settings save never writes these (they have routes of their own), so a stale page cannot undo a pause or plant the admin switch.
        string[] keys = [MemoryControls.PauseKey, MemoryControls.RetentionKey, MemoryControls.AdminKey];
        Assert.All(keys, k => Assert.Contains(k, Hartsy.Extensions.LLMAssistant.WebAPI.SettingsEndpoints.OwnRouteKeys));
    }

    // ---- Pause recall and what else it covers ----

    [Fact]
    public void Resuming_recall_restarts_the_unused_clock_so_a_long_pause_forgets_nothing_and_a_sweep_never_runs_while_paused()
    {
        MemoryService.ResetRetentionForTests();
        DateTime now = T0;
        MemoryStore store = StoreOf(new Storage(), () => now);
        MemoryNote note = Add(store, "fact", "Waiting through a long pause");
        MemoryControls paused = new(false, true, 30, false);
        MemoryControls running = new(false, false, 30, false);
        now = T0.AddDays(200);

        // Setting retention while recall is paused sweeps nothing.
        Assert.Equal(0, MemoryService.ApplyControlsChange(store, "u1", paused, paused, true));
        Assert.NotNull(store.Load().Find(note.Id));

        // Resuming restarts every note's clock, so the next sweep (even a forced one) finds nothing old.
        Assert.Equal(0, MemoryService.ApplyControlsChange(store, "u1", paused, running, false));
        Assert.Equal(now.ToString("o"), store.Load().Find(note.Id).LastUsed);
        Assert.Null(store.ApplyRetention(30));
        Assert.NotNull(store.Load().Find(note.Id));

        // Setting retention while running does sweep, at once.
        now = now.AddDays(100);
        Assert.Equal(1, MemoryService.ApplyControlsChange(store, "u1", running, running, true));
        Assert.Empty(store.Load().Notes);
        MemoryService.ResetRetentionForTests();
    }

    [Fact]
    public async Task While_recall_is_paused_the_extractor_is_shown_none_of_the_notes_and_while_it_is_not_it_is()
    {
        MemoryExtractionService.ResetForTests();
        MemoryStore store = StoreOf(new Storage());
        Add(store, "fact", "Secretly keeps bees in the garden");
        string Thread(string id) => new JObject
        {
            ["id"] = "t1",
            ["activeLeafId"] = id + "b",
            ["messages"] = new JArray(
                new JObject { ["id"] = id + "a", ["role"] = "user", ["content"] = "I moved to Lyon last month and I work as a nurse.", ["parentId"] = null },
                new JObject { ["id"] = id + "b", ["role"] = "assistant", ["content"] = "Lyon is lovely.", ["parentId"] = id + "a" })
        }.ToString();
        List<Hartsy.Extensions.LLMAssistant.LLMs.ExtendedLLMInput> calls = [];
        Task<Hartsy.Extensions.LLMAssistant.LLMs.LLMGenerateResult> Generate(Hartsy.Extensions.LLMAssistant.LLMs.ExtendedLLMInput input, CancellationToken ct)
        {
            calls.Add(input);
            return Task.FromResult(Hartsy.Extensions.LLMAssistant.LLMs.LLMGenerateResult.FromText("{\"ops\":[]}"));
        }
        MemoryExtractionSettings on = new(true, "", []);

        await MemoryExtractionService.ExtractAsync(store, "u1", "t1", JObject.Parse(Thread("p")), on, "m", null, Generate, CancellationToken.None, null, showNotes: false);
        Assert.Single(calls);
        Assert.DoesNotContain("bees", calls[0].UserMessage);
        Assert.Contains("Lyon", calls[0].UserMessage);

        await MemoryExtractionService.ExtractAsync(store, "u1", "t1", JObject.Parse(Thread("q")), on, "m", null, Generate, CancellationToken.None);
        Assert.Equal(2, calls.Count);
        Assert.Contains("bees", calls[1].UserMessage);
        MemoryExtractionService.ResetForTests();
    }

    // ---- Listing and adding by hand (the API behind the manager) ----

    [Fact]
    public void Listing_pages_through_the_notes_with_a_cursor_and_filters_by_search_and_kind()
    {
        MemoryStore store = StoreOf(new Storage());
        Add(store, "name", "Sam");
        for (int i = 0; i < 5; i++)
        {
            Add(store, "fact", $"Fact number {i}");
        }
        Add(store, "dislike", "Cilantro");
        Add(store, "preference", "Green tea");
        MemoryNote pinned = Add(store, "project", "Pinned project");
        store.Update(pinned.Id, null, null, true);
        MemoryNoteSet set = store.Load();

        JObject first = MemoryService.ListNotes(set, "", "", 4, "");
        Assert.Equal(4, ((JArray)first["notes"]).Count);
        Assert.Equal(9, (int)first["total"]);
        Assert.Equal(9, (int)first["matched"]);
        Assert.Equal("4", (string)first["nextCursor"]);
        List<string> seen = [.. ((JArray)first["notes"]).Select(n => (string)n["id"])];
        string cursor = (string)first["nextCursor"];
        while (cursor.Length > 0)
        {
            JObject page = MemoryService.ListNotes(set, "", "", 4, cursor);
            seen.AddRange(((JArray)page["notes"]).Select(n => (string)n["id"]));
            cursor = (string)page["nextCursor"];
        }
        Assert.Equal(9, seen.Distinct().Count());

        Assert.Equal(["Sam"], ((JArray)MemoryService.ListNotes(set, "", "profile", 0, "")["notes"]).Select(n => (string)n["text"]));
        Assert.Equal(["Cilantro"], ((JArray)MemoryService.ListNotes(set, "", "dislike", 0, "")["notes"]).Select(n => (string)n["text"]));
        Assert.Equal(["Green tea"], ((JArray)MemoryService.ListNotes(set, "", "preference", 0, "")["notes"]).Select(n => (string)n["text"]));
        Assert.Equal(5, (int)MemoryService.ListNotes(set, "", "fact", 0, "")["matched"]);
        Assert.Equal(2, (int)MemoryService.ListNotes(set, "", "pinned", 0, "")["matched"]);
        JObject searched = MemoryService.ListNotes(set, "NUMBER 3", "", 0, "");
        Assert.Equal(1, (int)searched["matched"]);
        Assert.Equal(9, (int)searched["total"]);
        Assert.Equal("", (string)searched["nextCursor"]);
    }

    [Fact]
    public void Listing_refuses_a_bad_kind_or_cursor_clamps_the_page_and_cuts_long_texts()
    {
        MemoryStore store = StoreOf(new Storage());
        Add(store, "fact", new string('x', 900));
        MemoryNoteSet set = store.Load();
        Assert.Null(MemoryService.ListNotes(set, "", "banana", 0, ""));
        Assert.Null(MemoryService.ListNotes(set, "", "", 0, "abc"));
        Assert.Null(MemoryService.ListNotes(set, "", "", 0, "-1"));
        Assert.Empty((JArray)MemoryService.ListNotes(set, "", "", 5, "99")["notes"]);
        JObject page = MemoryService.ListNotes(set, "", "", 100000, "");
        JToken note = ((JArray)page["notes"])[0];
        Assert.Equal(MemoryService.MaxListChars, ((string)note["text"]).Length);
        Assert.True((bool)note["truncated"]);
        Assert.Equal(1, (int)MemoryService.ListNotes(set, new string('q', 5000), "", 0, "")["total"]);
    }

    [Fact]
    public void Adding_by_hand_makes_a_user_note_and_never_drops_another_to_make_room()
    {
        MemoryStore store = StoreOf(new Storage());
        MemoryManageResult added = store.Add("  Likes jazz  ", "preference", false, null);
        Assert.True(added.Ok);
        Assert.Equal("Likes jazz", added.Note.Text);
        Assert.Equal(MemoryOrigins.User, added.Note.Origin);
        Assert.Equal(MemoryKinds.Preference, added.Note.Kind);
        Assert.True(store.Add("Hates noise", "dislike", false, null).Note.Negative);
        Assert.Equal(MemoryKinds.Fact, store.Add("Plain default", null, false, null).Note.Kind);
        Assert.Equal("duplicate", store.Add("likes   JAZZ", "preference", false, null).Error);
        Assert.Equal("empty", store.Add("  ", null, false, null).Error);
        Assert.Equal("kind", store.Add("x", "banana", false, null).Error);

        MemoryManageResult name = store.Add("Sam", "style", false, "name");
        Assert.True(name.Ok);
        Assert.True(name.Note.Pinned);
        Assert.Equal(MemorySlots.Name, name.Note.Slot);
        Assert.Equal("slotfilled", store.Add("Samuel", null, false, "name").Error);
        Assert.Equal("slot", store.Add("x", null, false, "shoes").Error);

        MemoryStore full = StoreOf(new Storage());
        for (int i = 0; i < MemoryNoteSet.MaxNotes; i++)
        {
            Add(full, "fact", $"Existing {i}");
        }
        Assert.Equal("full", full.Add("One too many", null, false, null).Error);
        Assert.Equal(MemoryNoteSet.MaxNotes, full.Load().Notes.Count);
        Assert.Contains(full.Load().Notes, n => n.Text == "Existing 0");

        MemoryStore pins = StoreOf(new Storage());
        for (int i = 0; i < MemoryNoteSet.MaxPinned; i++)
        {
            Assert.True(pins.Add($"Pinned {i}", null, true, null).Ok);
        }
        Assert.Equal("pinlimit", pins.Add("One more pin", null, true, null).Error);
        Assert.True(pins.Add("Unpinned is fine", null, false, null).Ok);
    }

    [Fact]
    public void Adding_is_refused_over_notes_written_by_a_newer_version()
    {
        Storage storage = new();
        storage.Values[MemoryStore.NotesKey] = "{\"version\":99,\"notes\":[]}";
        Assert.Equal("newer", StoreOf(storage).Add("x", null, false, null).Error);
    }

    // ---- One user never touches another's ----

    // ---- Retention after a quiet spell, memory_read, its own journal, and a partial Undo ----

    [Fact]
    public void After_a_long_idle_gap_a_lazy_sweep_forgets_nothing_and_the_first_reply_back_still_recalls_the_notes()
    {
        DateTime now = T0;
        MemoryStore store = StoreOf(new Storage(), () => now);
        Add(store, "fact", "Keeps bees in the garden");
        Add(store, "preference", "Green tea");
        now = T0.AddDays(31);
        Assert.Null(store.ApplyRetention(30, lazy: true));
        Assert.Equal(2, store.Load().Notes.Count);
        Assert.All(store.Load().Notes, n => Assert.Equal(now.ToString("o"), n.LastUsed));
        string prompt = MemoryService.RenderForPrompt(store, store.Load(), new MemoryRecallRequest("anything", null, null, true));
        Assert.Contains("Keeps bees in the garden", prompt);
        Assert.Contains("Green tea", prompt);
        now = now.AddDays(10);
        Assert.Null(store.ApplyRetention(30, lazy: true));
        Assert.Equal(2, store.Load().Notes.Count);

        // A sweep the user asked for (saving the setting) still forgets what is old, the gap rule being for the sweeps a reply starts.
        DateTime later = T0;
        MemoryStore asked = StoreOf(new Storage(), () => later);
        Add(asked, "fact", "Old");
        later = T0.AddDays(31);
        Assert.Single(asked.ApplyRetention(30).Changes);
    }

    [Fact]
    public void A_lazy_sweep_while_the_user_is_active_forgets_only_what_is_old_and_is_announced_once()
    {
        MemoryService.ResetRetentionForTests();
        DateTime now = T0;
        MemoryStore store = StoreOf(new Storage(), () => now);
        Add(store, "fact", "Old");
        now = T0.AddDays(50);
        MemoryNote active = Add(store, "fact", "Active");
        store.MarkUsed([active.Id]);
        now = T0.AddDays(60);
        MemoryChangeSet forgotten = MemoryService.RunRetentionIfDue(store, "u1", 30, false);
        Assert.NotNull(forgotten);
        Assert.Equal(["Active"], store.Load().Notes.Select(n => n.Text));
        // The next reply says so, once.
        Assert.Equal(forgotten.Id, MemoryService.TakeRetentionNotice("u1").Id);
        Assert.Null(MemoryService.TakeRetentionNotice("u1"));
        Assert.Null(MemoryService.TakeRetentionNotice("u2"));
        // A sweep the user asked for is not announced to them again.
        now = T0.AddDays(200);
        Assert.NotNull(MemoryService.RunRetentionIfDue(store, "u1", 30, true));
        Assert.Null(MemoryService.TakeRetentionNotice("u1"));
        MemoryService.ResetRetentionForTests();
    }

    [Fact]
    public void Notes_the_model_reads_with_memory_read_count_as_used_and_a_paused_read_or_write_does_nothing()
    {
        DateTime now = T0;
        MemoryStore store = StoreOf(new Storage(), () => now);
        MemoryNote note = Add(store, "fact", "Read by the model");
        now = T0.AddDays(40);
        JObject reply = MemoryService.ReadForTool(store, MemoryControls.Default, "");
        Assert.Equal(1, (int)reply["returned"]);
        Assert.Equal(now.ToString("o"), store.Load().Find(note.Id).LastUsed);
        Assert.Null(store.ApplyRetention(30));

        now = T0.AddDays(100);
        JObject paused = MemoryService.ReadForTool(store, new MemoryControls(false, true, 0, false), "");
        Assert.True((bool)paused["paused"]);
        Assert.Empty((JArray)paused["notes"]);
        Assert.NotEqual(now.ToString("o"), store.Load().Find(note.Id).LastUsed);
        Assert.Equal(0, (int)MemoryService.ReadForTool(null, MemoryControls.Default, "")["returned"]);

        Assert.Null(MemoryService.WriteRefusedWhilePaused(MemoryControls.Default));
        JObject refused = MemoryService.WriteRefusedWhilePaused(new MemoryControls(true, false, 0, false));
        Assert.False((bool)refused["success"]);
        Assert.True((bool)refused["paused"]);
        Assert.Contains("Do not try again", (string)refused["error"]);
    }

    [Fact]
    public void A_retention_sweep_is_not_pushed_out_of_the_history_by_later_extractions_and_a_partial_undo_keeps_what_could_not_come_back()
    {
        DateTime now = T0;
        MemoryStore store = StoreOf(new Storage(), () => now);
        MemoryNote one = Add(store, "fact", "Forgotten one");
        MemoryNote two = Add(store, "fact", "Forgotten two");
        MemoryNote three = Add(store, "fact", "Forgotten three");
        now = T0.AddDays(100);
        Add(store, "fact", "Kept");
        store.MarkUsed([store.Load().Notes.Last().Id]);
        MemoryChangeSet sweep = store.ApplyRetention(30);
        Assert.Equal(3, sweep.Changes.Count);
        for (int i = 0; i < MemoryStore.MaxChangeSets + 2; i++)
        {
            Assert.NotNull(store.ApplyExtraction([new MemoryOp("add", "", "", MemoryKinds.Fact, $"Learned {i}", false, MemoryOrigins.Assistant)], "t1").Changes);
        }
        IReadOnlyList<MemoryChangeSet> history = store.Changes();
        Assert.Equal(MemoryStore.MaxChangeSets + 1, history.Count);
        Assert.Contains(history, s => s.Id == sweep.Id);
        Assert.Equal(MemoryStore.MaxChangeSets, history.Count(s => s.Reason == ""));

        // Someone saved the words of one forgotten note again, so that one cannot come back: the other two do, and it stays in the set.
        Add(store, "fact", "Forgotten two");
        MemoryUndoResult partial = store.Undo(sweep.Id);
        Assert.Equal(2, partial.Undone);
        Assert.Equal(1, partial.Skipped);
        MemoryChangeSet left = store.Changes().Single(s => s.Id == sweep.Id);
        Assert.Equal([two.Id], left.Changes.Select(c => c.NoteId));
        Assert.Contains(store.Load().Notes, n => n.Id == one.Id);
        Assert.Contains(store.Load().Notes, n => n.Id == three.Id);

        // Once the clash is gone, it can be undone again, and then the set is gone.
        store.Delete([store.Load().Notes.Single(n => n.Text == "Forgotten two").Id]);
        MemoryUndoResult rest = store.Undo(sweep.Id);
        Assert.Equal(1, rest.Undone);
        Assert.DoesNotContain(store.Changes(), s => s.Id == sweep.Id);
        Assert.Equal(0, store.Undo(sweep.Id).Undone);
    }

    [Fact]
    public void Clearing_memory_removes_the_retention_history_too()
    {
        DateTime now = T0;
        Storage storage = new();
        MemoryStore store = StoreOf(storage, () => now);
        Add(store, "fact", "Old");
        now = T0.AddDays(100);
        Add(store, "fact", "Fresh");
        store.MarkUsed([store.Load().Notes.Last().Id]);
        Assert.NotNull(store.ApplyRetention(30));
        Assert.NotNull(storage.Get(MemoryStore.RetentionJournalKey));
        store.Clear();
        Assert.Null(storage.Get(MemoryStore.RetentionJournalKey));
        Assert.Empty(store.Changes());
    }

    // ---- The name, pronouns, bio and work notes are always pinned and do not count against the pin limit ----

    [Fact]
    public void A_slot_note_is_always_pinned_and_never_counts_toward_the_pin_limit()
    {
        MemoryStore store = StoreOf(new Storage());
        for (int i = 0; i < MemoryNoteSet.MaxPinned; i++)
        {
            Assert.True(store.Add($"Pinned {i}", null, true, null).Ok);
        }
        Assert.True(store.Add("Sam", null, false, "name").Note.Pinned);
        Assert.Equal("pinlimit", store.Add("One more", null, true, null).Error);
        MemoryNote other = store.Add("Not pinned", null, false, null).Note;
        Assert.Equal("pinlimit", store.Update(other.Id, null, null, true).Error);

        // An import with the pins full and a slot note: the slot note comes in pinned, the others lose the pin.
        string file = new JObject { ["notes"] = new JArray(new JObject { ["text"] = "Teaches maths", ["slot"] = "bio", ["pinned"] = true }, new JObject { ["text"] = "Wants a pin", ["pinned"] = true }) }.ToString();
        MemoryImportPlan plan = store.PreviewImport(file, NoNever);
        Assert.Equal(2, plan.Add.Count);
        Assert.True(plan.Add.Single(n => n.Slot == "bio").Pinned);
        Assert.False(plan.Add.Single(n => n.Slot == "").Pinned);
        Assert.Equal(1, plan.Unpinned);
        store.Import(file, NoNever, out _);
        Assert.True(store.Load().Slot(MemorySlots.Bio).Pinned);

        // A file of many slot notes and pinned notes never leaves a slot note unpinned.
        MemoryStore empty = StoreOf(new Storage());
        string many = new JObject { ["notes"] = new JArray(Enumerable.Range(0, 40).Select(i => new JObject { ["text"] = $"P{i}", ["pinned"] = true }).Concat([new JObject { ["text"] = "Sam", ["slot"] = "name" }, new JObject { ["text"] = "he/him", ["slot"] = "pronouns" }])) }.ToString();
        empty.Import(many, NoNever, out _);
        Assert.All(empty.Load().Notes.Where(n => n.Slot.Length > 0), n => Assert.True(n.Pinned));
        Assert.Equal(MemoryNoteSet.MaxPinned, empty.Load().Notes.Count(n => n.Pinned && n.Slot.Length == 0));
    }

    // ---- Settings resets keep what is about memory ----

    [Fact]
    public void A_settings_reset_keeps_the_memory_controls_and_nothing_else()
    {
        JObject layer = JObject.Parse("{\"memoryExtraction\":{\"enabled\":true},\"memoryPause\":{\"saving\":true},\"memoryRetention\":{\"unusedDays\":30},\"preferredModel\":\"x\",\"memoryAdmin\":{\"extractionDisabled\":true}}");
        JObject kept = Hartsy.Extensions.LLMAssistant.Services.SettingsService.KeepKeys(layer, Hartsy.Extensions.LLMAssistant.Services.SettingsService.PrivacyUserKeys);
        Assert.Equal(["memoryExtraction", "memoryPause", "memoryRetention"], kept.Properties().Select(p => p.Name).OrderBy(n => n));
        Assert.True((bool)kept["memoryPause"]["saving"]);
        Assert.NotSame(layer["memoryPause"], kept["memoryPause"]);
        JObject sharedKept = Hartsy.Extensions.LLMAssistant.Services.SettingsService.KeepKeys(layer, Hartsy.Extensions.LLMAssistant.Services.SettingsService.PrivacySharedKeys);
        Assert.Equal(["memoryAdmin"], sharedKept.Properties().Select(p => p.Name));
        Assert.Empty(Hartsy.Extensions.LLMAssistant.Services.SettingsService.KeepKeys(new JObject { ["preferredModel"] = "x" }, Hartsy.Extensions.LLMAssistant.Services.SettingsService.PrivacyUserKeys));
        Assert.Empty(Hartsy.Extensions.LLMAssistant.Services.SettingsService.KeepKeys(null, Hartsy.Extensions.LLMAssistant.Services.SettingsService.PrivacyUserKeys));
        // What a reset keeps is exactly what the memory settings routes own.
        Assert.All(Hartsy.Extensions.LLMAssistant.Services.SettingsService.PrivacyUserKeys.Concat(Hartsy.Extensions.LLMAssistant.Services.SettingsService.PrivacySharedKeys).Where(k => k != "memoryExtraction"), k => Assert.Contains(k, Hartsy.Extensions.LLMAssistant.WebAPI.SettingsEndpoints.OwnRouteKeys));
        Assert.Contains("memoryExtraction", Hartsy.Extensions.LLMAssistant.Services.SettingsService.UserOnlyKeys);
    }

    // ---- Pause and the admin switch in the extractor ----

    [Fact]
    public void The_extractor_says_which_switch_stopped_it()
    {
        MemoryExtractionSettings on = new(true, "", []);
        MemoryExtractionSettings off = new(false, "", []);
        Assert.Null(MemoryExtractionService.GateReason(on, MemoryControls.Default));
        Assert.Contains("switched off (Settings", MemoryExtractionService.GateReason(off, MemoryControls.Default));
        Assert.Contains("administrator", MemoryExtractionService.GateReason(on, new MemoryControls(false, false, 0, true)));
        Assert.Contains("paused", MemoryExtractionService.GateReason(on, new MemoryControls(true, false, 0, false)));
        // Recall paused alone does not stop saving.
        Assert.Null(MemoryExtractionService.GateReason(on, new MemoryControls(false, true, 0, false)));
        Assert.NotNull(MemoryExtractionService.GateReason(off, new MemoryControls(true, false, 0, true)));
    }

    [Fact]
    public async Task Saving_paused_while_the_model_is_working_saves_nothing_and_is_asked_once()
    {
        MemoryExtractionService.ResetForTests();
        MemoryStore store = StoreOf(new Storage());
        string thread = new JObject
        {
            ["id"] = "t1",
            ["activeLeafId"] = "pb",
            ["messages"] = new JArray(
                new JObject { ["id"] = "pa", ["role"] = "user", ["content"] = "I moved to Lyon last month and I work as a nurse.", ["parentId"] = null },
                new JObject { ["id"] = "pb", ["role"] = "assistant", ["content"] = "Lyon is lovely.", ["parentId"] = "pa" })
        }.ToString();
        Task<Hartsy.Extensions.LLMAssistant.LLMs.LLMGenerateResult> Generate(Hartsy.Extensions.LLMAssistant.LLMs.ExtendedLLMInput input, CancellationToken ct)
            => Task.FromResult(Hartsy.Extensions.LLMAssistant.LLMs.LLMGenerateResult.FromText("{\"ops\":[{\"op\":\"add\",\"kind\":\"fact\",\"text\":\"Works as a nurse in Lyon.\"}]}"));
        int asked = 0;
        ExtractionOutcome outcome = await MemoryExtractionService.ExtractAsync(store, "u1", "t1", JObject.Parse(thread), new MemoryExtractionSettings(true, "", []), "m", null, Generate, CancellationToken.None, null, () => { asked++; return true; });
        Assert.Equal("cancelled", outcome.Status);
        Assert.Empty(store.Load().Notes);
        Assert.Equal(1, asked);

        MemoryExtractionService.ResetForTests();
        ExtractionOutcome saved = await MemoryExtractionService.ExtractAsync(store, "u1", "t1", JObject.Parse(thread), new MemoryExtractionSettings(true, "", []), "m", null, Generate, CancellationToken.None, null, () => false);
        Assert.Equal("saved", saved.Status);
        Assert.Single(store.Load().Notes);
        MemoryExtractionService.ResetForTests();
    }

    // ---- Users and their locks ----

    [Fact]
    public async Task Two_users_never_see_each_others_notes_and_the_stores_of_one_user_share_one_lock()
    {
        Storage storageA = new();
        Storage storageB = new();
        MemoryStore a1 = MemoryService.StoreOver(storageA, "user-a");
        MemoryStore a2 = MemoryService.StoreOver(storageA, "user-a");
        MemoryStore b = MemoryService.StoreOver(storageB, "user-b");
        MemoryNote theirs = b.Write("fact", "B's secret fact", MemoryOrigins.Explicit, "").Note;

        // Two store objects for one user (every request makes its own) write at once: nothing is lost, because they share the user's lock.
        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() => (i % 2 == 0 ? a1 : a2).Write("fact", $"A fact {i}", MemoryOrigins.Explicit, ""))));
        Assert.Equal(60, a1.Load().Notes.Count);
        Assert.Equal(60, a2.Load().Notes.Count);
        Assert.Equal(1, b.Load().Notes.Count);

        Assert.Equal("notfound", a1.Update(theirs.Id, "hacked", null, null).Error);
        Assert.Equal(0, a2.Delete([theirs.Id]).Count);
        Assert.Null(a1.Get(theirs.Id));
        Assert.DoesNotContain("B's", a1.Export().ToString());
        Assert.Equal(0, a1.PreviewImport(b.Export().ToString(), NoNever).Duplicates);
        a1.Clear();
        Assert.Empty(a2.Load().Notes);
        Assert.Equal(["B's secret fact"], b.Load().Notes.Select(n => n.Text));
        Assert.Equal(theirs.Id, b.Load().Notes[0].Id);
    }
}
