using System.IO;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Code mode on the server: what a thread's mode is, which tools a code chat brings, and the folder listing behind the files panel.</summary>
public sealed class CodeModeTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "llma-mode-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Workspace _workspace;

    public CodeModeTests()
    {
        string root = Path.Combine(_base, "ws");
        Directory.CreateDirectory(Path.Combine(root, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(root, "A.txt"), "a");
        File.WriteAllText(Path.Combine(root, ".llma-123.tmp"), "half written");
        File.WriteAllText(Path.Combine(root, "src", "app.js"), "let x;\n");
        Directory.CreateDirectory(Path.Combine(_base, "outside"));
        _workspace = new Workspace("user:test", "test", root, true);
    }

    public void Dispose()
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(_base, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
            {
                Directory.Delete(directory);
            }
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the temp folder cleanup.
        }
    }

    [Theory]
    [InlineData("chat", true)]
    [InlineData("code", true)]
    [InlineData("Code", false)]
    [InlineData("", false)]
    [InlineData("agent", false)]
    public void OnlyChatAndCodeAreModes(string mode, bool valid) => Assert.Equal(valid, ThreadModes.IsValid(mode));

    [Fact]
    public void AThreadIsInCodeModeOnlyWhenItSaysSo()
    {
        Assert.True(CodeModeService.IsCodeThread(new JObject { ["mode"] = "code" }));
        Assert.False(CodeModeService.IsCodeThread(new JObject { ["mode"] = "chat" }));
        Assert.False(CodeModeService.IsCodeThread(new JObject()));
        Assert.False(CodeModeService.IsCodeThread(null));
    }

    [Fact]
    public void WithoutAUserOrTheModeNothingIsAdded()
    {
        List<JObject> chosen = [new JObject { ["id"] = "web_search" }];
        JObject settings = new() { ["tools"] = CodeToolDefinitions.Build("now") };
        // No user (so no llm_code_mode), even in a code thread: nothing brought along.
        Assert.Equal(chosen, CodeModeService.WithCodeTools(chosen, new JObject { ["mode"] = "code" }, settings, null));
        Assert.False(CodeModeService.BringsCodeTools(new JObject { ["mode"] = "code" }, null));
        Assert.False(CodeModeService.GrantsTool(ToolConstants.CodeBash, null, "t1"));
        Assert.False(CodeModeService.GrantsTool("web_search", null, "t1"));
        Assert.False(CodeModeService.GrantsTool(ToolConstants.CodeBash, null, null));
    }

    [Fact]
    public void ToolCallingPinnedOffForTheChatStopsCodeModeFromBringingTools()
    {
        Assert.False(CodeModeService.BringsCodeTools(new JObject { ["mode"] = "code", ["toolsEnabled"] = false }, null));
    }

    // ---- the files panel's listing ----

    [Fact]
    public void AFolderListsFoldersFirstThenFilesInNameOrderAndHidesOurTemporaryFiles()
    {
        JObject top = WorkspaceEndpoints.ListFolder(_workspace, "");
        Assert.True(top.Value<bool>("success"), top.ToString());
        Assert.Equal("", top.Value<string>("path"));
        List<(string Name, string Type)> entries = [.. ((JArray)top["entries"]!).Select(e => (e.Value<string>("name")!, e.Value<string>("type")!))];
        Assert.Equal([("docs", "dir"), ("src", "dir"), ("A.txt", "file"), ("b.txt", "file")], entries);
        Assert.False(top.Value<bool>("truncated"));

        JObject src = WorkspaceEndpoints.ListFolder(_workspace, "src");
        Assert.Equal("src", src.Value<string>("path"));
        Assert.Equal([("deep", "dir"), ("app.js", "file")], ((JArray)src["entries"]!).Select(e => (e.Value<string>("name")!, e.Value<string>("type")!)));
        Assert.Equal(7, ((JArray)src["entries"]!).Single(e => e.Value<string>("name") == "app.js").Value<int>("size"));
    }

    [Fact]
    public void AListingNeverLeavesTheWorkspaceAndNamesNoHostPath()
    {
        foreach (string bad in new[] { "..", "../outside", "src/../..", Path.Combine(_base, "outside") })
        {
            JObject result = WorkspaceEndpoints.ListFolder(_workspace, bad);
            Assert.False(result.Value<bool>("success"), bad);
            Assert.DoesNotContain(_base, result.ToString());
        }
        JObject missing = WorkspaceEndpoints.ListFolder(_workspace, "nope");
        Assert.False(missing.Value<bool>("success"));
        Assert.Contains("does not exist", missing.Value<string>("error"));
        Assert.DoesNotContain(_base, WorkspaceEndpoints.ListFolder(_workspace, "src").ToString());
        // A file is not a folder.
        Assert.False(WorkspaceEndpoints.ListFolder(_workspace, "b.txt").Value<bool>("success"));
    }

    [Fact]
    public void LinksAreLeftOutOfAListingAndCannotBeOpenedToSeeWhereTheyLead()
    {
        string link = Path.Combine(_workspace.Root, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_base, "outside"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Windows makes folder links without privileges only as junctions; the sandbox tests cover those.
            return;
        }
        JObject top = WorkspaceEndpoints.ListFolder(_workspace, "");
        Assert.DoesNotContain(((JArray)top["entries"]!).Select(e => e.Value<string>("name")), name => name == "escape");
        Assert.False(WorkspaceEndpoints.ListFolder(_workspace, "escape").Value<bool>("success"));
    }

    [Fact]
    public void AHugeFolderIsCutAndSaysSo()
    {
        string big = Path.Combine(_workspace.Root, "big");
        Directory.CreateDirectory(big);
        for (int i = 0; i < WorkspaceEndpoints.MaxListed + 25; i++)
        {
            File.WriteAllText(Path.Combine(big, $"f{i:D5}.txt"), "");
        }
        JObject result = WorkspaceEndpoints.ListFolder(_workspace, "big");
        Assert.Equal(WorkspaceEndpoints.MaxListed, ((JArray)result["entries"]!).Count);
        Assert.True(result.Value<bool>("truncated"));
    }
}
