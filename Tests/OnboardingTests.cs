using Hartsy.Extensions.LLMAssistant.Services.Onboarding;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Services.Readiness;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The first-run guide's rules that need no user or server: the starter set (installing twice, never writing over anything, each user apart), the saved state, and what the checklist says.</summary>
public class OnboardingTests
{
    /// <summary>One user's items in dictionaries.</summary>
    private sealed class FakeTarget(string userId = "local") : IStarterTarget
    {
        public string UserId => userId;

        /// <summary>The items, by kind and id; the value is the stored text.</summary>
        public readonly Dictionary<(string Kind, string Id), string> Items = [];

        /// <summary>The assistant each added chat was given.</summary>
        public readonly Dictionary<string, string> ChatAssistants = [];

        /// <summary>How many saves were made.</summary>
        public int Saves;

        /// <summary>The kinds this user may not make.</summary>
        public HashSet<string> Refused = [];

        /// <summary>An id whose save fails, one whose save throws, and one that something else saves between the look and the add.</summary>
        public string Failing, Throwing, Racing;

        public string Refusal(string kind) => Refused.Contains(kind) ? "not allowed" : null;

        public bool Has(string kind, string id) => Items.ContainsKey((kind, id));

        public bool HasAssistant(string assistantId) => Items.ContainsKey((StarterKinds.Assistant, assistantId));

        public string AddAssistant(StarterAssistant assistant) => Add(StarterKinds.Assistant, assistant.Id, assistant.ChatInstruction);

        public string AddSnippet(StarterSnippet snippet) => Add(StarterKinds.Snippet, snippet.Name, snippet.Body);

        public string AddChat(StarterChat chat, string assistantId)
        {
            ChatAssistants[chat.IdFor(UserId)] = assistantId;
            return Add(StarterKinds.Chat, chat.IdFor(UserId), chat.Title);
        }

        private string Add(string kind, string id, string text)
        {
            if (id == Throwing)
            {
                throw new InvalidOperationException("boom at C:/secret/path");
            }
            if (id == Failing)
            {
                return "full";
            }
            if (id == Racing)
            {
                return StarterSkips.AlreadyThere;
            }
            Saves++;
            // Add, not indexing: writing over an item is a failure of the test.
            Items.Add((kind, id), text);
            return null;
        }
    }

    /// <summary>One user's saved state.</summary>
    private sealed class FakeStorage : IOnboardingStorage
    {
        public string Value;
        public int Writes;

        public string Read() => Value;

        public void Write(string value)
        {
            Value = value;
            Writes++;
        }
    }

    private static int Total => StarterContent.Assistants.Length + StarterContent.Snippets.Length + StarterContent.Chats.Length;

    // ---- The starter set ----

    [Fact]
    public void The_first_install_adds_everything_and_a_second_adds_nothing()
    {
        FakeTarget user = new();
        (JArray added, JArray skipped) = StarterInstaller.Install(user);
        Assert.Equal(Total, added.Count);
        Assert.Empty(skipped);
        Assert.Equal(Total, user.Saves);
        (JArray again, JArray left) = StarterInstaller.Install(user);
        Assert.Empty(again);
        Assert.Equal(Total, left.Count);
        Assert.All(left, e => Assert.Equal(StarterSkips.Exists, e["code"].ToString()));
        Assert.Equal(Total, user.Saves);
    }

    [Fact]
    public void An_item_the_user_has_is_never_written_over_and_only_the_missing_ones_come_back()
    {
        FakeTarget user = new();
        user.Items[(StarterKinds.Assistant, StarterContent.GeneralId)] = "mine";
        user.Items[(StarterKinds.Snippet, "enhance-prompt")] = "my own command";
        StarterInstaller.Install(user);
        Assert.Equal("mine", user.Items[(StarterKinds.Assistant, StarterContent.GeneralId)]);
        Assert.Equal("my own command", user.Items[(StarterKinds.Snippet, "enhance-prompt")]);
        user.Items[(StarterKinds.Assistant, StarterContent.ResearcherId)] = "edited";
        user.Items.Remove((StarterKinds.Snippet, "video-prompt"));
        (JArray added, _) = StarterInstaller.Install(user);
        Assert.Equal("video-prompt", Assert.Single(added)["id"].ToString());
        Assert.Equal("edited", user.Items[(StarterKinds.Assistant, StarterContent.ResearcherId)]);
    }

