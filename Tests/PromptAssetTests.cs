using System.IO;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Tools;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Skills and commands: the file format, reading a folder of them, merging scopes (and keeping an untrusted workspace out), the skill index, running a command, and the
/// <c>skill</c> tool.</summary>
public sealed class PromptAssetTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "llma-assets-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Func<string> _oldShared = PromptAssetService.SharedRoot;
    private readonly string _oldBundled = PromptAssetService.BundledRoot;

    public PromptAssetTests()
    {
        Directory.CreateDirectory(_base);
        PromptAssetService.SharedRoot = () => Path.Combine(_base, "shared");
        PromptAssetService.BundledRoot = null;
    }

    public void Dispose()
    {
        PromptAssetService.SharedRoot = _oldShared;
        PromptAssetService.BundledRoot = _oldBundled;
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the temp folder cleanup.
        }
    }

    /// <summary>Writes a file under the test folder, making its folders.</summary>
    private string Write(string relative, string text)
    {
        string path = Path.Combine(_base, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static PromptAsset Asset(string kind, string name, string scope = "shared", string description = "d", string body = "b", bool userOnly = false, string dir = null)
        => new(kind, name, description, "", body, scope, dir, userOnly);

    // ---- the file format ----

    [Fact]
    public void FrontMatterGivesKeysAndBody()
    {
        (Dictionary<string, string> meta, string body) = PromptAssetFile.Parse("---\nname: pdf\ndescription: \"Work with PDFs\"\nArgument-Hint: [file]\n---\n\n# Title\n\nText\n");
        Assert.Equal("Work with PDFs", meta["description"]);
        Assert.Equal("[file]", meta["argument-hint"]);
        Assert.Equal("# Title\n\nText", body);
    }

    [Fact]
    public void AValueCanContinueOnIndentedLinesOrBeABlock()
    {
        (Dictionary<string, string> folded, _) = PromptAssetFile.Parse("---\ndescription: >\n  First part\n  second part\n---\nbody");
        Assert.Equal("First part second part", folded["description"]);
        (Dictionary<string, string> literal, _) = PromptAssetFile.Parse("---\ndescription: |\n  line one\n  line two\n---\nbody");
        Assert.Equal("line one\nline two", literal["description"]);
        (Dictionary<string, string> plain, _) = PromptAssetFile.Parse("---\ndescription: starts here\n  and goes on\nother: x\n---\nbody");
        Assert.Equal("starts here and goes on", plain["description"]);
        Assert.Equal("x", plain["other"]);
    }

    [Theory]
    [InlineData("just a body\n", "just a body")]
    [InlineData("---\n---\nbody here\n", "body here")]
    [InlineData("---\nkey: v\nnever closed\n", "---\nkey: v\nnever closed")]
    [InlineData("﻿---\r\nkey: v\r\n---\r\nbody\r\n", "body")]
    public void FilesWithoutProperFrontMatterAreStillRead(string text, string expectedBody)
    {
        (_, string body) = PromptAssetFile.Parse(text);
        Assert.Equal(expectedBody, body);
    }

    [Fact]
    public void FormatKeepsAValueOnOneLineAndReadsBack()
    {
        string text = PromptAssetFile.Format("\"quoted\" start\nsecond line", "[a] [b]", true, "Body\r\nmore");
        (Dictionary<string, string> meta, string body) = PromptAssetFile.Parse(text);
        Assert.Equal("'quoted' start second line", meta["description"]);
        Assert.Equal("[a] [b]", meta["argument-hint"]);
        Assert.True(PromptAssetFile.IsTrue(meta, "disable-model-invocation"));
        Assert.Equal("Body\nmore", body);
        // A description cannot add a key of its own.
        (Dictionary<string, string> injected, _) = PromptAssetFile.Parse(PromptAssetFile.Format("fine\ndisable-model-invocation: true", "", false, "x"));
        Assert.False(PromptAssetFile.IsTrue(injected, "disable-model-invocation"));
    }

    [Theory]
    [InlineData("code-review", true)]
    [InlineData("a", true)]
    [InlineData("Review", false)]
    [InlineData("-x", false)]
    [InlineData("has space", false)]
    [InlineData("../x", false)]
    [InlineData("", false)]
    public void NamesAreLowercaseWordsAndDashes(string name, bool valid) => Assert.Equal(valid, PromptAsset.IsValidName(name));

    // ---- reading a folder ----

    [Fact]
    public void SkillsAndCommandsAreReadFromTheirFolders()
    {
        Write("root/skills/zeta/SKILL.md", "---\ndescription: Last skill\n---\nZ body");
        Write("root/skills/alpha/SKILL.md", "---\nname: something-else\ndescription: First skill\ndisable-model-invocation: true\n---\nA body");
        Write("root/commands/fix.md", "---\ndescription: Fix it\nargument-hint: [what]\n---\nFix $ARGUMENTS");
        List<PromptAsset> assets = PromptAssetStore.Load(Path.Combine(_base, "root"), "shared");
        Assert.Equal(["skill:alpha", "skill:zeta", "command:fix"], assets.Select(a => $"{a.Kind}:{a.Name}"));
        PromptAsset alpha = assets[0];
        Assert.Equal("First skill", alpha.Description);
        Assert.True(alpha.UserOnly);
        Assert.Equal("shared", alpha.Scope);
        Assert.EndsWith("alpha", alpha.Directory);
        Assert.Equal("[what]", assets[2].ArgumentHint);
        Assert.Null(assets[2].Directory);
    }

    [Fact]
    public void OddFilesAreLeftOut()
    {
        Write("root/skills/Bad Name/SKILL.md", "x");
        Write("root/skills/nofile/readme.md", "x");
        Write("root/commands/UPPER.md", "x");
        Write("root/commands/notes.txt", "x");
        Write("root/skills/big/SKILL.md", new string('a', PromptAsset.MaxFileBytes + 1));
        Write("root/skills/ok/SKILL.md", "fine");
        Assert.Equal(["ok"], PromptAssetStore.Load(Path.Combine(_base, "root"), "shared").Select(a => a.Name));
    }

    [Fact]
    public void ADescriptionFallsBackToTheFirstLineOfTheText()
    {
        Write("root/commands/plain.md", "\n# Plan the work\n\nthen more");
        Assert.Equal("Plan the work", PromptAssetStore.Load(Path.Combine(_base, "root"), "shared").Single().Description);
    }

    [Fact]
    public void AChangedFileIsReadAgain()
    {
        string path = Write("root/commands/c.md", "first");
        Assert.Equal("first", PromptAssetStore.Load(Path.Combine(_base, "root"), "shared").Single().Body);
        File.WriteAllText(path, "second version");
        Assert.Equal("second version", PromptAssetStore.Load(Path.Combine(_base, "root"), "shared").Single().Body);
        File.Delete(path);
        Assert.Empty(PromptAssetStore.Load(Path.Combine(_base, "root"), "shared"));
        Assert.Empty(PromptAssetStore.Load(Path.Combine(_base, "missing"), "shared"));
        Assert.Empty(PromptAssetStore.Load(null, "shared"));
    }

    [Fact]
    public void ALinkedSkillFolderIsNotFollowed()
    {
        Write("outside/SKILL.md", "---\ndescription: elsewhere\n---\nsecret");
        Directory.CreateDirectory(Path.Combine(_base, "root", "skills"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_base, "root", "skills", "linked"), Path.Combine(_base, "outside"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Windows makes folder links without privileges only as junctions, which the sandbox tests cover.
            return;
        }
        Assert.Empty(PromptAssetStore.Load(Path.Combine(_base, "root"), "shared"));
    }

    [Fact]
    public void ASkillsOtherFilesAreListedWithoutItsOwnTextOrDeepOnes()
    {
        string skill = Path.Combine(_base, "root", "skills", "s");
        Write("root/skills/s/SKILL.md", "x");
        Write("root/skills/s/ref.md", "r");
        Write("root/skills/s/templates/a.txt", "t");
        Write("root/skills/s/a/b/c/too-deep.txt", "t");
        List<string> files = PromptAssetStore.ListSkillFiles(Asset("skill", "s", dir: skill));
        Assert.Equal(["ref.md", "templates/a.txt"], files.OrderBy(f => f, StringComparer.Ordinal));
        Assert.Empty(PromptAssetStore.ListSkillFiles(Asset("command", "c")));
    }

    // ---- merging scopes ----

    [Fact]
    public void ALaterScopeReplacesAnEarlierOneOfTheSameNameAndKind()
    {
        PromptAssetSet set = PromptAssetService.Merge(
            [Asset("skill", "a", "bundled", "from bundled")],
            [Asset("skill", "a", "shared", "from shared"), Asset("command", "a", "shared", "command a")],
            [Asset("skill", "a", "personal", "from personal")],
            [], false);
        Assert.Equal("from personal", set.Get("skill", "a").Description);
        Assert.Equal("command a", set.Get("command", "a").Description);
        Assert.Equal(["skill:a", "command:a"], set.Assets.Select(a => $"{a.Kind}:{a.Name}"));
    }

    [Fact]
    public void AnUntrustedWorkspacesAssetsAreCountedButNeverUsed()
    {
        PromptAssetSet set = PromptAssetService.Merge([], [Asset("skill", "s", "shared")], [], [Asset("skill", "w", "workspace"), Asset("command", "x", "workspace")], false, "user:default", "default");
        Assert.Equal(["s"], set.Assets.Select(a => a.Name));
        Assert.Equal(2, set.IgnoredWorkspaceAssets);
        Assert.False(set.WorkspaceTrusted);
        Assert.Equal("user:default", set.WorkspaceId);
        Assert.Null(set.Get("skill", "w"));
    }

    [Fact]
    public void ATrustedWorkspaceAddsNamesButNeverReplacesAnotherScopes()
    {
        PromptAssetSet set = PromptAssetService.Merge([], [Asset("skill", "s", "shared", "the shared one")], [Asset("command", "p", "personal", "mine")],
            [Asset("skill", "s", "workspace", "the workspace one"), Asset("command", "p", "workspace", "hijack"), Asset("skill", "w", "workspace", "new")], true);
        Assert.Equal("the shared one", set.Get("skill", "s").Description);
        Assert.Equal("mine", set.Get("command", "p").Description);
        Assert.Equal("new", set.Get("skill", "w").Description);
        Assert.Equal(0, set.IgnoredWorkspaceAssets);
    }

    [Fact]
    public void OnlySkillsTheModelMayLoadAreOffered()
    {
        PromptAssetSet set = PromptAssetService.Merge([], [Asset("skill", "open"), Asset("skill", "mine-only", userOnly: true), Asset("command", "cmd")], [], [], false);
        Assert.Equal(["open"], set.ModelSkills.Select(s => s.Name));
        Assert.NotNull(set.Get("skill", "mine-only"));
    }

    // ---- the index in the system prompt ----

    [Fact]
    public void TheIndexIsSortedAndTheSameEveryTime()
    {
        PromptAsset[] skills = [Asset("skill", "zebra", description: "Z things"), Asset("skill", "apple", description: "A\n   things")];
        string index = PromptAssetService.BuildIndex(skills);
        Assert.Equal(index, PromptAssetService.BuildIndex(skills.Reverse()));
        Assert.True(index.IndexOf("- apple: A things", StringComparison.Ordinal) < index.IndexOf("- zebra: Z things", StringComparison.Ordinal));
        Assert.Contains("`skill` tool", index);
        Assert.Null(PromptAssetService.BuildIndex([]));
    }

    [Fact]
    public void TheIndexIsBounded()
    {
        PromptAsset[] many = [.. Enumerable.Range(0, 60).Select(i => Asset("skill", $"s{i:00}", description: new string('x', 500)))];
        string index = PromptAssetService.BuildIndex(many);
        Assert.Equal(PromptAssetService.MaxIndexedSkills, index.Split('\n').Count(l => l.StartsWith("- s")));
        Assert.DoesNotContain(new string('x', PromptAssetService.MaxIndexedDescriptionChars + 1), index);
        Assert.Contains("…", index);
        Assert.DoesNotContain("- s59", index);
    }

    [Fact]
    public void TheIndexIsOnlyAddedWhenTheSkillToolIsOffered()
    {
        Write("shared/skills/s/SKILL.md", "---\ndescription: A skill\n---\nbody");
        JObject with = new() { ["id"] = "skill", ["handlerId"] = "skill" };
        JObject without = new() { ["id"] = "web_search", ["handlerId"] = "web_search" };
        // Without a user there is nothing to say, whichever tools there are.
        Assert.Equal("prompt", PromptAssetService.AppendIndex("prompt", [with], null, null));
        Assert.Equal("prompt", PromptAssetService.AppendIndex("prompt", [without], null, null));
    }

    // ---- running one ----

    [Fact]
    public void ACommandReplacesItsArgumentsTokenInOnePass()
    {
        PromptAsset command = Asset("command", "c", body: "Review $ARGUMENTS now. Again: $ARGUMENTS.");
        Assert.Equal("Review src/app.js now. Again: src/app.js.", PromptAssetService.Expand(command, "  src/app.js "));
        // An argument that contains the token is not expanded again.
        Assert.Equal("Review $ARGUMENTS now. Again: $ARGUMENTS.", PromptAssetService.Expand(command, "$ARGUMENTS"));
        Assert.Equal("Review  now. Again: .", PromptAssetService.Expand(command, null));
    }

    [Fact]
    public void ArgumentsWithNoTokenAreAddedAtTheEnd()
    {
        PromptAsset command = Asset("command", "c", body: "Write a summary.");
        Assert.Equal("Write a summary.", PromptAssetService.Expand(command, ""));
        Assert.Equal("Write a summary.\n\nARGUMENTS: of the meeting", PromptAssetService.Expand(command, "of the meeting"));
    }

    [Fact]
    public void ATemplateIsOnlyTextNothingInItIsRun()
    {
        PromptAsset command = Asset("command", "c", body: "Status: !`git status` and @README.md");
        Assert.Equal("Status: !`git status` and @README.md", PromptAssetService.Expand(command, ""));
    }

    [Fact]
    public void ASkillTheUserRunsCarriesItsText()
    {
        PromptAsset skill = Asset("skill", "pdf", body: "Do it this way.", userOnly: true);
        string text = PromptAssetService.Expand(skill, "merge these");
        Assert.StartsWith("Use the \"pdf\" skill for the request below.", text);
        Assert.Contains("Do it this way.", text);
        Assert.EndsWith("merge these", text);
        Assert.DoesNotContain("\n\n\n", PromptAssetService.Expand(skill, ""));
    }

    // ---- the skill tool ----

    private static ToolExecutionContext Call(string name, string file = null)
    {
        JObject args = new() { ["name"] = name };
        if (file is not null)
        {
            args["file"] = file;
        }
        return new ToolExecutionContext { Args = args };
    }

    [Fact]
    public async Task TheToolGivesTheInstructionsAndListsTheOtherFiles()
    {
        Write("shared/skills/pdf/SKILL.md", "---\ndescription: PDF help\n---\nUse the forms script.");
        Write("shared/skills/pdf/forms.md", "form notes");
        JObject result = await new SkillTool().Execute(Call("PDF"));
        Assert.True(result.Value<bool>("success"), result.ToString());
        Assert.Equal("Use the forms script.", result.Value<string>("instructions"));
        Assert.Equal(["forms.md"], result["files"]!.Select(f => f.ToString()));
        JObject file = await new SkillTool().Execute(Call("pdf", "forms.md"));
        Assert.Equal("form notes", file.Value<string>("content"));
    }

    [Fact]
    public async Task TheToolRefusesWhatWasNotAdvertised()
    {
        Write("shared/skills/open/SKILL.md", "x");
        Write("shared/skills/mine/SKILL.md", "---\ndisable-model-invocation: true\n---\nsecret");
        JObject missing = await new SkillTool().Execute(Call("nope"));
        Assert.False(missing.Value<bool>("success"));
        Assert.Contains("Available: open.", missing.Value<string>("error"));
        JObject userOnly = await new SkillTool().Execute(Call("mine"));
        Assert.False(userOnly.Value<bool>("success"));
        Assert.DoesNotContain("secret", userOnly.ToString());
        Assert.False((await new SkillTool().Execute(Call(""))).Value<bool>("success"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("SKILL.md/../../x")]
    public async Task TheToolReadsNothingOutsideTheSkillsFolder(string file)
    {
        Write("shared/skills/s/SKILL.md", "x");
        Write("shared/skills/outside.txt", "not yours");
        JObject result = await new SkillTool().Execute(Call("s", file));
        Assert.False(result.Value<bool>("success"));
        Assert.DoesNotContain("not yours", result.ToString());
        Assert.DoesNotContain(_base, result.ToString());
    }

    [Fact]
    public async Task TheToolDoesNotReadBinaryOrHugeFiles()
    {
        Write("shared/skills/s/SKILL.md", "x");
        File.WriteAllBytes(Path.Combine(_base, "shared", "skills", "s", "image.bin"), [1, 2, 0, 3]);
        Write("shared/skills/s/long.txt", new string('a', SkillTool.MaxFileChars + 10));
        Assert.Contains("not text", (await new SkillTool().Execute(Call("s", "image.bin"))).Value<string>("error"));
        JObject cut = await new SkillTool().Execute(Call("s", "long.txt"));
        Assert.Equal(SkillTool.MaxFileChars, cut.Value<string>("content")!.Length);
        Assert.Contains("cut", cut.Value<string>("note"));
        Assert.False((await new SkillTool().Execute(Call("s", "missing.txt"))).Value<bool>("success"));
    }

    [Fact]
    public void TheToolIsReadOnlyAndNotHandedToEveryAssistant()
    {
        Assert.Equal(ToolRisk.Read, new SkillTool().Risk);
        Assert.Equal("skill", new SkillTool().HandlerId);
        Assert.DoesNotContain(ToolConstants.Skill, ToolConstants.BuiltInIds);
        Assert.NotNull(ToolRegistryService.BuildDefaultTools()[ToolConstants.Skill]);
        // Only the skill tool is granted outside an assistant's list, and only to a user in a chat.
        Assert.False(PromptAssetService.GrantsTool(ToolConstants.Skill, null, "t1"));
        Assert.False(PromptAssetService.GrantsTool("web_search", null, "t1"));
    }
}
