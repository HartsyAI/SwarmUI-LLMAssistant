using Hartsy.Extensions.LLMAssistant.Services.Hooks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Reading, saving and checking hook definitions.</summary>
public class ToolHookTests
{
    private static JObject Rule(Action<JObject> change = null)
    {
        JObject json = new() { ["id"] = "no-env", ["name"] = "No env files", ["phase"] = "pre", ["tool"] = "file_write", ["kind"] = "rule", ["argument"] = "path", ["matchType"] = "glob", ["value"] = "*.env", ["action"] = "deny", ["message"] = "Do not write env files." };
        change?.Invoke(json);
        return json;
    }

    private static string Check(Action<JObject> change) => ToolHook.FromJson(Rule(change)).Validate();

    [Fact]
    public void AGoodRuleIsValidAndSurvivesAJsonRoundTrip()
    {
        ToolHook hook = ToolHook.FromJson(Rule());
        Assert.Null(hook.Validate());
        ToolHook again = ToolHook.FromJson(hook.ToJson());
        Assert.Equal(hook.ToJson().ToString(), again.ToJson().ToString());
    }

    [Fact]
    public void AMissingFieldTakesItsDefault()
    {
        ToolHook hook = ToolHook.FromJson(new JObject { ["id"] = "x", ["name"] = "x", ["action"] = "ask" });
        Assert.True(hook.Enabled);
        Assert.Equal("pre", hook.Phase);
        Assert.Equal("*", hook.Tool);
        Assert.Equal("rule", hook.Kind);
        Assert.Equal("any", hook.MatchType);
        Assert.Null(hook.Validate());
    }

    [Theory]
    [InlineData("id", "has space")]
    [InlineData("id", "")]
    [InlineData("name", "")]
    [InlineData("phase", "during")]
    [InlineData("tool", "has space")]
    [InlineData("kind", "script")]
    [InlineData("matchType", "fuzzy")]
    [InlineData("action", "explode")]
    [InlineData("value", "")]
    public void ARuleWithABadFieldIsRejected(string field, string value)
    {
        Assert.NotNull(Check(json => json[field] = value));
    }

    [Fact]
    public void ARegexThatDoesNotCompileIsRejected()
    {
        Assert.NotNull(Check(json => { json["matchType"] = "regex"; json["value"] = "([unclosed"; }));
    }

    [Fact]
    public void FieldsAreKeptInsideTheirLimits()
    {
        Assert.NotNull(Check(json => json["name"] = new string('n', 101)));
        Assert.NotNull(Check(json => json["message"] = new string('m', 1001)));
        Assert.NotNull(Check(json => json["value"] = new string('v', 501)));
        Assert.NotNull(Check(json => json["id"] = new string('i', 65)));
    }

    [Fact]
    public void APostHookCanOnlyAddANote()
    {
        Assert.NotNull(Check(json => { json["phase"] = "post"; json["action"] = "deny"; }));
        Assert.NotNull(Check(json => { json["phase"] = "post"; json["action"] = "rewrite"; }));
        Assert.Null(Check(json => { json["phase"] = "post"; json["action"] = "note"; json["message"] = "Remember to review."; }));
    }

    [Fact]
    public void ANoteNeedsItsMessage_AndARewriteNeedsSomethingToSet()
    {
        Assert.NotNull(Check(json => { json["action"] = "note"; json["message"] = "  "; }));
        Assert.NotNull(Check(json => { json["action"] = "rewrite"; json["set"] = new JObject(); }));
        Assert.Null(Check(json => { json["action"] = "rewrite"; json["set"] = new JObject { ["overwrite"] = false }; }));
    }

    [Fact]
    public void ACommandHookNeedsAProgramAndStaysInsideItsLimits()
    {
        JObject command(Action<JObject> change)
        {
            JObject json = new() { ["id"] = "c", ["name"] = "c", ["kind"] = "command", ["command"] = "node", ["args"] = new JArray("hook.mjs"), ["timeoutSeconds"] = 5, ["onError"] = "deny" };
            change?.Invoke(json);
            return json;
        }
        Assert.Null(ToolHook.FromJson(command(null)).Validate());
        Assert.NotNull(ToolHook.FromJson(command(j => j["command"] = "")).Validate());
        Assert.NotNull(ToolHook.FromJson(command(j => j["timeoutSeconds"] = 0)).Validate());
        Assert.NotNull(ToolHook.FromJson(command(j => j["timeoutSeconds"] = 61)).Validate());
        Assert.NotNull(ToolHook.FromJson(command(j => j["onError"] = "shrug")).Validate());
        Assert.NotNull(ToolHook.FromJson(command(j => j["args"] = new JArray(Enumerable.Repeat("a", 21)))).Validate());
    }

    [Fact]
    public void ADisabledFlagIsReadAsWritten()
    {
        Assert.False(ToolHook.FromJson(Rule(j => j["enabled"] = false)).Enabled);
        Assert.True(ToolHook.FromJson(Rule(j => j["enabled"] = "false")).Enabled);
    }

    [Fact]
    public void SavingSettingsNeverAcceptsHooksOrTheOtherDictsWithTheirOwnRoutes()
    {
        JObject incoming = new()
        {
            ["parameters"] = new JObject { ["temperature"] = 0.5 },
            ["hooks"] = new JObject { ["x"] = new JObject { ["kind"] = "command", ["command"] = "calc" } },
            ["assistants"] = new JObject(),
            ["tools"] = new JObject(),
        };

        JObject filtered = Hartsy.Extensions.LLMAssistant.WebAPI.SettingsEndpoints.FilterIncoming(incoming);

        Assert.Equal(["parameters"], filtered.Properties().Select(p => p.Name));
        Assert.NotNull(incoming["hooks"]);
    }
}
