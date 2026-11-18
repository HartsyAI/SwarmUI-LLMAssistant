using System.IO;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The line diff, and the journal of what the file tools changed: baselines, the net change, and putting files back.</summary>
public sealed class ChangeJournalTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "llma-journal-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _journalFolder;
    private readonly Workspace _workspace;
    private readonly ChangeRecorder _recorder;

    public ChangeJournalTests()
    {
        string root = Path.Combine(_base, "ws");
        Directory.CreateDirectory(root);
        _journalFolder = Path.Combine(_base, "journal");
        _workspace = new Workspace("user:test", "test", root, true);
        _recorder = new ChangeRecorder(_journalFolder, _workspace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the temp folder cleanup.
        }
    }

    private string Full(string relative) => Path.Combine(_workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string relative, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
        File.WriteAllText(Full(relative), text, new UTF8Encoding(false));
    }

    private static JObject Args(params (string Name, object Value)[] pairs)
    {
        JObject args = [];
        foreach ((string name, object value) in pairs)
        {
            args[name] = JToken.FromObject(value);
        }
        return args;
    }

    private Task<JObject> Write(string path, string content, bool overwrite = false)
        => CodeWriteTool.Run(_workspace, Args(("path", path), ("content", content), ("overwrite", overwrite)), CancellationToken.None, _recorder);

    private Task<JObject> Edit(string path, string oldText, string newText)
        => CodeEditTool.Run(_workspace, Args(("path", path), ("oldText", oldText), ("newText", newText)), CancellationToken.None, _recorder);

    private List<ChangeJournalService.FileChange> Changes(bool withDiff = false) => ChangeJournalService.List(_journalFolder, _workspace, withDiff);

    // ---- the diff -----------------------------------------------------------------------------------------

    [Fact]
    public void EqualTextsHaveNoDiff()
    {
        TextDiff.Result result = TextDiff.Compare("a\nb\n", "a\nb\n");

        Assert.Equal("", result.Text);
        Assert.Equal(0, result.Added + result.Removed);
    }

    [Fact]
    public void AnEndingDifferenceAloneIsNotAChange()
    {
        Assert.Equal("", TextDiff.Compare("a\r\nb\r\n", "a\nb\n").Text);
    }

    [Fact]
    public void ADiffShowsEachChangeInItsOwnHunkWithContext()
    {
        string before = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n";
        string after = before.Replace("line 3\n", "line three\n").Replace("line 27\n", "line twenty-seven\n");

        TextDiff.Result result = TextDiff.Compare(before, after);

        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.Removed);
        string[] hunks = result.Text.Split('\n').Where(l => l.StartsWith("@@")).ToArray();
        Assert.Equal(2, hunks.Length);
        Assert.Contains("-line 3", result.Text);
        Assert.Contains("+line three", result.Text);
        // Three lines of context around a change, and nothing from the long unchanged stretch between them.
        Assert.Contains(" line 6", result.Text);
        Assert.DoesNotContain(" line 15", result.Text);
    }

    [Fact]
    public void AddedAndRemovedLinesAreCountedAndMarked()
    {
        TextDiff.Result result = TextDiff.Compare("one\ntwo\nthree\n", "one\nthree\nfour\n");

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Contains("-two", result.Text);
        Assert.Contains("+four", result.Text);
        Assert.StartsWith("@@ -1,3 +1,3 @@", result.Text);
    }

    [Fact]
    public void ANewFileIsAllAdditions()
    {
        TextDiff.Result result = TextDiff.Compare("", "a\nb\n");

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.StartsWith("@@ -0,0 +1,2 @@", result.Text);
    }

    [Fact]
    public void AHugeChangeIsCutButStillCounted()
    {
        string after = string.Join("\n", Enumerable.Range(0, TextDiff.MaxDiffLines + 500).Select(i => $"row {i}"));

        TextDiff.Result result = TextDiff.Compare("", after);

        Assert.True(result.Truncated);
        Assert.Equal(TextDiff.MaxDiffLines + 500, result.Added);
        Assert.True(result.Text.Split('\n').Length <= TextDiff.MaxDiffLines);
    }

    // ---- the journal --------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewFileIsListedAsCreatedAndRevertingDeletesIt()
    {
        Assert.True((await Write("src/new.txt", "hello\n")).Value<bool>("success"));

        ChangeJournalService.FileChange change = Assert.Single(Changes(withDiff: true));
        Assert.Equal("src/new.txt", change.Path);
        Assert.Equal("created", change.Status);
        Assert.Equal(1, change.Added);
        Assert.True(change.Revertable);
        Assert.Contains("+hello", change.Diff);

        ChangeJournalService.RevertResult result = ChangeJournalService.Revert(_journalFolder, _workspace, "src/new.txt", force: false);

        Assert.Equal(["src/new.txt"], result.Reverted);
        Assert.False(File.Exists(Full("src/new.txt")));
        Assert.Empty(Changes());
    }

    [Fact]
    public async Task SeveralEditsToOneFileAreOneChangeAgainstTheOriginal()
    {
        Put("a.txt", "alpha\nbeta\ngamma\n");

        await Edit("a.txt", "beta", "BETA");
        await Edit("a.txt", "gamma", "GAMMA");

        ChangeJournalService.FileChange change = Assert.Single(Changes(withDiff: true));
        Assert.Equal("modified", change.Status);
        Assert.Equal(2, change.Added);
        Assert.Equal(2, change.Removed);
        Assert.Contains("-beta", change.Diff);
        Assert.Contains("+GAMMA", change.Diff);

        ChangeJournalService.Revert(_journalFolder, _workspace, "a.txt", force: false);

        Assert.Equal("alpha\nbeta\ngamma\n", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public async Task AFileEditedBackToItsOriginalIsNoChange()
    {
        Put("a.txt", "one\ntwo\n");

        await Edit("a.txt", "two", "2");
        await Edit("a.txt", "2", "two");

        Assert.Empty(Changes());
    }

    [Fact]
    public async Task ARevertKeepsTheOriginalBytesIncludingAByteOrderMark()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("keep\r\nme\r\n")];
        Directory.CreateDirectory(_workspace.Root);
        File.WriteAllBytes(Full("bom.txt"), original);

        await Write("bom.txt", "replaced\n", overwrite: true);
        ChangeJournalService.Revert(_journalFolder, _workspace, "bom.txt", force: false);

        Assert.Equal(original, File.ReadAllBytes(Full("bom.txt")));
    }

    [Fact]
    public async Task AFileChangedSinceTheAssistantWroteItIsLeftAloneUnlessForced()
    {
        Put("a.txt", "start\n");
        await Edit("a.txt", "start", "assistant");
        // Someone else edits it afterwards.
        File.WriteAllText(Full("a.txt"), "assistant\nplus the user's own line\n");

        ChangeJournalService.FileChange change = Assert.Single(Changes());
        Assert.True(change.Conflict);

        ChangeJournalService.RevertResult held = ChangeJournalService.Revert(_journalFolder, _workspace, "a.txt", force: false);
        Assert.Equal(["a.txt"], held.Conflicts);
        Assert.Empty(held.Reverted);
        Assert.Contains("user's own line", File.ReadAllText(Full("a.txt")));

        ChangeJournalService.RevertResult forced = ChangeJournalService.Revert(_journalFolder, _workspace, "a.txt", force: true);
        Assert.Equal(["a.txt"], forced.Reverted);
        Assert.Equal("start\n", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public async Task RevertingAllPutsEveryFileBackAndLeavesConflictsForTheUser()
    {
        Put("a.txt", "a1\n");
        Put("b.txt", "b1\n");
        await Edit("a.txt", "a1", "a2");
        await Edit("b.txt", "b1", "b2");
        await Write("c.txt", "new\n");
        File.WriteAllText(Full("b.txt"), "b2 and more\n");

        ChangeJournalService.RevertResult result = ChangeJournalService.Revert(_journalFolder, _workspace, onlyPath: null, force: false);

        Assert.Equal(["a.txt", "c.txt"], result.Reverted.Order().ToArray());
        Assert.Equal(["b.txt"], result.Conflicts);
        Assert.Equal("a1\n", File.ReadAllText(Full("a.txt")));
        Assert.False(File.Exists(Full("c.txt")));
        Assert.Single(Changes());
    }

    [Fact]
    public async Task OnlyTheFirstVersionOfAFileIsKept()
    {
        Put("a.txt", "original\n");

        await Write("a.txt", "second\n", overwrite: true);
        await Write("a.txt", "third\n", overwrite: true);
        ChangeJournalService.Revert(_journalFolder, _workspace, "a.txt", force: false);

        Assert.Equal("original\n", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public async Task AFileTooLargeToKeepIsListedButCannotBeReverted()
    {
        File.WriteAllText(Full("big.txt"), new string('x', (int)ChangeJournalService.MaxBaselineBytes + 10));

        await Write("big.txt", "small now\n", overwrite: true);

        ChangeJournalService.FileChange change = Assert.Single(Changes());
        Assert.False(change.Revertable);
        ChangeJournalService.RevertResult result = ChangeJournalService.Revert(_journalFolder, _workspace, "big.txt", force: false);
        Assert.Single(result.Failed);
        Assert.Equal("small now\n", File.ReadAllText(Full("big.txt")));
    }

    [Fact]
    public async Task ADiffIsNotDrawnForALargeFile()
    {
        await Write("huge.txt", new string('y', (int)ChangeJournalService.MaxDiffBytes + 100));

        ChangeJournalService.FileChange change = Assert.Single(Changes(withDiff: true));

        Assert.True(change.Large);
        Assert.Null(change.Diff);
        Assert.True(change.Revertable);
    }

    [Fact]
    public async Task AJournalForAnotherWorkspaceIsDiscarded()
    {
        await Write("a.txt", "x\n");
        Workspace other = new("user:other", "other", Path.Combine(_base, "other"), true);
        Directory.CreateDirectory(other.Root);

        Assert.Empty(ChangeJournalService.List(_journalFolder, other));
    }

    [Fact]
    public async Task AFailingJournalNeverStopsTheEdit()
    {
        // A file where the journal's folder should be makes every journal write fail.
        string blocked = Path.Combine(_base, "blocked");
        File.WriteAllText(blocked, "not a folder");
        ChangeRecorder broken = new(blocked, _workspace);

        JObject result = await CodeWriteTool.Run(_workspace, Args(("path", "ok.txt"), ("content", "still written\n")), CancellationToken.None, broken);

        Assert.True(result.Value<bool>("success"), result.ToString());
        Assert.Equal("still written\n", File.ReadAllText(Full("ok.txt")));
    }

    [Fact]
    public void AThreadIdThatIsNotASafeFolderNameHasNoJournal()
    {
        Assert.Null(ChangeJournalService.FolderFor(null, "abc"));
    }
}
