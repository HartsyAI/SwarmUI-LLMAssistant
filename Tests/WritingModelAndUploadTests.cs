using System.Collections.Concurrent;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Memory;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>What the writing model of a prompt rewrite is told besides the image style (its chat-model style and the user's style notes), what a backend says about tool calling, and the removal of a refused message's upload.</summary>
public class WritingModelAndUploadTests : IDisposable
{
    private sealed class Storage : IMemoryStorage
    {
        public readonly ConcurrentDictionary<string, string> Values = new();
        public string Get(string key) => Values.TryGetValue(key, out string v) ? v : null;
        public void Save(string key, string value) => Values[key] = value;
        public void Delete(string key) => Values.TryRemove(key, out _);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmassistant-unsent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly RewriteSettings Settings = new("", 5, 2048, 0.4);

    private static PromptStyle Style(string negative = "none") => new("s", "image", "s", "d", [], "text", "", negative, [], [], "", "Write a prompt.", "bundled");

    private static RewriteRequest Request(string addendum = "", string notes = "", string negative = "", bool fresh = true)
        => new("a lighthouse", negative, "img.safetensors", 0, 0, "m", "", fresh, addendum, notes);

    // ---- The rewrite's instructions ----

    [Fact]
    public void The_addendum_and_the_notes_sit_between_the_style_and_the_rules()
    {
        string system = PromptRewriter.BuildSystem(Style(), Request("ADDENDUM TEXT", "NOTES BLOCK\n"), json: false);

        int style = system.IndexOf("Write a prompt.", StringComparison.Ordinal);
        int addendum = system.IndexOf("ADDENDUM TEXT", StringComparison.Ordinal);
        int notes = system.IndexOf("NOTES BLOCK", StringComparison.Ordinal);
        int rules = system.IndexOf("Rules for this task:", StringComparison.Ordinal);
        Assert.True(style == 0 && style < addendum && addendum < notes && notes < rules, system);
    }

    [Fact]
    public void With_neither_the_instructions_are_what_they_were()
    {
        string system = PromptRewriter.BuildSystem(Style(), Request(), json: false);

        Assert.StartsWith("Write a prompt.\n\nRules for this task:", system);
    }

    [Fact]
    public async Task The_negative_prompt_request_gets_the_addendum_but_not_the_notes()
    {
        List<string> systems = [];
        Task<LLMGenerateResult> Generate(string system, string user, CancellationToken ct)
        {
            systems.Add(system);
            return Task.FromResult(LLMGenerateResult.FromText("a tall lighthouse"));
        }

        await PromptRewriter.RunAsync(Style(PromptStyle.NegativeSeparate), Request("ADDENDUM TEXT", "NOTES BLOCK", "blurry"), Settings, Generate, CancellationToken.None);

        Assert.Equal(2, systems.Count);
        Assert.EndsWith("ADDENDUM TEXT", systems[1]);
        Assert.DoesNotContain("NOTES BLOCK", systems[1]);
    }

    [Fact]
    public async Task Changed_notes_or_a_changed_addendum_are_not_answered_from_the_cache()
    {
        int calls = 0;
        Task<LLMGenerateResult> Generate(string system, string user, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(LLMGenerateResult.FromText("a tall lighthouse"));
        }
        string userId = "rewrite-notes-" + Guid.NewGuid().ToString("N");

        foreach ((string addendum, string notes) in new[] { ("", ""), ("", "likes film grain"), ("", "likes watercolor"), ("short answers", "likes watercolor") })
        {
            await PromptRewriteService.RewriteWithStyleAsync(userId, Style(), Request(addendum, notes, fresh: false), Settings, Generate, CancellationToken.None);
        }

        Assert.Equal(4, calls);
    }

    // ---- Style notes ----

    private static MemoryStore Notes(params (string Category, string Text)[] notes)
    {
        MemoryStore store = new(new Storage());
        foreach ((string category, string text) in notes)
        {
            Assert.True(store.Write(category, text, MemoryOrigins.User, "").Saved);
        }
        return store;
    }

    [Fact]
    public void Only_style_notes_reach_the_rewrite_and_they_count_as_used()
    {
        MemoryStore store = Notes(("style", "Likes film grain <b>always</b>"), ("fact", "Lives in Lyon"), ("preference", "Prefers tea"), ("preferred_name", "Sam"));

        string block = MemoryService.StyleNotesFrom(store, MemoryControls.Default, "a lighthouse");

        Assert.Contains("- [style] Likes film grain ＜b＞always＜/b＞", block);
        Assert.Contains("<style_notes>", block);
        Assert.DoesNotContain("Lyon", block);
        Assert.DoesNotContain("tea", block);
        Assert.DoesNotContain("Sam", block);
        Assert.Equal(["style"], store.Load().Notes.Where(n => n.LastUsedAt is not null).Select(n => n.Kind));
    }

    [Fact]
    public void Paused_recall_sends_no_note_and_marks_none_used()
    {
        MemoryStore store = Notes(("style", "Likes film grain"));

        Assert.Equal("", MemoryService.StyleNotesFrom(store, new MemoryControls(false, true, 0, false), "a lighthouse"));
        Assert.All(store.Load().Notes, n => Assert.Null(n.LastUsedAt));
        // Paused saving and the admin's extractor switch do not stop saved notes being read.
        Assert.Contains("film grain", MemoryService.StyleNotesFrom(store, new MemoryControls(true, false, 0, true), "a lighthouse"));
    }

    [Fact]
    public void No_style_note_means_no_block()
        => Assert.Equal("", MemoryService.StyleNotesFrom(Notes(("fact", "Lives in Lyon")), MemoryControls.Default, "a lighthouse"));

    // ---- The task a one-shot request is ----

    [Theory]
    [InlineData(null, "chat")]
    [InlineData("chat", "chat")]
    [InlineData("vision", "vision")]
    [InlineData("caption", "caption")]
    [InlineData("prompt", "rewrite")]
    [InlineData("video-prompt", "rewrite")]
    [InlineData("randomprompt", "chat")]
    [InlineData("my-own-instruction", "chat")]
    public void A_one_shot_request_is_the_task_of_its_instruction(string instructionId, string task)
        => Assert.Equal([task], LlmAddendumService.TasksForInstruction(instructionId));

    // ---- Tool calling ----

    [Theory]
    [InlineData("""{"capabilities":{"function_calling":false}}""", false)]
    [InlineData("""{"capabilities":{"function_calling":true}}""", true)]
    [InlineData("""{"supported_parameters":["temperature","tools"]}""", true)]
    [InlineData("""{"supported_parameters":["temperature"]}""", false)]
    [InlineData("""{"supported_parameters":[]}""", null)]
    [InlineData("""{"capabilities":["completion"]}""", null)]
    [InlineData("""{"capabilities":{"function_calling":"no"}}""", null)]
    [InlineData("""{"id":"plain"}""", null)]
    public void A_model_listing_says_whether_the_model_calls_tools_only_when_it_states_it(string json, bool? expected)
        => Assert.Equal(expected, RemoteOpenAILLMProvider.ToolSupportOf(JObject.Parse(json)));

    [Fact]
    public void Only_a_turn_with_tools_on_a_model_that_says_no_is_warned_about()
    {
        List<JObject> tools = [new JObject { ["id"] = "web_search" }];

        Assert.Equal("m", TurnSetupService.ToolsUnsupportedBy(new LLMModelInfo { Id = "m", SupportsTools = false }, tools));
        Assert.Null(TurnSetupService.ToolsUnsupportedBy(new LLMModelInfo { Id = "m", SupportsTools = false }, []));
        Assert.Null(TurnSetupService.ToolsUnsupportedBy(new LLMModelInfo { Id = "m", SupportsTools = true }, tools));
        Assert.Null(TurnSetupService.ToolsUnsupportedBy(new LLMModelInfo { Id = "m" }, tools));
        Assert.Null(TurnSetupService.ToolsUnsupportedBy(null, tools));
    }

    // ---- The upload of a refused message ----

    private string Upload(string thread, string name)
    {
        string path = Path.GetFullPath(Path.Combine(_dir, thread, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public void A_refused_messages_files_go_and_no_other()
    {
        string image = Upload("t1", "m1.png");
        string video = Upload("t1", "m1.mp4");
        string other = Upload("t1", "m2.png");
        string longer = Upload("t1", "m10.png");
        string elsewhere = Upload("t2", "m1.png");

        Assert.Equal(2, MediaStorageService.DiscardUnsentIn(_dir, "t1", "m1", () => []));

        Assert.False(File.Exists(image));
        Assert.False(File.Exists(video));
        Assert.True(File.Exists(other) && File.Exists(longer) && File.Exists(elsewhere));
    }

    [Fact]
    public void A_file_a_stored_message_refers_to_is_kept()
    {
        string image = Upload("t1", "m1.png");

        Assert.Equal(0, MediaStorageService.DiscardUnsentIn(_dir, "t1", "m1", () => [image.Replace('\\', '/')]));

        Assert.True(File.Exists(image));
    }

    [Theory]
    [InlineData("t1", null)]
    [InlineData("t1", "")]
    [InlineData("t1", "../..")]
    [InlineData(null, "m1")]
    [InlineData("t1", "nothing")]
    public void With_nothing_to_find_nothing_is_deleted_and_the_references_are_not_read(string thread, string message)
    {
        string image = Upload("t1", "m1.png");

        Assert.Equal(0, MediaStorageService.DiscardUnsentIn(_dir, thread, message, () => throw new InvalidOperationException("not needed")));

        Assert.True(File.Exists(image));
    }
}
