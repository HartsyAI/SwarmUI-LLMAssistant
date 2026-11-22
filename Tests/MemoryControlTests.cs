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
        // The real clock is used for the throttle, the store's for the notes' ages.
        now = T0.AddDays(60);
        Assert.NotNull(MemoryService.RunRetentionIfDue(store, "u1", 30, false));
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
    public void A_users_request_becomes_a_patch_of_only_the_fields_it_names()
    {
        Assert.Null(MemoryControls.UserPatch(new JObject()));
        Assert.Null(MemoryControls.UserPatch(new JObject { ["savingPaused"] = "yes" }));
        JObject patch = MemoryControls.UserPatch(new JObject { ["savingPaused"] = true, ["unusedDays"] = 3 });
        Assert.True((bool)patch["memoryPause"]["saving"]);
        Assert.Null(patch["memoryPause"]["recall"]);
        Assert.Equal(MemoryControls.MinRetentionDays, (int)patch["memoryRetention"]["unusedDays"]);
        Assert.Equal(0, (int)MemoryControls.UserPatch(new JObject { ["unusedDays"] = 0 })["memoryRetention"]["unusedDays"]);
        Assert.Equal(MemoryControls.MaxRetentionDays, (int)MemoryControls.UserPatch(new JObject { ["unusedDays"] = 1e12 })["memoryRetention"]["unusedDays"]);
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

    // ---- One user never touches another's ----

    [Fact]
    public void Every_change_stays_in_the_users_own_notes()
    {
        Storage storageA = new();
        Storage storageB = new();
        MemoryStore a = StoreOf(storageA);
        MemoryStore b = StoreOf(storageB);
        MemoryNote mine = Add(a, "fact", "A's secret fact");
        MemoryNote theirs = Add(b, "fact", "B's secret fact");

        Assert.Equal("notfound", a.Update(theirs.Id, "hacked", null, null).Error);
        Assert.Equal(0, a.Delete([theirs.Id]).Count);
        Assert.Null(a.Get(theirs.Id));
        Assert.DoesNotContain("B's", a.Export().ToString());
        Assert.Equal(0, a.PreviewImport(b.Export().ToString(), NoNever).Duplicates);
        a.Import(b.Export().ToString(), NoNever, out _);
        Assert.Equal(["B's secret fact"], b.Load().Notes.Select(n => n.Text));
        Assert.Equal(2, a.Load().Notes.Count);
        Assert.Equal("A's secret fact", a.Load().Find(mine.Id).Text);
    }
}
