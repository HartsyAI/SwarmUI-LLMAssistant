using Hartsy.Extensions.LLMAssistant.Services.Hooks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>How hooks combine on one call, with a fake command runner so no process starts.</summary>
public class HookEngineTests
{
    /// <summary>A command runner that returns a scripted answer and records what it was given.</summary>
    private sealed class FakeCommands(Func<ToolHook, JObject, HookCommandResult> answer = null) : IHookCommandRunner
    {
        public List<(ToolHook Hook, JObject Payload)> Calls { get; } = [];

        public Task<HookCommandResult> RunAsync(ToolHook hook, JObject payload, CancellationToken ct)
        {
            Calls.Add((hook, payload));
            return Task.FromResult(answer?.Invoke(hook, payload) ?? new HookCommandResult());
        }
    }

    private static ToolHook Rule(string name, string action, string tool = "file_write", string argument = "", string matchType = "any", string value = "", string message = "", string phase = "pre", JObject set = null)
        => new() { Id = name, Name = name, Tool = tool, Argument = argument, MatchType = matchType, Value = value, Action = action, Message = message, Phase = phase, Set = set ?? [] };

    private static ToolHook Command(string name, string phase = "pre", string onError = "deny", string tool = "*")
        => new() { Id = name, Name = name, Kind = "command", Command = "x", Phase = phase, OnError = onError, Tool = tool };

    private static HookEngine Engine(FakeCommands commands = null) => new(commands ?? new FakeCommands());

    private static readonly JObject Args = new() { ["path"] = "notes.txt", ["content"] = "hi" };