    [Fact]
    public void Each_user_gets_their_own_set()
    {
        FakeTarget one = new("one"), two = new("two");
        StarterInstaller.Install(one);
        Assert.Equal(Total, (int)StarterInstaller.Describe(two)["missing"]);
        StarterInstaller.Install(two);
        Assert.Equal(Total, two.Saves);
        // Their chats have ids of their own, as every other chat does.
        Assert.Empty(one.ChatAssistants.Keys.Intersect(two.ChatAssistants.Keys));
        two.Items.Clear();
        Assert.Equal(0, (int)StarterInstaller.Describe(one)["missing"]);
    }

    [Fact]
    public void A_kind_the_user_may_not_make_is_skipped_and_the_rest_installed()
    {
        FakeTarget user = new() { Refused = [StarterKinds.Assistant, StarterKinds.Snippet] };
        (JArray added, JArray skipped) = StarterInstaller.Install(user);
        Assert.All(added, e => Assert.Equal(StarterKinds.Chat, e["kind"].ToString()));
        Assert.Equal(StarterContent.Chats.Length, added.Count);
        Assert.Equal(Total - StarterContent.Chats.Length, skipped.Count(e => e["code"].ToString() == StarterSkips.Permission));
        // Without the starter assistants the chats go to the default one.
        Assert.All(user.ChatAssistants.Values, id => Assert.Equal(AssistantConstants.DefaultId, id));
    }

    [Fact]
    public void Chats_belong_to_their_starter_assistant_when_it_is_there()
    {
        FakeTarget user = new();
        StarterInstaller.Install(user);
        foreach (StarterChat chat in StarterContent.Chats)
        {
            Assert.Equal(chat.AssistantId, user.ChatAssistants[chat.IdFor(user.UserId)]);
        }
    }

    [Fact]
    public void One_item_that_cannot_be_saved_does_not_stop_the_others_or_leak_the_reason_for_a_crash()
    {
        FakeTarget user = new() { Failing = "video-prompt", Throwing = StarterContent.ResearcherId };
        (JArray added, JArray skipped) = StarterInstaller.Install(user);
        Assert.Equal(Total - 2, added.Count);
        Assert.Equal("full", skipped.Single(e => e["id"].ToString() == "video-prompt")["reason"].ToString());
        JToken crashed = skipped.Single(e => e["id"].ToString() == StarterContent.ResearcherId);
        Assert.Equal(StarterSkips.Error, crashed["code"].ToString());
        Assert.DoesNotContain("secret", crashed["reason"].ToString());
    }

    [Fact]
    public void The_content_is_saveable_as_it_is()
    {
        Assert.All(StarterContent.Assistants, a => Assert.Matches("^[A-Za-z0-9_-]{1,64}$", a.Id));
        Assert.All(StarterContent.Snippets, s =>
        {
            Assert.True(PromptAsset.IsValidName(s.Name), s.Name);
            Assert.Contains("$ARGUMENTS", s.Body);
        });
        Assert.All(StarterContent.Assistants.SelectMany(a => a.ToolIds), t => Assert.NotEqual(ToolConstants.ShellExec, t));
        Assert.Equal([StarterContent.GeneralId, StarterContent.PromptHelperId, StarterContent.ResearcherId], StarterContent.Assistants.Select(a => a.Id));
    }

