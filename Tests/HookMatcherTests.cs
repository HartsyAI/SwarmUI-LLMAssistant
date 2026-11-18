using Hartsy.Extensions.LLMAssistant.Services.Hooks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Whether a rule hook applies to a tool call.</summary>
public class HookMatcherTests
{
    [Theory]
    [InlineData("file_write", "file_write", true)]
    [InlineData("FILE_WRITE", "file_write", true)]
    [InlineData("file_*", "file_write", true)]
    [InlineData("file_*", "memory_write", false)]
    [InlineData("*", "anything", true)]
    [InlineData("*_write", "file_write", true)]
    [InlineData("file_writ?", "file_write", true)]
    [InlineData("file", "file_write", false)]
    [InlineData("", "file_write", false)]
    // Regex characters in a glob are plain text.
    [InlineData("a.b", "axb", false)]
    [InlineData("a.b", "a.b", true)]
    public void AToolPatternIsAGlobOverTheToolId(string pattern, string toolId, bool expected)
    {
        Assert.Equal(expected, HookMatcher.ToolMatches(pattern, toolId));
    }

    private static ToolHook Hook(string tool, string argument, string matchType, string value) => new() { Tool = tool, Argument = argument, MatchType = matchType, Value = value };

    [Fact]
    public void AHookThatNamesNoArgumentAppliesToEveryCallOfItsTool()
    {
        Assert.True(HookMatcher.Applies(Hook("shell_exec", "", "any", ""), "shell_exec", new JObject()));
        Assert.False(HookMatcher.Applies(Hook("shell_exec", "", "any", ""), "file_write", new JObject()));
    }

    [Fact]
    public void GlobMatchesTheWholeArgument()
    {
        ToolHook hook = Hook("file_write", "path", "glob", "*.env");
        Assert.True(HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = "config/prod.env" }));
        Assert.False(HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = "notes.txt" }));
        Assert.False(HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = "prod.env.bak" }));
    }

    [Fact]
    public void ContainsIgnoresCase()
    {
        ToolHook hook = Hook("shell_exec", "command", "contains", "RM -RF");
        Assert.True(HookMatcher.Applies(hook, "shell_exec", new JObject { ["command"] = "sudo rm -rf /" }));
        Assert.False(HookMatcher.Applies(hook, "shell_exec", new JObject { ["command"] = "ls" }));
    }

    [Fact]
    public void RegexMatchesAnywhereInTheArgument()
    {
        ToolHook hook = Hook("http_request", "url", "regex", @"^https?://[^/]*\.internal\b");
        Assert.True(HookMatcher.Applies(hook, "http_request", new JObject { ["url"] = "http://db.internal/admin" }));
        Assert.False(HookMatcher.Applies(hook, "http_request", new JObject { ["url"] = "https://example.com" }));
    }

    [Fact]
    public void AnArgumentTheCallDoesNotHaveNeverMatches()
    {
        ToolHook hook = Hook("file_write", "path", "any", "");
        Assert.False(HookMatcher.Applies(hook, "file_write", new JObject { ["content"] = "x" }));
        Assert.True(HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = "x" }));
        Assert.False(HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = JValue.CreateNull() }));
    }

    [Fact]
    public void StarLooksAtAllTheArgumentsAsText()
    {
        ToolHook hook = Hook("*", "*", "contains", "secret");
        Assert.True(HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = "a", ["content"] = "my secret" }));
        Assert.False(HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = "a", ["content"] = "fine" }));
    }

    [Fact]
    public void ANonTextArgumentIsComparedAsItsJson()
    {
        ToolHook hook = Hook("generate_image", "steps", "regex", @"^(5\d|[6-9]\d|\d{3,})$");
        Assert.True(HookMatcher.Applies(hook, "generate_image", new JObject { ["steps"] = 80 }));
        Assert.False(HookMatcher.Applies(hook, "generate_image", new JObject { ["steps"] = 20 }));
    }

    [Fact]
    public void ABadRegexNeverMatchesAndDoesNotThrow()
    {
        ToolHook hook = Hook("file_write", "path", "regex", "([unclosed");
        Assert.False(HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = "x" }));
        Assert.False(HookMatcher.TryCompile("([unclosed", out _));
    }

    [Fact]
    public void ARegexThatBacktracksBadlyGivesUpInsteadOfStallingTheTurn()
    {
        ToolHook hook = Hook("file_write", "path", "regex", "^(a+)+$");
        DateTime start = DateTime.UtcNow;
        bool matched = HookMatcher.Applies(hook, "file_write", new JObject { ["path"] = new string('a', 40) + "!" });
        Assert.False(matched);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AGlobThatTakesTooLongDoesNotMatchAndDoesNotThrow()
    {
        // Many wildcards against a long text with no way to match: the regex behind it backtracks until its time limit.
        string pattern = "*a*a*a*a*a*a*a*a*a*a*b";
        string text = new('a', 6000);

        Assert.False(HookMatcher.Glob(pattern, text));
        Assert.False(HookMatcher.ToolMatches(pattern, text));
    }

    [Fact]
    public void ARepeatedGlobGivesTheSameAnswerFromItsCachedForm()
    {
        for (int i = 0; i < 3; i++)
        {
            Assert.True(HookMatcher.Glob("file_*", "FILE_write"));
            Assert.False(HookMatcher.Glob("file_*", "shell_exec"));
        }
    }
}