    [Fact]
    public async Task NoHooksLeavesTheCallAlone()
    {
        PreHookOutcome outcome = await Engine().RunPreAsync([], "file_write", "file_write", Args, CancellationToken.None);

        Assert.False(outcome.Denied);
        Assert.False(outcome.ForceAsk);
        Assert.Equal(Args.ToString(), outcome.Arguments.ToString());
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public async Task ADenyRefusesTheCallWithItsReason()
    {
        ToolHook deny = Rule("no-notes", "deny", argument: "path", matchType: "glob", value: "*.txt", message: "No text files.");

        PreHookOutcome outcome = await Engine().RunPreAsync([deny], "file_write", "file_write", Args, CancellationToken.None);

        Assert.True(outcome.Denied);
        Assert.Equal("No text files.", outcome.Message);
        Assert.Equal("no-notes", outcome.HookName);
    }

    [Fact]
    public async Task ADenyWithoutAMessageStillSaysWhichHookBlockedIt()
    {
        PreHookOutcome outcome = await Engine().RunPreAsync([Rule("guard", "deny")], "file_write", "file_write", Args, CancellationToken.None);

        Assert.Contains("guard", outcome.Message);
    }

    [Fact]
    public async Task AHookForAnotherToolOrAnArgumentThatDoesNotMatchDoesNothing()
    {
        ToolHook otherTool = Rule("a", "deny", tool: "shell_exec");
        ToolHook otherValue = Rule("b", "deny", argument: "path", matchType: "glob", value: "*.env");

        PreHookOutcome outcome = await Engine().RunPreAsync([otherTool, otherValue], "file_write", "file_write", Args, CancellationToken.None);

        Assert.False(outcome.Denied);
    }

    [Fact]
    public async Task ADisabledHookNeverRuns()
    {
        ToolHook deny = Rule("off", "deny");
        deny.Enabled = false;

        Assert.False((await Engine().RunPreAsync([deny], "file_write", "file_write", Args, CancellationToken.None)).Denied);
    }

    [Fact]
    public async Task TheFirstDenyStopsTheRest_SoNoLaterHookOrCommandRuns()
    {
        FakeCommands commands = new();

        PreHookOutcome outcome = await Engine(commands).RunPreAsync([Rule("first", "deny", message: "no"), Command("later")], "file_write", "file_write", Args, CancellationToken.None);

        Assert.True(outcome.Denied);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task AskForcesTheUserToBeAsked()
    {
        PreHookOutcome outcome = await Engine().RunPreAsync([Rule("careful", "ask")], "file_write", "file_write", Args, CancellationToken.None);

        Assert.False(outcome.Denied);
        Assert.True(outcome.ForceAsk);
    }

    [Fact]
    public async Task ARewriteChangesTheArguments_WithoutTouchingTheCallersObject()
    {
        ToolHook rewrite = Rule("safe", "rewrite", set: new JObject { ["overwrite"] = false, ["path"] = "sandbox/notes.txt" });
        JObject original = (JObject)Args.DeepClone();

        PreHookOutcome outcome = await Engine().RunPreAsync([rewrite], "file_write", "file_write", original, CancellationToken.None);

        Assert.Equal("sandbox/notes.txt", outcome.Arguments["path"]?.ToString());
        Assert.False(outcome.Arguments["overwrite"].Value<bool>());
        Assert.Equal("hi", outcome.Arguments["content"]?.ToString());
        Assert.Equal(Args.ToString(), original.ToString());
    }

    [Fact]
    public async Task RewritesApplyInOrder_SoALaterHookSeesWhatAnEarlierOneProduced()
    {
        ToolHook first = Rule("first", "rewrite", set: new JObject { ["path"] = "moved.txt" });
        // Only matches after the first hook has rewritten the path.
        ToolHook second = Rule("second", "deny", argument: "path", matchType: "glob", value: "moved.*", message: "saw the rewrite");

        PreHookOutcome outcome = await Engine().RunPreAsync([first, second], "file_write", "file_write", Args, CancellationToken.None);

        Assert.True(outcome.Denied);
        Assert.Equal("saw the rewrite", outcome.Message);
    }

    [Fact]
    public async Task NotesPileUpInOrder_AndBlankOnesAreDropped()
    {
        PreHookOutcome outcome = await Engine().RunPreAsync([Rule("a", "note", message: "first"), Rule("b", "note", message: "  "), Rule("c", "note", message: "third")], "file_write", "file_write", Args, CancellationToken.None);

        Assert.Equal(["first", "third"], outcome.Notes);
    }

    [Fact]
    public async Task APostHookAddsItsNoteOnlyWhenItApplies()
    {
        ToolHook applies = Rule("review", "note", message: "Review the file.", phase: "post");
        ToolHook elsewhere = Rule("other", "note", message: "never", phase: "post", tool: "shell_exec");
        ToolHook pre = Rule("pre", "note", message: "pre note");

        List<string> notes = await Engine().RunPostAsync([applies, elsewhere, pre], "file_write", "file_write", Args, new JObject { ["success"] = true }, CancellationToken.None);

        Assert.Equal(["Review the file."], notes);
    }

    [Fact]
    public async Task ACommandHookIsGivenTheCallAsJson()
    {
        FakeCommands commands = new();

        await Engine(commands).RunPreAsync([Command("c")], "file_write", "file_write", Args, CancellationToken.None);

        JObject payload = Assert.Single(commands.Calls).Payload;
        Assert.Equal("pre", payload["phase"]?.ToString());
        Assert.Equal("file_write", payload["tool"]?.ToString());
        Assert.Equal("notes.txt", payload["arguments"]?["path"]?.ToString());
    }

    [Fact]
    public async Task APostCommandHookIsAlsoGivenTheResult()
    {
        FakeCommands commands = new((_, _) => new HookCommandResult { Note = "scanned" });

        List<string> notes = await Engine(commands).RunPostAsync([Command("c", phase: "post")], "file_write", "file_write", Args, new JObject { ["success"] = true }, CancellationToken.None);

        Assert.Equal(["scanned"], notes);
        Assert.True(commands.Calls[0].Payload["result"]?["success"]?.Value<bool>());
    }

    [Fact]
    public async Task ACommandCanDenyAskRewriteAndNote()
    {
        HookCommandResult deny = new() { Decision = "deny", Message = "script says no" };
        Assert.Equal("script says no", (await Engine(new FakeCommands((_, _) => deny)).RunPreAsync([Command("c")], "file_write", "file_write", Args, CancellationToken.None)).Message);

        HookCommandResult both = new() { Decision = "ask", Arguments = new JObject { ["path"] = "elsewhere.txt" }, Note = "moved it" };
        PreHookOutcome outcome = await Engine(new FakeCommands((_, _) => both)).RunPreAsync([Command("c")], "file_write", "file_write", Args, CancellationToken.None);
        Assert.True(outcome.ForceAsk);
        Assert.Equal("elsewhere.txt", outcome.Arguments["path"]?.ToString());
        Assert.Equal(["moved it"], outcome.Notes);
    }

    [Fact]
    public async Task ACommandThatCannotRunRefusesTheCallWhenItIsSetToDeny_AndLetsItThroughWhenSetToAllow()
    {
        FakeCommands broken = new((_, _) => new HookCommandResult { Ok = false, Error = "crashed" });

        PreHookOutcome closed = await Engine(broken).RunPreAsync([Command("guard", onError: "deny")], "file_write", "file_write", Args, CancellationToken.None);
        Assert.True(closed.Denied);
        Assert.Contains("guard", closed.Message);
        // The reason a hook failed is for the log, not for the model.
        Assert.DoesNotContain("crashed", closed.Message);

        PreHookOutcome open = await Engine(broken).RunPreAsync([Command("guard", onError: "allow")], "file_write", "file_write", Args, CancellationToken.None);
        Assert.False(open.Denied);
    }

    [Fact]
    public async Task APostCommandThatFailsIsSkippedAndNeverBreaksTheTurn()
    {
        FakeCommands broken = new((_, _) => new HookCommandResult { Ok = false, Error = "crashed" });

        List<string> notes = await Engine(broken).RunPostAsync([Command("c", phase: "post")], "file_write", "file_write", Args, new JObject(), CancellationToken.None);

        Assert.Empty(notes);
    }

    [Fact]
    public async Task ACommandHookOnlyRunsForItsTool()
    {
        FakeCommands commands = new();

        await Engine(commands).RunPreAsync([Command("c", tool: "shell_exec")], "file_write", "file_write", Args, CancellationToken.None);

        Assert.Empty(commands.Calls);
    }
}