    [Fact]
    public void An_example_chat_has_one_id_per_user_and_is_one_branch_from_the_users_message()
    {
        foreach (StarterChat chat in StarterContent.Chats)
        {
            string id = chat.IdFor("ada");
            Assert.Matches("^[0-9a-f]{32}$", id);
            Assert.Equal(id, chat.IdFor("ada"));
            Assert.NotEqual(id, chat.IdFor("bob"));
            JObject thread = chat.ToThread("ada", "default", DateTime.UtcNow);
            Assert.Equal(id, thread["id"].ToString());
            JArray messages = (JArray)thread["messages"];
            Assert.Equal(0, messages.Count % 2);
            Assert.Equal(JTokenType.Null, messages[0]["parentId"].Type);
            for (int i = 0; i < messages.Count; i++)
            {
                Assert.Equal(i % 2 == 0 ? "user" : "assistant", messages[i]["role"].ToString());
                if (i > 0)
                {
                    Assert.Equal(messages[i - 1]["id"].ToString(), messages[i]["parentId"].ToString());
                }
            }
            Assert.Equal(messages[^1]["id"].ToString(), thread["activeLeafId"].ToString());
        }
        Assert.Equal(StarterContent.Chats.Length, StarterContent.Chats.Select(c => c.IdFor("ada")).Distinct().Count());
    }

    [Fact]
    public void An_item_saved_by_something_else_meanwhile_is_reported_as_there_not_as_added_or_failed()
    {
        FakeTarget user = new() { Racing = "video-prompt" };
        (JArray added, JArray skipped) = StarterInstaller.Install(user);
        Assert.Equal(Total - 1, added.Count);
        Assert.Equal(StarterSkips.Exists, Assert.Single(skipped)["code"].ToString());
    }

    [Fact]
    public void An_assistant_is_there_by_its_id_or_by_its_name()
    {
        StarterAssistant researcher = StarterContent.Assistants.Single(a => a.Id == StarterContent.ResearcherId);
        Assert.False(StarterMatching.HasAssistant([], researcher));
        Assert.False(StarterMatching.HasAssistant(new JObject { ["x"] = new JObject { ["name"] = "Research notes" } }, researcher));
        Assert.True(StarterMatching.HasAssistant(new JObject { [researcher.Id] = new JObject { ["name"] = "Renamed" } }, researcher));
        Assert.True(StarterMatching.HasAssistant(new JObject { ["mine"] = new JObject { ["name"] = " researcher " } }, researcher));
    }

