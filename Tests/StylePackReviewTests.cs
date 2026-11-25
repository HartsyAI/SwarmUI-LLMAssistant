using System.IO;
using System.Runtime.CompilerServices;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Review fixes for style pack 1: a plain MOSS caption that starts with a tag, prompts that carry a user's bbox tag, formatters that never throw, and the style ids checked against core's own tables.</summary>
public class StylePackReviewTests
{
    private static readonly RewriteSettings Settings = new("", 5, 2048, 0.4);

    private static PromptStyle Style(string formatter, string format = "json", string negative = "none")
        => new(formatter, "image", formatter, "d", [], format, formatter, negative, [], [], "", "Write a prompt.", "bundled");

    private static RewriteRequest Request(string prompt, int w = 0, int h = 0) => new(prompt, "", "img.safetensors", w, h, "m", "", true);

    private sealed class Scripted(params string[] replies)
    {
        public int Calls;

        public Task<LLMGenerateResult> Ask(string system, string user, CancellationToken ct)
            => Task.FromResult(LLMGenerateResult.FromText(replies[Math.Min(Interlocked.Increment(ref Calls) - 1, replies.Length - 1)]));
    }

    private static string RepoAssets([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here)) ?? "", "Assets", "prompt-assets");

    // ---- 1: a MOSS plain caption that starts with a tag is not a layout draft ----

    [Fact]
    public void AMossCaptionThatStartsWithATagPlaceholderIsAPlainCaption()
    {
        MossImageFormatter formatter = new();
        FormatOutcome outcome = formatter.Normalize("{{SWARM_TAG_0}} a rain-soaked night street, yellow bicycle at a lamp post", Style("moss-image", "text"), 1024, 1024);
        Assert.Null(outcome.Error);
        Assert.Equal("{{SWARM_TAG_0}} a rain-soaked night street, yellow bicycle at a lamp post", outcome.Text);
        Assert.False(formatter.EscapesTags(outcome.Text));
        Assert.False(formatter.EscapesTags("  " + outcome.Text));
        Assert.True(formatter.EscapesTags("""{"width":1024}"""));
    }

    [Fact]
    public async Task AMossRewriteOfAPromptThatStartsWithATagKeepsTheTagRaw()
    {
        Scripted model = new("{{SWARM_TAG_0}} a rain-soaked night street with a yellow bicycle.");
        RewriteResult result = await PromptRewriter.RunAsync(Style("moss-image", "text", "separate"), Request("<lora:night:0.8> night street", 1024, 1024), Settings, model.Ask, default);
        Assert.True(result.Rewritten);
        Assert.False(result.Retried);
        Assert.Equal("<lora:night:0.8> a rain-soaked night street with a yellow bicycle.", result.Text);
    }

    // ---- 2: a prompt with a user's bbox tag is left alone by the families that make JSON or a layout ----

    [Theory]
    [InlineData("ideogram-4")]
    [InlineData("fibo-2")]
    [InlineData("ming-image")]
    [InlineData("moss-image")]
    [InlineData("flux-3")]
    [InlineData("json")]
    public async Task AStructuredFamilyLeavesAPromptWithABboxTagAsWritten(string formatter)
    {
        Scripted model = new("should never be asked");
        PromptStyle style = formatter == "json" ? Style("", "json") : Style(formatter, formatter is "moss-image" or "flux-3" ? "text" : "json");
        RewriteResult result = await PromptRewriter.RunAsync(style, Request("a dog <bbox[0.1,0.1,0.5,0.5]:a brown dog> on a beach", 1024, 1024), Settings, model.Ask, default);
        Assert.False(result.Rewritten);
        Assert.False(result.FellBack);
        Assert.Equal("a dog <bbox[0.1,0.1,0.5,0.5]:a brown dog> on a beach", result.Text);
        Assert.Contains(result.Warnings, w => w.Contains("bbox"));
        Assert.Contains("left as written", result.Skipped);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task AProseOrTagFamilyStillRewritesAPromptWithABboxTagAndKeepsTheTag()
    {
        // Prose output is not JSON, so core's expansion of the tag cannot break it; the tag only has to come back untouched.
        Scripted model = new("A brown dog {{SWARM_TAG_0}} on a sunny beach.");
        RewriteResult result = await PromptRewriter.RunAsync(Style("", "text"), Request("a dog <bbox[0.1,0.1,0.5,0.5]:a brown dog> on a beach"), Settings, model.Ask, default);
        Assert.True(result.Rewritten);
        Assert.Equal("A brown dog <bbox[0.1,0.1,0.5,0.5]:a brown dog> on a sunny beach.", result.Text);
    }

    [Fact]
    public async Task OtherTagsInAStructuredFamilyStillGoThroughTheModel()
    {
        Scripted model = new("""{"short_description":"A dog {{SWARM_TAG_0}}."}""");
        RewriteResult result = await PromptRewriter.RunAsync(Style("fibo-2"), Request("a dog <lora:x:1>"), Settings, model.Ask, default);
        Assert.True(result.Rewritten);
        Assert.Equal("""{"short_description":"A dog <lora:x:1>."}""", result.Text);
    }

    [Fact]
    public void NoBundledStyleTellsTheWriterToCarryABboxTagOrSectionTags()
    {
        foreach (PromptStyle style in PromptStyleStore.Load(RepoAssets(), PromptAsset.ScopeBundled))
        {
            Assert.DoesNotContain("<bbox[...]", style.Body);
            Assert.DoesNotContain("bbox placeholder", style.Body);
            Assert.DoesNotContain("<videoswap>", style.Body);
            Assert.DoesNotContain("<refiner>", style.Body);
        }
    }

    // ---- 3: formatters never throw ----

    public static IEnumerable<object[]> Hostile()
    {
        yield return ["moss-image", """{"width":99999999999999999999,"height":1024,"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,10,10]}]}"""];
        yield return ["moss-image", """{"width":1024,"height":1024,"bbox_coord_space":["abs"],"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,10,10]}]}"""];
        yield return ["moss-image", """{"width":1024,"height":1024,"bbox_format":{"a":1},"summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,10,10]}]}"""];
        yield return ["moss-image", """{"width":1024.5,"height":"1024","summary":{"caption":"c"},"items":[{"label":"object","caption":"a","bbox":[0,0,1e999,10]}]}"""];
        yield return ["moss-image", """{"width":1024,"height":1024,"summary":[],"items":{"a":1}}"""];
        yield return ["ming-image", """{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[{"description":"d","coordinates":"cx: ٠.٥, cy: ٠.٥, w: ١, h: ١"}]}"""];
        yield return ["ming-image", """{"canvas_settings":{"aspect_ratio":"a","ambient_lighting":"l","image_style":"s"},"layers":[{"description":"d","coordinates":{"cx":0.5,"cy":0.5,"w":"1","h":1}}]}"""];
        yield return ["ming-image", """{"canvas_settings":[],"layers":"x"}"""];
        yield return ["fibo-2", """{"short_description":{"a":1},"objects":[1,2]}"""];
        yield return ["fibo-2", """{"short_description":"x","objects":[{"description":["a"]}]}"""];
        yield return ["flux-3", """A dog <dog_1>. [{"id":["dog_1"],"bbox":[0,0,500,500],"desc":"d"}]"""];
        yield return ["flux-3", """A dog <dog_1>. [{"id":"dog_1","bbox":[0,0,1e999,500],"desc":"d"}]"""];
        yield return ["ideogram-4", """{"compositional_deconstruction":{"background":"b","elements":[{"type":"obj","bbox":[0,0,99999999999999999999999,5],"desc":"d"}]}}"""];
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void OddInputIsRefusedOrFixedAndNeverThrows(string formatterId, string raw)
    {
        PromptStyle style = Style(formatterId, formatterId is "moss-image" or "flux-3" ? "text" : "json");
        IPromptFormatter formatter = PromptFormatters.For(style);
        FormatOutcome outcome = null;
        Exception thrown = Record.Exception(() => outcome = formatter.Normalize(raw, style, 1024, 1024));
        Assert.Null(thrown);
        Assert.True(outcome.Error is not null || outcome.Text is not null);
        // The call the rewrite makes is protected as well.
        Assert.NotNull(PromptFormatters.Safe(formatter, raw, style, 1024, 1024));
    }

    [Fact]
    public void AFormatterThatThrowsBecomesARefusalTheModelCanRetryFrom()
    {
        FormatOutcome outcome = PromptFormatters.Safe(new ThrowingFormatter(), "x", Style("t", "text"));
        Assert.Null(outcome.Text);
        Assert.Contains("could not be read", outcome.Error);
    }

    private sealed class ThrowingFormatter : IPromptFormatter
    {
        public string Id => "t";

        public bool ProducesJson => false;

        public FormatOutcome Normalize(string raw, PromptStyle style) => throw new InvalidCastException("boom");

        public string Wrap(string original) => null;
    }

    [Fact]
    public async Task ARewriteWithAHostileAnswerRetriesAndFallsBackInsteadOfThrowing()
    {
        Scripted model = new("""{"width":99999999999999999999,"height":1024,"summary":{"caption":"c"},"items":[]}""");
        RewriteResult result = await PromptRewriter.RunAsync(Style("moss-image", "text", "separate"), Request("a poster", 1024, 1024), Settings, model.Ask, default);
        Assert.True(result.FellBack);
        Assert.True(result.Retried);
        Assert.Equal("a poster", result.Text);
    }

    // ---- 4 and 5: the instructions only teach what the matched models can use ----

    [Fact]
    public void QwenAndHiDreamTeachNoTransparencyOrEditClauseTheirMatchedModelsCannotAllUse()
    {
        Dictionary<string, PromptStyle> by = PromptStyleStore.Load(RepoAssets(), PromptAsset.ScopeBundled).ToDictionary(s => s.Id);
        Assert.DoesNotContain("RGBA", by["qwen-image"].Body);
        // Qwen's edit classes are matched, so its edit clause stays; HiDream's edit class is not matched, so its clause is gone.
        Assert.Contains("class:qwen-image-edit", by["qwen-image"].Matches.Select(m => $"{m.Kind}:{m.Pattern}"));
        Assert.DoesNotContain("edit an existing picture", by["hidream"].Body);
        Assert.DoesNotContain("hidream-i1-edit", by["hidream"].Matches.Select(m => m.Pattern));
    }

    // ---- 7: the ids a style names are core's ----

    /// <summary>Core's tables, read from its own sorter. Falls back to nothing when the sorter cannot start without a host, and the test then says so rather than passing silently.</summary>
    private static (Dictionary<string, T2IModelClass> Classes, Dictionary<string, T2IModelCompatClass> Compats) Core()
    {
        if (T2IModelClassSorter.ModelClasses.Count == 0)
        {
            T2IModelClassSorter.Init();
        }
        return (T2IModelClassSorter.ModelClasses, T2IModelClassSorter.CompatClasses);
    }

    [Fact]
    public void EveryClassAndCompatIdAStyleNamesExistsInCore()
    {
        (Dictionary<string, T2IModelClass> classes, Dictionary<string, T2IModelCompatClass> compats) = Core();
        Assert.NotEmpty(classes);
        foreach (PromptStyle style in PromptStyleStore.Load(RepoAssets(), PromptAsset.ScopeBundled))
        {
            foreach (StyleMatch match in style.Matches)
            {
                if (match.Kind == "class")
                {
                    Assert.True(classes.ContainsKey(match.Pattern.ToLowerInvariant()), $"{style.Id}: core has no model class {match.Pattern}");
                }
                else if (match.Kind == "compat")
                {
                    Assert.True(compats.ContainsKey(match.Pattern.ToLowerInvariant()), $"{style.Id}: core has no compat class {match.Pattern}");
                }
            }
        }
    }

    [Fact]
    public void EachClassAStyleNamesIsInTheCompatClassTheTestsAssumeAndIsNotAnEditOrLoraClass()
    {
        (Dictionary<string, T2IModelClass> classes, _) = Core();
        // The hand-written table in StylePackOneTests pairs a class id with its compat id; check those pairs against core so a rename or a move fails here.
        foreach (object[] row in StylePackOneTests.Intended())
        {
            foreach ((string cls, string compat) in ((string Class, string Compat)[])row[1])
            {
                Assert.True(classes.TryGetValue(cls.ToLowerInvariant(), out T2IModelClass clazz), $"core has no class {cls}");
                Assert.Equal(compat, clazz.CompatClass?.ID, ignoreCase: true);
                Assert.False(clazz.IsLora, cls);
            }
        }
    }
}
