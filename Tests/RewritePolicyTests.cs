using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Which switch decides whether a generation's prompt is rewritten: per call, then per chat, then per assistant, then the model, then the entry point, then the main switch. The behaviour through the generation
/// hook is checked end to end in <c>E2E/tests/rewrite-controls.spec.ts</c>.</summary>
public class RewritePolicyTests
{
    /// <summary>Layers where every level from <paramref name="from"/> down says <paramref name="value"/> and every one below the first says the opposite, so only the first can have decided.</summary>
    private static RewriteLayers DecidedAt(int from, bool value)
    {
        bool? At(int level) => level < from ? null : level == from ? value : !value;
        return new RewriteLayers(At(0), At(1), At(2), At(3), At(4), from == 5 ? value : !value);
    }

    [Theory]
    [InlineData(0, RewriteLevels.Call, true)]
    [InlineData(0, RewriteLevels.Call, false)]
    [InlineData(1, RewriteLevels.Chat, true)]
    [InlineData(1, RewriteLevels.Chat, false)]
    [InlineData(2, RewriteLevels.Assistant, true)]
    [InlineData(2, RewriteLevels.Assistant, false)]
    [InlineData(3, RewriteLevels.Model, true)]
    [InlineData(3, RewriteLevels.Model, false)]
    [InlineData(4, RewriteLevels.EntryPoint, true)]
    [InlineData(4, RewriteLevels.EntryPoint, false)]
    [InlineData(5, RewriteLevels.Master, true)]
    [InlineData(5, RewriteLevels.Master, false)]
    public void TheHighestLevelThatSaysAnythingDecides(int from, string level, bool value)
    {
        Assert.Equal(new RewriteDecision(value, level), RewritePolicy.Resolve(DecidedAt(from, value)));
    }

    [Theory]
    [InlineData("default", false, false)]
    [InlineData("default", true, true)]
    [InlineData("on", false, true)]
    [InlineData("off", true, false)]
    [InlineData("ON", false, true)]
    [InlineData(" Off ", true, false)]
    [InlineData(null, true, true)]
    [InlineData("nonsense", true, true)]
    [InlineData("nonsense", false, false)]
    public void AGenerationsOwnChoiceWinsOverTheMainSwitchAndAnythingElseSaysNothing(string mode, bool master, bool expected)
    {
        RewriteSettings settings = RewriteSettings.Default with { Auto = master };
        Assert.Equal(expected, RewritePolicy.Resolve(RewritePolicy.Layers(RewriteScope.Generate, mode, null, settings)).Run);
    }

    [Fact]
    public void ChatAndAssistantSwitchesCountOnlyForTheImageToolAndEachEntryPointHasItsOwnSwitch()
    {
        RewriteSettings settings = RewriteSettings.Default with { ChatEntry = true, GenerateEntry = false };
        RewriteLayers chat = RewritePolicy.Layers(new RewriteScope(RewriteEntryPoints.Chat, false, true), "default", null, settings);
        Assert.Equal(new RewriteLayers(null, false, true, null, true, settings.Auto), chat);
        RewriteLayers generate = RewritePolicy.Layers(new RewriteScope(RewriteEntryPoints.Generate, false, true), "default", null, settings);
        Assert.Equal(new RewriteLayers(null, null, null, null, false, settings.Auto), generate);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, false)]
    [InlineData(false, null)]
    [InlineData(null, true)]
    public void AScopeTravelsInTheMetadataAndBack(bool? chat, bool? assistant)
    {
        RewriteScope scope = new(RewriteEntryPoints.Chat, chat, assistant);
        Assert.Equal(scope, RewriteScope.FromMeta(scope.ToMeta()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chat")]
    [InlineData("generate on on")]
    [InlineData("chat on on extra")]
    [InlineData(42)]
    public void AGenerationWithNoScopeCountsAsTheGenerateTab(object meta)
    {
        Assert.Equal(RewriteScope.Generate, RewriteScope.FromMeta(meta));
    }

    [Fact]
    public void TheMainSwitchDefaultsToOffAndComesFromOneConstant()
    {
        Assert.False(RewriteSettings.DefaultAuto);
        Assert.Equal(RewriteSettings.DefaultAuto, RewriteSettings.Default.Auto);
        Assert.Equal(RewriteSettings.DefaultAuto, RewriteSettings.From(JObject.Parse("""{"promptRewrite":{"auto":"yes"}}""")).Auto);
    }

    [Fact]
    public void TheEntryPointSwitchesAreReadFromTheSettingsAndAnythingElseFollowsTheMainSwitch()
    {
        RewriteSettings read = RewriteSettings.From(JObject.Parse("""{"promptRewrite":{"entryPoints":{"chat":"on","generate":false}}}"""));
        Assert.True(read.ChatEntry);
        Assert.False(read.GenerateEntry);
        foreach (string json in new[] { """{"promptRewrite":{}}""", """{"promptRewrite":{"entryPoints":{"chat":"default","generate":3}}}""", """{"promptRewrite":{"entryPoints":"on"}}""" })
        {
            RewriteSettings none = RewriteSettings.From(JObject.Parse(json));
            Assert.Null(none.ChatEntry);
            Assert.Null(none.GenerateEntry);
        }
    }

    [Fact]
    public void AStyleNamedForTheGenerationOrNoModelLeavesTheModelLevelSilent()
    {
        Assert.Null(RewritePolicy.ModelSwitch(null, "some-model", "generic"));
        Assert.Null(RewritePolicy.ModelSwitch(null, "", ""));
    }

    [Fact]
    public void AnAssistantsSwitchIsInheritedUntilAChildSaysOtherwise()
    {
        string parent = "p" + Guid.NewGuid().ToString("N")[..8];
        string inherits = "c" + Guid.NewGuid().ToString("N")[..8];
        string overrides = "o" + Guid.NewGuid().ToString("N")[..8];
        string silent = "s" + Guid.NewGuid().ToString("N")[..8];
        JObject settings = JObject.Parse("{\"assistants\":{\"" + parent + "\":{\"name\":\"P\",\"autoEnhance\":false},\"" + inherits + "\":{\"name\":\"C\",\"extends\":\"" + parent
            + "\",\"autoEnhance\":null},\"" + overrides + "\":{\"name\":\"O\",\"extends\":\"" + parent + "\",\"autoEnhance\":true},\"" + silent + "\":{\"name\":\"S\"}}}");
        Assert.False(AssistantResolver.Resolve(parent, null, settings).AutoEnhance);
        Assert.False(AssistantResolver.Resolve(inherits, null, settings).AutoEnhance);
        Assert.True(AssistantResolver.Resolve(overrides, null, settings).AutoEnhance);
        Assert.Null(AssistantResolver.Resolve(silent, null, settings).AutoEnhance);
    }
}
