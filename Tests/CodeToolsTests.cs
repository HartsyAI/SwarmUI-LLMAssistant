using System.IO;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The code tools against a real folder: reading, writing, editing, searching and running, and that none of them leaves the workspace.</summary>
public sealed class CodeToolsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "llma-code-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Workspace _workspace;

    public CodeToolsTests()
    {
        string root = Path.Combine(_base, "ws");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(_base, "outside"));
        File.WriteAllText(Path.Combine(_base, "outside", "secret.txt"), "secret");
        _workspace = new Workspace("user:test", "test", root, true);
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

    private string Path_(string relative) => Path.Combine(_workspace.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string relative, string text, Encoding encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path_(relative))!);
        File.WriteAllText(Path_(relative), text, encoding ?? new UTF8Encoding(false));
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

    private Task<JObject> Read(params (string, object)[] a) => CodeReadTool.Run(_workspace, Args(a), CancellationToken.None);

    private Task<JObject> Write(params (string, object)[] a) => CodeWriteTool.Run(_workspace, Args(a), CancellationToken.None);

    private Task<JObject> Edit(params (string, object)[] a) => CodeEditTool.Run(_workspace, Args(a), CancellationToken.None);

    private Task<JObject> Glob(params (string, object)[] a) => CodeGlobTool.Run(_workspace, Args(a), CancellationToken.None);

    private Task<JObject> Grep(params (string, object)[] a) => CodeGrepTool.Run(_workspace, Args(a), CancellationToken.None);

    private static void Ok(JObject result) => Assert.True(result.Value<bool>("success"), result.ToString());

    private static string Failed(JObject result)
    {
        Assert.False(result.Value<bool>("success"), result.ToString());
        return result.Value<string>("error")!;
    }

    // ---- read ----

    [Fact]
    public async Task ReadReturnsTheTextAndItsLineRange()
    {
        Put("a.txt", string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")));
        JObject whole = await Read(("path", "a.txt"));
        Ok(whole);
        Assert.Equal(1, whole.Value<int>("startLine"));
        Assert.Equal(10, whole.Value<int>("endLine"));
        Assert.Equal(10, whole.Value<int>("totalLines"));
        Assert.False(whole.Value<bool>("truncated"));

        JObject part = await Read(("path", "a.txt"), ("offset", 4), ("limit", 3));
        Assert.Equal("line 4\nline 5\nline 6\n", part.Value<string>("content"));
        Assert.Equal(6, part.Value<int>("endLine"));
        Assert.True(part.Value<bool>("truncated"));
        Assert.Equal(10, part.Value<int>("totalLines"));
    }

    [Fact]
    public async Task ReadRefusesWhatIsNotAFileInTheWorkspace()
    {
        Put("bin.dat", "ab\0cd");
        Assert.Contains("binary", Failed(await Read(("path", "bin.dat"))));
        Directory.CreateDirectory(Path_("folder"));
        Assert.Contains("folder", Failed(await Read(("path", "folder"))));
        Assert.Contains("not found", Failed(await Read(("path", "missing.txt"))));
        Assert.Contains("outside", Failed(await Read(("path", Path.Combine(_base, "outside", "secret.txt")))));
        Assert.Contains("..", Failed(await Read(("path", "../outside/secret.txt"))));
    }

    [Fact]
    public async Task ReadNeverSkipsALineInTheMiddleOfTheRangeItReturns()
    {
        // A line too long to fit, then short ones: nothing after the long line may be taken, or the range would have a hole in it.
        Put("wide.txt", "first\n" + new string('x', CodeReadTool.MaxChars) + "\nshort one\nshort two\n");
        JObject result = await Read(("path", "wide.txt"));
        Ok(result);
        Assert.Equal("first\n", result.Value<string>("content"));
        Assert.Equal(1, result.Value<int>("endLine"));
        Assert.True(result.Value<bool>("truncated"));
        Assert.Equal(4, result.Value<int>("totalLines"));
        // The model can pick up after it.
        JObject next = await Read(("path", "wide.txt"), ("offset", 3));
        Assert.StartsWith("short one\nshort two\n", next.Value<string>("content"));
    }

    [Fact]
    public async Task ReadCanReadAFileAnotherProgramHasOpenForWriting()
    {
        Put("live.log", "one\ntwo\n");
        using FileStream writer = new(Path_("live.log"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        JObject result = await Read(("path", "live.log"));
        Ok(result);
        Assert.Equal("one\ntwo\n", result.Value<string>("content"));
    }

    [Fact]
    public async Task SearchesNeverReadAFileThroughALinkEvenOneThatLeadsOutside()
    {
        string link = Path_("sneaky.txt");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_base, "outside", "secret.txt"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Windows only lets a user make a file link with privileges; the folder-link case in the sandbox tests covers the walk on every host.
            return;
        }
        Put("real.txt", "secret in a real file");
        JObject grep = await Grep(("pattern", "secret"));
        Assert.Equal(["real.txt"], ((JArray)grep["matches"]!).Select(m => m.Value<string>("file")));
        Assert.DoesNotContain("sneaky.txt", ((JArray)(await Glob(("pattern", "*.txt")))["files"]!).Select(f => f.ToString()));
        // Reading it directly goes through the sandbox, which follows the link and refuses.
        Assert.Contains("link", Failed(await Read(("path", "sneaky.txt"))));
    }

    [Fact]
    public async Task ReplacingAFileKeepsItsPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Put("run.sh", "#!/bin/sh\necho old\n");
        File.SetUnixFileMode(Path_("run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Ok(await Edit(("path", "run.sh"), ("oldText", "old"), ("newText", "new")));
        Assert.True(File.GetUnixFileMode(Path_("run.sh")).HasFlag(UnixFileMode.UserExecute));
        Ok(await Write(("path", "run.sh"), ("content", "#!/bin/sh\necho again\n"), ("overwrite", true)));
        Assert.True(File.GetUnixFileMode(Path_("run.sh")).HasFlag(UnixFileMode.UserExecute));
    }

    // ---- write ----

    [Fact]
    public async Task WriteCreatesFoldersAndRefusesToReplaceUnlessAsked()
    {
        Ok(await Write(("path", "deep/er/new.txt"), ("content", "hello")));
        Assert.Equal("hello", File.ReadAllText(Path_("deep/er/new.txt")));
        Assert.Contains("overwrite", Failed(await Write(("path", "deep/er/new.txt"), ("content", "again"))));
        Assert.Equal("hello", File.ReadAllText(Path_("deep/er/new.txt")));
        JObject replaced = await Write(("path", "deep/er/new.txt"), ("content", "again"), ("overwrite", true));
        Ok(replaced);
        Assert.False(replaced.Value<bool>("created"));
        Assert.Equal("again", File.ReadAllText(Path_("deep/er/new.txt")));
        Assert.Empty(Directory.GetFiles(Path_("deep/er"), "*.tmp"));
    }

    [Fact]
    public async Task WriteStaysInsideTheWorkspaceAndOutOfGit()
    {
        Assert.Contains("..", Failed(await Write(("path", "../escape.txt"), ("content", "x"))));
        Assert.False(File.Exists(Path.Combine(_base, "escape.txt")));
        Assert.Contains("outside", Failed(await Write(("path", Path.Combine(_base, "outside", "new.txt")), ("content", "x"))));
        Assert.Contains(".git", Failed(await Write(("path", ".git/hooks/pre-commit"), ("content", "calc"))));
        Assert.Contains(".git", Failed(await Write(("path", "sub/.git/config"), ("content", "x"))));
        Assert.Contains("too large", Failed(await Write(("path", "big.txt"), ("content", new string('x', CodeWriteTool.MaxChars + 1)))));
        Assert.Contains("content", Failed(await Write(("path", "x.txt"))));
    }

    // ---- edit ----

    [Fact]
    public async Task EditReplacesExactTextOnceAndShowsWhatChanged()
    {
        Put("f.cs", "class A\n{\n    int x = 1;\n    int y = 2;\n}\n");
        JObject result = await Edit(("path", "f.cs"), ("oldText", "    int x = 1;"), ("newText", "    int x = 10;"));
        Ok(result);
        Assert.Equal("class A\n{\n    int x = 10;\n    int y = 2;\n}\n", File.ReadAllText(Path_("f.cs")));
        Assert.Equal(1, result.Value<int>("replacements"));
        string diff = result.Value<string>("diff")!;
        Assert.Contains("-    int x = 1;", diff);
        Assert.Contains("+    int x = 10;", diff);
        Assert.Contains("@@ line 3 @@", diff);
    }

    [Fact]
    public async Task EditRefusesTextThatIsMissingOrNotUniqueAndSaysHow()
    {
        Put("f.txt", "one\ntwo\none\n");
        Assert.Contains("not found", Failed(await Edit(("path", "f.txt"), ("oldText", "three"), ("newText", "x"))));
        string many = Failed(await Edit(("path", "f.txt"), ("oldText", "one"), ("newText", "1")));
        Assert.Contains("2 times", many);
        Assert.Contains("replaceAll", many);
        Assert.Equal("one\ntwo\none\n", File.ReadAllText(Path_("f.txt")));
        JObject all = await Edit(("path", "f.txt"), ("oldText", "one"), ("newText", "1"), ("replaceAll", true));
        Ok(all);
        Assert.Equal(2, all.Value<int>("replacements"));
        Assert.Equal("1\ntwo\n1\n", File.ReadAllText(Path_("f.txt")));
        Assert.Contains("same", Failed(await Edit(("path", "f.txt"), ("oldText", "two"), ("newText", "two"))));
        Assert.Contains("oldText is required", Failed(await Edit(("path", "f.txt"), ("oldText", ""), ("newText", "x"))));
    }

    [Fact]
    public async Task EditOnAWindowsFileMatchesTextWrittenWithPlainNewlinesAndKeepsTheFilesEndings()
    {
        Put("crlf.txt", "first\r\nsecond\r\nthird\r\n");
        Ok(await Edit(("path", "crlf.txt"), ("oldText", "first\nsecond"), ("newText", "first\nINSERTED\nsecond")));
        string text = File.ReadAllText(Path_("crlf.txt"));
        Assert.Equal("first\r\nINSERTED\r\nsecond\r\nthird\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public async Task EditOnAUnixFileKeepsPlainNewlinesEvenIfTheModelSentWindowsOnes()
    {
        Put("lf.txt", "a\nb\nc\n");
        Ok(await Edit(("path", "lf.txt"), ("oldText", "a\r\nb"), ("newText", "x\r\ny")));
        Assert.Equal("x\ny\nc\n", File.ReadAllText(Path_("lf.txt")));
    }

    [Fact]
    public async Task EditKeepsAByteOrderMarkAndNeverDamagesOtherText()
    {
        Put("bom.txt", "héllo wörld\n", new UTF8Encoding(true));
        Ok(await Edit(("path", "bom.txt"), ("oldText", "wörld"), ("newText", "there")));
        byte[] bytes = File.ReadAllBytes(Path_("bom.txt"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("héllo there\n", new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task EditStaysInsideTheWorkspaceOutOfGitAndOffBinaryFiles()
    {
        Assert.Contains("..", Failed(await Edit(("path", "../outside/secret.txt"), ("oldText", "secret"), ("newText", "x"))));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(_base, "outside", "secret.txt")));
        Put(".git/config", "[core]\n");
        Assert.Contains(".git", Failed(await Edit(("path", ".git/config"), ("oldText", "core"), ("newText", "x"))));
        Put("bin.dat", "ab\0cd");
        Assert.Contains("binary", Failed(await Edit(("path", "bin.dat"), ("oldText", "ab"), ("newText", "x"))));
        Assert.Contains("not found", Failed(await Edit(("path", "nope.txt"), ("oldText", "a"), ("newText", "b"))));
    }

    [Fact]
    public void TheDiffHelpersCountAndReplaceExactly()
    {
        Assert.Equal(3, CodeEditTool.CountOccurrences("aaa", "a"));
        Assert.Equal(1, CodeEditTool.CountOccurrences("aaa", "aa"));
        Assert.Equal(0, CodeEditTool.CountOccurrences("abc", "x"));
        Assert.Equal("Xbab", CodeEditTool.ReplaceFirst("abab", "a", "X"));
        Assert.Equal("a\r\nb", CodeEditTool.NormalizeEndings("a\nb", "\r\n"));
        Assert.Equal("a\nb", CodeEditTool.NormalizeEndings("a\r\nb", "\n"));
    }

    // ---- glob ----

    [Fact]
    public async Task GlobMatchesNamesAnywhereAndFolderPatternsAsAShellDoes()
    {
        Put("a.cs", "");
        Put("src/b.cs", "");
        Put("src/deep/c.cs", "");
        Put("src/d.js", "");
        Put("docs/readme.md", "");
        Put("node_modules/pkg/x.cs", "");
        Put(".git/objects/y.cs", "");

        Assert.Equal(["a.cs", "src/b.cs", "src/deep/c.cs"], ((JArray)(await Glob(("pattern", "*.cs")))["files"]!).Select(f => f.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(["src/b.cs", "src/deep/c.cs"], ((JArray)(await Glob(("pattern", "src/**/*.cs")))["files"]!).Select(f => f.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(["src/b.cs", "src/d.js"], ((JArray)(await Glob(("pattern", "src/*")))["files"]!).Select(f => f.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(["src/deep/c.cs"], ((JArray)(await Glob(("pattern", "*.cs"), ("path", "src/deep")))["files"]!).Select(f => f.ToString()));
        // A pattern with no folder part matches the file name wherever the file is, so ?.cs finds every one-letter .cs name.
        Assert.Equal(["a.cs", "src/b.cs", "src/deep/c.cs"], ((JArray)(await Glob(("pattern", "?.cs")))["files"]!).Select(f => f.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GlobDoesNotGoThroughLinksOrIntoSkippedFoldersAndRefusesBadPatterns()
    {
        Assert.Contains("pattern", Failed(await Glob(("pattern", ""))));
        Assert.Contains("pattern", Failed(await Glob(("pattern", new string('a', PathGlob.MaxPatternLength + 1)))));
        Assert.Contains("..", Failed(await Glob(("pattern", "*"), ("path", "../outside"))));
        Assert.Contains("does not exist", Failed(await Glob(("pattern", "*"), ("path", "nope"))));
    }

    // ---- grep ----

    [Fact]
    public async Task GrepFindsMatchingLinesWithTheirFilesAndNumbers()
    {
        Put("a.cs", "int x;\nvar Needle = 1;\nint y;\n");
        Put("b.txt", "nothing here\nneedle again\n");
        Put("node_modules/p/c.cs", "Needle\n");
        Put("bin.dat", "Needle\0binary");

        JObject sensitive = await Grep(("pattern", "Needle"));
        Ok(sensitive);
        JObject hit = (JObject)((JArray)sensitive["matches"]!).Single();
        Assert.Equal("a.cs", hit.Value<string>("file"));
        Assert.Equal(2, hit.Value<int>("line"));
        Assert.Equal("var Needle = 1;", hit.Value<string>("text"));

        JObject insensitive = await Grep(("pattern", "needle"), ("ignoreCase", true));
        Assert.Equal(2, insensitive.Value<int>("count"));
        JObject filtered = await Grep(("pattern", "needle"), ("ignoreCase", true), ("glob", "*.txt"));
        Assert.Equal("b.txt", ((JObject)((JArray)filtered["matches"]!).Single()).Value<string>("file"));
        JObject oneFile = await Grep(("pattern", "int"), ("path", "a.cs"));
        Assert.Equal(2, oneFile.Value<int>("count"));
    }

    [Fact]
    public async Task GrepCapsItsResultsAndRefusesBadPatternsAndPaths()
    {
        Put("many.txt", string.Concat(Enumerable.Repeat("hit\n", CodeGrepTool.MaxMatches + 50)));
        JObject capped = await Grep(("pattern", "hit"));
        Assert.Equal(CodeGrepTool.MaxMatches, capped.Value<int>("count"));
        Assert.True(capped.Value<bool>("truncated"));
        Assert.Contains("valid regular expression", Failed(await Grep(("pattern", "(unclosed"))));
        Assert.Contains("pattern is required", Failed(await Grep(("pattern", ""))));
        Assert.Contains("..", Failed(await Grep(("pattern", "x"), ("path", "../outside"))));
    }

    [Fact]
    public async Task GrepGivesUpOnAPatternThatBacktracksBadly()
    {
        Put("long.txt", new string('a', 8000) + "\n");
        string error = Failed(await Grep(("pattern", "(a+)+$b")));
        Assert.Contains("too long", error);
    }

    // ---- bash ----

    [Fact]
    public async Task BashRunsInTheWorkspaceAndReportsOutputAndExitCode()
    {
        Put("marker.txt", "here");
        string list = OperatingSystem.IsWindows() ? "dir /b" : "ls";
        JObject result = await CodeBashTool.Run(_workspace, Args(("command", list)), CancellationToken.None);
        Ok(result);
        Assert.Equal(0, result.Value<int>("exitCode"));
        Assert.Contains("marker.txt", result.Value<string>("stdout"));
        Assert.Equal("", result.Value<string>("workingDirectory"));

        JObject failing = await CodeBashTool.Run(_workspace, Args(("command", "exit 3")), CancellationToken.None);
        Assert.Equal(3, failing.Value<int>("exitCode"));
        Assert.True(failing.Value<bool>("success"));
    }

    [Fact]
    public async Task BashStartsInASubfolderOnlyWithinTheWorkspaceAndStopsALongCommand()
    {
        Put("sub/inner.txt", "x");
        string list = OperatingSystem.IsWindows() ? "dir /b" : "ls";
        JObject inSub = await CodeBashTool.Run(_workspace, Args(("command", list), ("workingDirectory", "sub")), CancellationToken.None);
        Assert.Contains("inner.txt", inSub.Value<string>("stdout"));
        Assert.Equal("sub", inSub.Value<string>("workingDirectory"));
        Assert.Contains("..", Failed(await CodeBashTool.Run(_workspace, Args(("command", list), ("workingDirectory", "../outside")), CancellationToken.None)));
        Assert.Contains("does not exist", Failed(await CodeBashTool.Run(_workspace, Args(("command", list), ("workingDirectory", "nope")), CancellationToken.None)));

        string sleeper = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";
        DateTime start = DateTime.UtcNow;
        JObject stopped = await CodeBashTool.Run(_workspace, Args(("command", sleeper), ("timeoutSeconds", 1)), CancellationToken.None);
        Assert.True(stopped.Value<bool>("timedOut"));
        Assert.False(stopped.Value<bool>("success"));
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(15));
        Assert.Contains("command is required", Failed(await CodeBashTool.Run(_workspace, Args(("command", " ")), CancellationToken.None)));
    }

    // ---- project instructions ----

    [Fact]
    public void ProjectInstructionsComeFromAgentsMdThenClaudeMdAndAreCappedAndStable()
    {
        Assert.Null(ProjectInstructionsService.Read(_workspace));
        Put("CLAUDE.md", "from claude\r\nline two");
        string claude = ProjectInstructionsService.Read(_workspace)!;
        Assert.Contains("from claude\nline two", claude);
        Assert.Contains("CLAUDE.md", claude);
        Put("AGENTS.md", "from agents");
        Assert.Contains("from agents", ProjectInstructionsService.Read(_workspace)!);
        Assert.DoesNotContain("from claude", ProjectInstructionsService.Read(_workspace)!);
        // The same text every time, so a prompt cache stays valid until the file changes.
        Assert.Equal(ProjectInstructionsService.Read(_workspace), ProjectInstructionsService.Read(_workspace));

        Put("AGENTS.md", new string('x', ProjectInstructionsService.MaxChars + 500));
        string capped = ProjectInstructionsService.Read(_workspace)!;
        Assert.Contains("left out", capped);
        Assert.True(capped.Length < ProjectInstructionsService.MaxChars + 300);
        Put("AGENTS.md", "   \n  ");
        Assert.DoesNotContain("AGENTS.md", ProjectInstructionsService.Read(_workspace) ?? "");
    }

    [Fact]
    public void OnlyACodeToolMakesAChatTakeInWorkspaceInstructions()
    {
        JObject code = new() { ["handlerId"] = ToolConstants.CodeEdit };
        JObject other = new() { ["handlerId"] = ToolConstants.FileRead };
        Assert.True(ProjectInstructionsService.HasCodeTool([other, code]));
        Assert.False(ProjectInstructionsService.HasCodeTool([other]));
        Assert.False(ProjectInstructionsService.HasCodeTool([]));
        Assert.Equal(7, ToolConstants.CodeIds.Length);
        Assert.DoesNotContain(ToolConstants.BuiltInIds, id => ToolConstants.CodeIds.Contains(id));
    }

    // ---- definitions ----

    [Fact]
    public void EveryCodeToolHasAnOffDefinitionWithAHandlerOfTheSameId()
    {
        JObject defs = CodeToolDefinitions.Build("now");
        Assert.Equal(ToolConstants.CodeIds.Order(StringComparer.Ordinal), defs.Properties().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (JProperty property in defs.Properties())
        {
            JObject def = (JObject)property.Value;
            Assert.False(def.Value<bool>("enabled"), $"{property.Name} must be off until someone switches it on");
            Assert.Equal(property.Name, def.Value<string>("handlerId"));
            Assert.Equal("object", def["parameters"]!["type"]!.ToString());
            Assert.NotEmpty((JArray)def["parameters"]!["required"]!);
            Assert.Contains("workspace", def.Value<string>("description")!, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Patterns for file paths.</summary>
public class PathGlobTests
{
    [Theory]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "src/a.cs", true)]
    [InlineData("*.cs", "a.cs.txt", false)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/deep/a.cs", false)]
    [InlineData("src/**/*.cs", "src/a.cs", true)]
    [InlineData("src/**/*.cs", "src/x/y/a.cs", true)]
    [InlineData("**/test_*.py", "a/b/test_x.py", true)]
    [InlineData("**/test_*.py", "test_x.py", true)]
    [InlineData("a?c.txt", "abc.txt", true)]
    [InlineData("a?c.txt", "a/c.txt", false)]
    [InlineData("./src/*.js", "src/x.js", true)]
    [InlineData("a.b", "aXb", false)]
    [InlineData("(x)[y]+.txt", "(x)[y]+.txt", true)]
    public void PatternsMatchAsAShellUserExpects(string pattern, string path, bool expected)
        => Assert.Equal(expected, PathGlob.IsMatch(PathGlob.Compile(pattern, out string error)!, path));

    [Fact]
    public void AnEmptyOrHugePatternIsRefused()
    {
        Assert.Null(PathGlob.Compile("", out string error));
        Assert.NotNull(error);
        Assert.Null(PathGlob.Compile(new string('*', 1000), out _));
    }
}
