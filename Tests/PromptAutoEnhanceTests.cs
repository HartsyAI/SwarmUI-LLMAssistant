using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The parts of the generation hook that need no running core: when it runs, what it writes to the metadata, and the memory that stops a prompt being rewritten twice.</summary>
public class PromptAutoEnhanceTests
{
    [Theory]
    [InlineData("default", false, false)]
    [InlineData("default", true, true)]
    [InlineData("on", false, true)]
    [InlineData("on", true, true)]
    [InlineData("off", true, false)]
    [InlineData("off", false, false)]
    [InlineData("ON", false, true)]
    [InlineData(" Off ", true, false)]
    [InlineData(null, true, true)]
    [InlineData("nonsense", true, true)]
    [InlineData("nonsense", false, false)]
    public void AGenerationsOwnChoiceWinsOverTheSetting(string mode, bool setting, bool expected)
    {
        Assert.Equal(expected, PromptAutoEnhance.ShouldRun(mode, setting));
    }

    [Fact]
    public void TheAutomaticSettingIsOffUnlessTheUserTurnsItOn()
    {
        Assert.False(RewriteSettings.Default.Auto);
        Assert.False(RewriteSettings.From(null).Auto);
        Assert.False(RewriteSettings.From(JObject.Parse("""{"promptRewrite":{"model":"x"}}""")).Auto);
        Assert.False(RewriteSettings.From(JObject.Parse("""{"promptRewrite":{"auto":"yes"}}""")).Auto);
        Assert.True(RewriteSettings.From(JObject.Parse("""{"promptRewrite":{"auto":true}}""")).Auto);
    }

    private static RewriteResult Result(bool rewritten = true, bool fellBack = false, string style = "ideogram-4", List<string> warnings = null)
        => new("cat", "{\"a\":1}", "", rewritten, null, false, fellBack, style, style is null ? null : "bundled", "match", "why", "qwen", warnings ?? [], null);

    [Fact]
    public void TheMetadataKeepsTheOriginalPromptTheStyleTheModelAndWhatHappened()
    {
        Dictionary<string, string> meta = PromptAutoEnhance.BuildMeta(Result(warnings: ["one", "two"]));
        Assert.Equal("cat", meta[PromptAutoEnhance.MetaOriginal]);
        Assert.Equal("rewritten", meta[PromptAutoEnhance.MetaStatus]);
        Assert.Equal("ideogram-4 (bundled)", meta[PromptAutoEnhance.MetaStyle]);
        Assert.Equal("qwen", meta[PromptAutoEnhance.MetaModel]);
        Assert.Equal("one | two", meta[PromptAutoEnhance.MetaWarnings]);

        Assert.Equal("fallback", PromptAutoEnhance.BuildMeta(Result(rewritten: false, fellBack: true))[PromptAutoEnhance.MetaStatus]);
        Dictionary<string, string> plain = PromptAutoEnhance.BuildMeta(Result(rewritten: false) with { Model = "" });
        Assert.Equal("unchanged", plain[PromptAutoEnhance.MetaStatus]);
        Assert.DoesNotContain(PromptAutoEnhance.MetaModel, plain.Keys);
        Assert.DoesNotContain(PromptAutoEnhance.MetaWarnings, plain.Keys);
    }

    [Fact]
    public void APromptThisExtensionProducedIsRememberedPerUserAndIgnoringItsEnds()
    {
        RewriteGuard.Clear();
        Assert.False(RewriteGuard.WasProduced("u1", "A fluffy cat."));
        RewriteGuard.Remember("u1", "A fluffy cat.");
        Assert.True(RewriteGuard.WasProduced("u1", "  A fluffy cat.\n"));
        Assert.False(RewriteGuard.WasProduced("u2", "A fluffy cat."));
        Assert.False(RewriteGuard.WasProduced("u1", "A fluffy cat"));
        Assert.False(RewriteGuard.WasProduced("u1", ""));
        RewriteGuard.Remember("u1", "   ");
        Assert.False(RewriteGuard.WasProduced("u1", "   "));
    }

    [Fact]
    public void TheMemoryIsBoundedPerUser()
    {
        RewriteGuard.Clear();
        for (int i = 0; i < RewriteGuard.MaxPerUser + 10; i++)
        {
            RewriteGuard.Remember("u1", $"prompt {i}");
        }
        Assert.False(RewriteGuard.WasProduced("u1", "prompt 0"));
        Assert.True(RewriteGuard.WasProduced("u1", $"prompt {RewriteGuard.MaxPerUser + 9}"));
    }

    [Fact]
    public void AReportIsTakenOnceAndOnlyByItsId()
    {
        RewriteReports.Clear();
        RewriteResult result = Result();
        RewriteReports.Put("abc", result);
        Assert.Null(RewriteReports.Take("other"));
        Assert.Same(result, RewriteReports.Take("abc"));
        Assert.Null(RewriteReports.Take("abc"));
        RewriteReports.Put("", result);
        RewriteReports.Put("x", null);
        Assert.Null(RewriteReports.Take(""));
        Assert.Null(RewriteReports.Take("x"));
    }

    [Fact]
    public void TheReportStoreIsBounded()
    {
        RewriteReports.Clear();
        for (int i = 0; i < RewriteReports.MaxReports + 25; i++)
        {
            RewriteReports.Put($"id{i}", Result());
        }
        Assert.Null(RewriteReports.Take("id0"));
        Assert.NotNull(RewriteReports.Take($"id{RewriteReports.MaxReports + 24}"));
    }
}