    [Fact]
    public void A_snippet_name_is_in_use_by_a_command_or_a_skill_and_a_create_only_write_replaces_neither()
    {
        string root = Path.Combine(Path.GetTempPath(), $"llma-onboarding-{Guid.NewGuid():N}");
        PromptAssetSet none = new();
        PromptAsset skill = new(PromptAsset.KindSkill, "video-prompt", "", "", "", PromptAsset.ScopeShared, null, false);
        try
        {
            Assert.False(StarterMatching.HasSnippet(none, [], root, "video-prompt"));
            Assert.True(StarterMatching.HasSnippet(new PromptAssetSet { Assets = [skill] }, [], root, "video-prompt"));
            // A trusted workspace's, of either kind: a personal command would take its place there.
            PromptAsset inProject = new(PromptAsset.KindCommand, "video-prompt", "", "", "", PromptAsset.ScopeWorkspace, null, false);
            Assert.True(StarterMatching.HasSnippet(none, [inProject], root, "video-prompt"));
            Assert.True(StarterMatching.HasSnippet(none, [skill], root, "video-prompt"));
            Assert.False(StarterMatching.HasSnippet(none, [inProject], root, "enhance-prompt"));
            Assert.True(PromptAssetService.Write(root, PromptAsset.ScopePersonal, PromptAsset.KindSkill, "video-prompt", "my skill", true, out _));
            Assert.True(StarterMatching.HasSnippet(none, [], root, "video-prompt"));
            Assert.False(PromptAssetService.Write(root, PromptAsset.ScopePersonal, PromptAsset.KindCommand, "video-prompt", "starter", true, out string shadow));
            Assert.Equal(PromptAssetService.AlreadyExists, shadow);
            string file = Path.Combine(root, "commands", "enhance-prompt.md");
            Assert.True(PromptAssetService.Write(root, PromptAsset.ScopePersonal, PromptAsset.KindCommand, "enhance-prompt", "mine", false, out _));
            Assert.False(PromptAssetService.Write(root, PromptAsset.ScopePersonal, PromptAsset.KindCommand, "enhance-prompt", "starter", true, out string error));
            Assert.Equal(PromptAssetService.AlreadyExists, error);
            Assert.Equal("mine", File.ReadAllText(file));
            // An ordinary save still replaces.
            Assert.True(PromptAssetService.Write(root, PromptAsset.ScopePersonal, PromptAsset.KindCommand, "enhance-prompt", "newer", false, out _));
            Assert.Equal("newer", File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    // ---- The state ----

    [Fact]
    public void A_new_user_with_no_chats_sees_the_guide_and_one_with_chats_does_not()
    {
        Assert.Equal(OnboardingState.New, OnboardingState.Load(new FakeStorage()));
        Assert.True(OnboardingState.ShouldShow(OnboardingState.New, false));
        Assert.False(OnboardingState.ShouldShow(OnboardingState.New, true));
        Assert.True(OnboardingState.ShouldShow(OnboardingState.Pending, true));
        Assert.False(OnboardingState.ShouldShow(OnboardingState.Skipped, false));
        Assert.False(OnboardingState.ShouldShow(OnboardingState.Completed, false));
    }

    [Theory]
    [InlineData(OnboardingState.ActionSkip, OnboardingState.Skipped)]
    [InlineData(OnboardingState.ActionComplete, OnboardingState.Completed)]
    [InlineData(OnboardingState.ActionRun, OnboardingState.Pending)]
    public void An_action_is_saved_once_however_often_it_is_done(string action, string expected)
    {
        FakeStorage storage = new();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(OnboardingState.Apply(storage, action, out string status, out _));
            Assert.Equal(expected, status);
        }
        Assert.Equal(1, storage.Writes);
        Assert.Equal(expected, OnboardingState.Load(storage));
    }

    [Fact]
    public void A_reset_makes_the_user_new_again()
    {
        FakeStorage storage = new();
        Assert.True(OnboardingState.Apply(storage, OnboardingState.ActionReset, out string status, out _));
        Assert.Equal(OnboardingState.New, status);
        Assert.Equal(0, storage.Writes);
        OnboardingState.Apply(storage, OnboardingState.ActionComplete, out _, out _);
        OnboardingState.Apply(storage, OnboardingState.ActionReset, out status, out _);
        Assert.Equal(OnboardingState.New, status);
        Assert.Equal(OnboardingState.New, OnboardingState.Load(storage));
        Assert.Equal(2, storage.Writes);
    }

    [Fact]
    public void Each_user_has_their_own_state()
    {
        FakeStorage one = new(), two = new();
        OnboardingState.Apply(one, OnboardingState.ActionSkip, out _, out _);
        Assert.Equal(OnboardingState.Skipped, OnboardingState.Load(one));
        Assert.Equal(OnboardingState.New, OnboardingState.Load(two));
        Assert.Equal(0, two.Writes);
    }

    [Fact]
    public void An_unknown_action_changes_nothing_and_a_damaged_value_reads_as_new()
    {
        FakeStorage storage = new();
        Assert.False(OnboardingState.Apply(storage, "erase", out _, out string error));
        Assert.NotNull(error);
        Assert.False(OnboardingState.Apply(storage, null, out _, out _));
        Assert.Equal(0, storage.Writes);
        foreach (string bad in new[] { "not json", "[]", "{}", """{"status":"odd"}""" })
        {
            Assert.Equal(OnboardingState.New, OnboardingState.Load(new FakeStorage { Value = bad }));
        }
    }

    [Fact]
    public void Installing_in_a_first_run_keeps_the_guide_open_only_when_something_was_added()
    {
        FakeStorage first = new();
        OnboardingState.DuringInstall(first, false, () =>
        {
            // Already kept open while the chats are being made.
            Assert.Equal(OnboardingState.Pending, OnboardingState.Observe(first, true));
            return true;
        });
        Assert.Equal(OnboardingState.Pending, OnboardingState.Load(first));
        FakeStorage nothing = new();
        OnboardingState.DuringInstall(nothing, false, () => false);
        Assert.Equal(OnboardingState.New, OnboardingState.Load(nothing));
        FakeStorage broken = new();
        Assert.Throws<InvalidOperationException>(() => OnboardingState.DuringInstall(broken, false, () => throw new InvalidOperationException()));
        Assert.Equal(OnboardingState.New, OnboardingState.Load(broken));
        // Dismissed while it ran: that stays.
        FakeStorage dismissed = new();
        OnboardingState.DuringInstall(dismissed, false, () =>
        {
            OnboardingState.Apply(dismissed, OnboardingState.ActionSkip, out _, out _);
            return false;
        });
        Assert.Equal(OnboardingState.Skipped, OnboardingState.Load(dismissed));
        FakeStorage used = new();
        OnboardingState.DuringInstall(used, true, () => true);
        Assert.Equal(0, used.Writes);
        FakeStorage skipped = new();
        OnboardingState.Apply(skipped, OnboardingState.ActionSkip, out _, out _);
        OnboardingState.DuringInstall(skipped, false, () => true);
        Assert.Equal(OnboardingState.Skipped, OnboardingState.Load(skipped));
    }

    [Fact]
    public async Task Two_installs_at_once_leave_a_new_user_in_the_guide()
    {
        FakeStorage storage = new();
        int taken = 0;
        using ManualResetEventSlim firstInside = new(), secondDone = new();
        // The install that opens the guide is held inside until the other has had its turn, or has shown (by this wait running out) that it waits for this one.
        Task first = Task.Run(() => OnboardingState.DuringInstall(storage, false, () =>
        {
            firstInside.Set();
            secondDone.Wait(TimeSpan.FromSeconds(2));
            return Interlocked.Exchange(ref taken, 1) == 0;
        }));
        Assert.True(firstInside.Wait(TimeSpan.FromSeconds(30)));
        Task second = Task.Run(() =>
        {
            OnboardingState.DuringInstall(storage, false, () => Interlocked.Exchange(ref taken, 1) == 0);
            secondDone.Set();
        });
        await Task.WhenAll(first, second);
        // The items are there, whichever install added them: the user is still in the guide.
        Assert.Equal(OnboardingState.Pending, OnboardingState.Load(storage));
    }

    [Fact]
    public void A_new_user_seen_with_chats_is_saved_as_past_the_first_run()
    {
        FakeStorage fresh = new();
        Assert.Equal(OnboardingState.New, OnboardingState.Observe(fresh, false));
        Assert.Equal(0, fresh.Writes);
        Assert.Equal(OnboardingState.Completed, OnboardingState.Observe(fresh, true));
        // Deleting every chat afterwards does not start a first run.
        Assert.False(OnboardingState.ShouldShow(OnboardingState.Observe(fresh, false), false));
        Assert.Equal(1, fresh.Writes);
        FakeStorage running = new();
        OnboardingState.Apply(running, OnboardingState.ActionRun, out _, out _);
        Assert.Equal(OnboardingState.Pending, OnboardingState.Observe(running, true));
    }

    [Theory]
    [InlineData(null, OnboardingState.Completed)]
    [InlineData(OnboardingState.ActionRun, OnboardingState.Completed)]
    [InlineData(OnboardingState.ActionSkip, OnboardingState.Skipped)]
    [InlineData(OnboardingState.ActionComplete, OnboardingState.Completed)]
    public void A_message_ends_a_first_or_a_running_guide(string before, string after)
    {
        FakeStorage storage = new();
        if (before is not null)
        {
            OnboardingState.Apply(storage, before, out _, out _);
        }
        OnboardingState.OnMessage(storage);
        int writes = storage.Writes;
        OnboardingState.OnMessage(storage);
        Assert.Equal(after, OnboardingState.Load(storage));
        Assert.Equal(writes, storage.Writes);
    }

    // ---- The checklist ----

    private static Dictionary<string, ReadinessCheck> Evaluate(ReadinessInputs setup, int embedders = 0, bool canAdd = true)
        => OnboardingChecks.Evaluate(new OnboardingInputs(setup, embedders, canAdd)).ToDictionary(c => c.Id);

    [Fact]
    public void With_no_backend_it_says_where_to_add_one_and_names_no_engine_or_provider()
    {
        ReadinessInputs none = new();
        Dictionary<string, ReadinessCheck> checks = Evaluate(none);
        Assert.Equal(["backend", "model", "tools", "embeddings"], checks.Keys);
        Assert.Equal(CheckStatus.Fail, checks["backend"].Status);
        Assert.Contains("Server > Backends", checks["backend"].Fix);
        Assert.Equal(CheckStatus.Info, checks["model"].Status);
        Assert.False(OnboardingChecks.CanChat(new OnboardingInputs(none, 0, true)));
        Assert.Contains("administrator", Evaluate(none, canAdd: false)["backend"].Fix);
        string text = string.Join(" ", checks.Values.Select(c => $"{c.Detail} {c.Fix}"));
        foreach (string name in new[] { "OpenAI", "Anthropic", "Claude", "Ollama", "Hartsy", "GGUF", "llama" })
        {
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_backend_with_a_model_is_enough_to_chat_without_tools_or_embeddings()
    {
        ReadinessInputs ready = new() { Providers = 1, Models = 2 };
        Dictionary<string, ReadinessCheck> checks = Evaluate(ready);
        Assert.Equal(CheckStatus.Ok, checks["backend"].Status);
        Assert.Equal(CheckStatus.Ok, checks["model"].Status);
        Assert.Equal(CheckStatus.Info, checks["tools"].Status);
        Assert.Equal(CheckStatus.Info, checks["embeddings"].Status);
        Assert.True(OnboardingChecks.CanChat(new OnboardingInputs(ready, 0, true)));
    }

    [Fact]
    public void A_backend_with_no_model_fails_and_a_missing_preferred_model_warns()
    {
        Assert.Equal(CheckStatus.Fail, Evaluate(new() { Providers = 1 })["model"].Status);
        ReadinessInputs gone = new() { Providers = 1, Models = 1, PreferredModel = "old", PreferredModelFound = false };
        Assert.Equal(CheckStatus.Warn, Evaluate(gone)["model"].Status);
        Assert.True(OnboardingChecks.CanChat(new OnboardingInputs(gone, 0, true)));
    }

    [Fact]
    public void Tools_count_only_what_the_user_can_run_and_embeddings_follow_what_is_set_and_offered()
    {
        ReadinessInputs denied = new() { Providers = 1, Models = 1, ToolsEnabled = 2, ToolsDenied = 1, ToolsWithoutHandler = ["ghost"] };
        Assert.Equal(CheckStatus.Info, Evaluate(denied)["tools"].Status);
        Assert.Equal(CheckStatus.Ok, Evaluate(new() { ToolsEnabled = 3, ToolsDenied = 1 })["tools"].Status);
        Assert.Contains("Settings > Knowledge", Evaluate(new() { Providers = 1 }, embedders: 1)["embeddings"].Detail);
        Assert.Contains("No backend offers embeddings", Evaluate(new() { Providers = 1 })["embeddings"].Detail);
        Assert.Equal(CheckStatus.Ok, Evaluate(new() { EmbeddingSet = true })["embeddings"].Status);
    }

    [Fact]
    public void A_part_that_could_not_be_looked_at_is_a_warning_and_not_a_guess()
    {
        ReadinessInputs broken = new() { Unchecked = [new ReadinessCheck("backends", "LLM backends", CheckStatus.Warn, "Could not be checked.")] };
        Dictionary<string, ReadinessCheck> checks = Evaluate(broken);
        Assert.Equal(CheckStatus.Warn, checks["backend"].Status);
        Assert.Equal(CheckStatus.Warn, checks["model"].Status);
        Assert.False(OnboardingChecks.CanChat(new OnboardingInputs(broken, 0, true)));
    }
}
