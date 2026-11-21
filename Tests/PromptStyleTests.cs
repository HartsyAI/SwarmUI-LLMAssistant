using System.IO;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Prompt styles: the file format, reading a folder, merging scopes, validating a style, and choosing a style for an image model and for a chat model.</summary>
public sealed class PromptStyleTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "llma-styles-" + Guid.NewGuid().ToString("N")[..8]);

    public PromptStyleTests()
    {
        Directory.CreateDirectory(_base);
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

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_base, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static PromptStyle Style(string id, string kind = "image", string scope = "bundled", string matches = "", string tasks = "")
        => new(id, kind, id, "d", PromptStyleFile.ParseMatches(matches, kind), "text", "", "none", [], [.. tasks.Split(',', StringSplitOptions.RemoveEmptyEntries)], "", "body", scope);

    private static ImageModelFacts Image(string name = "m.safetensors", string cls = "ideogram-4", string compat = "ideogram-4", bool video = false) => new(name, cls, cls, compat, video);

    private static LLMModelInfo Llm(string id, string family = null, string provider = null) => new() { Id = id, Family = family, Provider = provider };

    // ---- the file format ----

    [Fact]
    public void AStyleFileGivesItsFieldsAndBody()
    {
        PromptStyle style = PromptStyleFile.Parse("ideogram-4", "bundled",
            "---\nname: Ideogram 4\ndescription: JSON only\nkind: image\nmatches: class:ideogram-4, compat:ideogram-4\nformat: json\nformatter: Ideogram-4\nnegative: separate\nsupports: boxes, Text-Rendering, nonsense\nguide: docs/prompting/ideogram-4.md\n---\n\nWrite JSON.\n");
        Assert.Equal("Ideogram 4", style.Name);
        Assert.Equal("image", style.Kind);
        Assert.Equal(["class:ideogram-4", "compat:ideogram-4"], style.Matches.Select(m => $"{m.Kind}:{m.Pattern}"));
        Assert.Equal("json", style.Format);
        Assert.Equal("ideogram-4", style.Formatter);
        Assert.Equal("separate", style.Negative);
        Assert.Equal(["boxes", "text-rendering"], style.Supports);
        Assert.Equal("docs/prompting/ideogram-4.md", style.Guide);
        Assert.Equal("Write JSON.", style.Body);
        Assert.Equal("bundled", style.Scope);
    }

    [Fact]
    public void MissingOrUnknownValuesAreMadeSafeAndAnUnknownKindIsNotAStyle()
    {
        PromptStyle plain = PromptStyleFile.Parse("plain", "personal", "just text");
        Assert.Equal("image", plain.Kind);
        Assert.Equal("plain", plain.Name);
        Assert.Equal("text", plain.Format);
        Assert.Equal("none", plain.Negative);
        Assert.Empty(plain.Matches);
        Assert.Equal("text", PromptStyleFile.Parse("x", "personal", "---\nformat: yaml\n---\nb").Format);
        Assert.Null(PromptStyleFile.Parse("x", "personal", "---\nkind: skill\n---\nb"));
    }

    [Fact]
    public void ConditionsKeepOnlyKindsThatApplyAndSplitAtTheFirstColon()
    {
        List<StyleMatch> image = PromptStyleFile.ParseMatches("class:flux-1, family:qwen, glob:*pony*, nocolon, exact:", "image");
        Assert.Equal(["class:flux-1", "glob:*pony*"], image.Select(m => $"{m.Kind}:{m.Pattern}"));
        List<StyleMatch> llm = PromptStyleFile.ParseMatches("family:qwen2, glob:llama3:*, class:flux-1, PROVIDER:ollama", "llm");
        Assert.Equal(["family:qwen2", "glob:llama3:*", "provider:ollama"], llm.Select(m => $"{m.Kind}:{m.Pattern}"));
    }

    [Fact]
    public void AStyleSurvivesBeingWrittenAndRead()
    {
        PromptStyle original = new("my-style", "llm", "My style", "For quirky models", PromptStyleFile.ParseMatches("family:qwen2, glob:*coder*", "llm"), "tags", "", "inline",
            ["weights"], ["chat", "rewrite"], "docs/prompting/sdxl.md", "Line one.\n\nLine two: with a colon.", "personal");
        PromptStyle read = PromptStyleFile.Parse("my-style", "personal", PromptStyleFile.Format(original));
        Assert.Equal(original.Name, read.Name);
        Assert.Equal(original.Description, read.Description);
        Assert.Equal(original.Kind, read.Kind);
        Assert.Equal(original.Matches, read.Matches);
        Assert.Equal(original.Format, read.Format);
        Assert.Equal(original.Negative, read.Negative);
        Assert.Equal(original.Supports, read.Supports);
        Assert.Equal(original.Tasks, read.Tasks);
        Assert.Equal(original.Guide, read.Guide);
        Assert.Equal(original.Body, read.Body);
    }

    [Fact]
    public void AValueCannotBreakOutOfTheFrontMatter()
    {
        PromptStyle style = Style("evil") with { Name = "a\n---\nkind: llm", Description = "line1\nline2" };
        PromptStyle read = PromptStyleFile.Parse("evil", "personal", PromptStyleFile.Format(style));
        Assert.Equal("image", read.Kind);
        Assert.Equal("a --- kind: llm", read.Name);
        Assert.Equal("line1 line2", read.Description);
    }

    // ---- validating ----

    [Theory]
    [InlineData("Bad Id", "image", "", "body", "text", "none", "An id is")]
    [InlineData("ok", "skill", "", "body", "text", "none", "Choose image")]
    [InlineData("ok", "image", "", "   ", "text", "none", "instructions are empty")]
    [InlineData("ok", "image", "", "body", "yaml", "none", "format is")]
    [InlineData("ok", "image", "", "body", "text", "maybe", "negative prompt setting")]
    public void ABadStyleIsRefusedWithAReason(string id, string kind, string matches, string body, string format, string negative, string expected)
    {
        PromptStyle style = Style(id, PromptStyle.IsValidKind(kind) ? kind : "image") with { Kind = kind, Body = body, Format = format, Negative = negative, Matches = PromptStyleFile.ParseMatches(matches, "image") };
        Assert.Contains(expected, PromptStyleService.Validate(style));
    }

    [Fact]
    public void ValidateChecksConditionKindsAndLimits()
    {
        Assert.Null(PromptStyleService.Validate(Style("fine", matches: "class:flux-1")));
        PromptStyle wrongKind = Style("bad") with { Matches = [new StyleMatch("family", "qwen")] };
        Assert.Contains("cannot use the condition", PromptStyleService.Validate(wrongKind));
        PromptStyle comma = Style("bad") with { Matches = [new StyleMatch("glob", "a,b")] };
        Assert.Contains("cannot contain a comma", PromptStyleService.Validate(comma));
        Assert.Contains("up to 20 conditions", PromptStyleService.Validate(Style("bad") with { Matches = [.. Enumerable.Range(0, 21).Select(i => new StyleMatch("glob", "m" + i))] }));
        Assert.Contains("too long", PromptStyleService.Validate(Style("bad") with { Body = new string('x', PromptAsset.MaxFileBytes) }));
        Assert.Contains("honours", PromptStyleService.Validate(Style("bad") with { Supports = ["nonsense"] }));
        Assert.Contains("A task can be", PromptStyleService.Validate(Style("bad", "llm") with { Tasks = ["nonsense"] }));
    }

    // ---- reading a folder, and merging scopes ----

    [Fact]
    public void StylesAreReadFromTheStylesFolderInIdOrder()
    {
        Write("root/styles/zeta.md", "---\nkind: llm\n---\nZ");
        Write("root/styles/alpha.md", "---\nkind: image\nmatches: class:flux-1\n---\nA");
        Write("root/styles/Bad Name.md", "x");
        Write("root/styles/notes.txt", "x");
        Write("root/styles/notastyle.md", "---\nkind: skill\n---\nx");
        Write("root/skills/some-skill/SKILL.md", "---\ndescription: a skill\n---\nbody");
        List<PromptStyle> styles = PromptStyleStore.Load(Path.Combine(_base, "root"), "shared");
        Assert.Equal(["alpha", "zeta"], styles.Select(s => s.Id));
        Assert.All(styles, s => Assert.Equal("shared", s.Scope));
        // The styles are not skills or commands, so the skill loader does not see them (and a skill is not a style).
        Assert.Equal(["some-skill"], PromptAssetStore.Load(Path.Combine(_base, "root"), "shared").Select(a => a.Name));
    }

    [Fact]
    public void AChangedStyleFileIsReadAgainAndMissingFoldersOfferNothing()
    {
        string path = Write("root/styles/c.md", "first");
        Assert.Equal("first", PromptStyleStore.Load(Path.Combine(_base, "root"), "shared").Single().Body);
        File.WriteAllText(path, "second version");
        Assert.Equal("second version", PromptStyleStore.Load(Path.Combine(_base, "root"), "shared").Single().Body);
        File.Delete(path);
        Assert.Empty(PromptStyleStore.Load(Path.Combine(_base, "root"), "shared"));
        Assert.Empty(PromptStyleStore.Load(Path.Combine(_base, "missing"), "shared"));
        Assert.Empty(PromptStyleStore.Load(null, "shared"));
    }

    [Fact]
    public void AStyleOverTheSizeLimitIsSkipped()
    {
        Write("root/styles/big.md", new string('a', PromptAsset.MaxFileBytes + 1));
        Write("root/styles/ok.md", "fine");
        Assert.Equal(["ok"], PromptStyleStore.Load(Path.Combine(_base, "root"), "shared").Select(s => s.Id));
    }

    [Fact]
    public void ThePersonalCopyOfAStyleReplacesTheBundledOneAndTheScopesAreKnown()
    {
        PromptStyleSet set = PromptStyleService.Merge(
            [Style("one", scope: "bundled"), Style("two", scope: "bundled")],
            [Style("one", scope: "shared")],
            [Style("one", scope: "personal"), Style("three", scope: "personal")]);
        Assert.Equal(["one", "three", "two"], set.Styles.Select(s => s.Id));
        Assert.Equal("personal", set.Get("one").Scope);
        Assert.Equal("bundled", set.Get("two").Scope);
        Assert.Equal(["bundled", "shared", "personal"], set.ScopesOf("one"));
        Assert.Equal(["bundled"], set.ScopesOf("two"));
    }

    // ---- choosing a style for an image model ----

    [Fact]
    public void TheStyleWrittenForTheModelsClassWinsOverTheDefault()
    {
        List<PromptStyle> styles = [Style("generic"), Style("ideogram-4", matches: "class:ideogram-4, compat:ideogram-4")];
        StyleResolution resolution = PromptStyleResolver.ResolveImage(styles, [], Image());
        Assert.Equal("ideogram-4", resolution.Style.Id);
        Assert.Equal("match", resolution.Via);
        Assert.Equal("class:ideogram-4", resolution.Candidates[0].Matched);
        StyleResolution other = PromptStyleResolver.ResolveImage(styles, [], Image("sd.safetensors", "sdxl", "sdxl"));
        Assert.Equal("generic", other.Style.Id);
        Assert.Equal("default", other.Via);
    }

    // ---- what a model file says about itself ----

    [Fact]
    public void AHeaderGivesItsPromptFactsAndDropsWhatIsNotInTheVocabulary()
    {
        ModelPromptFacts facts = ModelPromptFacts.FromMetadata(JObject.Parse("""
            {"modelspec.prompt_format":"JSON","modelspec.prompt_negative":"separate","modelspec.prompt_supports":"boxes, Text-Rendering, bogus, boxes",
             "modelspec.prompt_boxes":"YXYX-1000","modelspec.prompt_encoder":"t5, clip, nonsense"}
            """));
        Assert.NotNull(facts);
        Assert.Equal("json", facts.Format);
        Assert.Equal("separate", facts.Negative);
        Assert.Equal(["boxes", "text-rendering"], facts.Supports);
        Assert.Equal("yxyx-1000", facts.Boxes);
        Assert.Equal(["t5", "clip"], facts.Encoders);
        Assert.True(facts.Has("boxes"));
        Assert.False(facts.Has("weights"));
    }

    [Theory]
    [InlineData("""{"modelspec.title":"x"}""")]
    [InlineData("""{"modelspec.prompt_format":"xml","modelspec.prompt_negative":7,"modelspec.prompt_boxes":"zzzz-1000"}""")]
    [InlineData("""{"modelspec.prompt_boxes":"yxyx-0"}""")]
    public void AHeaderWithNoUsableKeyGivesNoFacts(string json)
    {
        Assert.Null(ModelPromptFacts.FromMetadata(JObject.Parse(json)));
    }

    [Fact]
    public void AnEmptyOrMissingHeaderGivesNoFactsAndAnEmptySupportsListMeansNone()
    {
        Assert.Null(ModelPromptFacts.FromMetadata(null));
        Assert.Null(ModelPromptFacts.FromMetadata([]));
        ModelPromptFacts none = ModelPromptFacts.FromMetadata(JObject.Parse("""{"modelspec.prompt_supports":""}"""));
        Assert.NotNull(none);
        Assert.Empty(none.Supports);
        Assert.Null(none.Format);
        Assert.False(none.Has("boxes"));
    }

    [Fact]
    public void AValueThatIsTooLongIsIgnored()
    {
        Assert.Null(ModelPromptFacts.FromMetadata(new JObject { ["modelspec.prompt_format"] = new string('a', ModelPromptFacts.MaxValueChars + 1) }));
    }

    [Fact]
    public void AStyleCanMatchTheFormatAModelDeclaresBelowItsClassAndAboveItsCompatClass()
    {
        PromptStyle byFormat = Style("any-json", matches: "format:json");
        PromptStyle cls = Style("by-class", matches: "class:sdxl");
        PromptStyle compat = Style("by-compat", matches: "compat:sdxl");
        ModelPromptFacts json = new("json", null, null, null, null);
        ImageModelFacts declared = Image("x.safetensors", "sdxl", "sdxl") with { Prompt = json };
        Assert.Equal("by-class", PromptStyleResolver.ResolveImage([byFormat, cls, compat], [], declared).Style.Id);
        Assert.Equal("any-json", PromptStyleResolver.ResolveImage([byFormat, compat], [], declared).Style.Id);
        Assert.Equal("format:json", PromptStyleResolver.ResolveImage([byFormat], [], declared).Candidates[0].Matched);
        // A model that declares nothing, or another format, is not matched.
        Assert.Empty(PromptStyleResolver.ResolveImage([byFormat], [], Image("x.safetensors", "sdxl", "sdxl")).Candidates);
        Assert.Empty(PromptStyleResolver.ResolveImage([byFormat], [], declared with { Prompt = new ModelPromptFacts("tags", null, null, null, null) }).Candidates);
        // The condition is allowed in an image style file, not in a chat-model one.
        Assert.Equal(["format:json"], PromptStyleFile.ParseMatches("format:json", "image").Select(m => $"{m.Kind}:{m.Pattern}"));
        Assert.Empty(PromptStyleFile.ParseMatches("format:json", "llm"));
    }

    [Fact]
    public void ConditionsRankExactThenGlobThenClassThenCompat()
    {
        PromptStyle exact = Style("by-exact", matches: "exact:Pony/pony-v6.safetensors");
        PromptStyle glob = Style("by-glob", matches: "glob:*pony*");
        PromptStyle cls = Style("by-class", matches: "class:sdxl");
        PromptStyle compat = Style("by-compat", matches: "compat:sdxl");
        ImageModelFacts pony = Image("Pony/pony-v6.safetensors", "sdxl", "sdxl");
        Assert.Equal("by-exact", PromptStyleResolver.ResolveImage([compat, cls, glob, exact], [], pony).Style.Id);
        Assert.Equal("by-glob", PromptStyleResolver.ResolveImage([compat, cls, glob], [], pony).Style.Id);
        Assert.Equal("by-class", PromptStyleResolver.ResolveImage([compat, cls], [], pony).Style.Id);
        Assert.Equal("by-compat", PromptStyleResolver.ResolveImage([compat], [], pony).Style.Id);
        Assert.Equal(["by-exact", "by-glob", "by-class", "by-compat"], PromptStyleResolver.ResolveImage([compat, cls, glob, exact], [], pony).Candidates.Select(c => c.Id));
    }

    [Fact]
    public void EqualConditionsGoToTheMorePersonalScopeThenTheSmallerId()
    {
        PromptStyle bundled = Style("b-style", scope: "bundled", matches: "class:sdxl");
        PromptStyle personal = Style("z-style", scope: "personal", matches: "class:sdxl");
        PromptStyle shared = Style("a-style", scope: "shared", matches: "class:sdxl");
        Assert.Equal("z-style", PromptStyleResolver.ResolveImage([bundled, shared, personal], [], Image("m", "sdxl", "sdxl")).Style.Id);
        Assert.Equal("a-style", PromptStyleResolver.ResolveImage([bundled, shared, Style("b-other", scope: "shared", matches: "class:sdxl")], [], Image("m", "sdxl", "sdxl")).Style.Id);
    }

    [Fact]
    public void AVideoModelPrefersTheVideoDefaultAndFallsBackToTheImageOne()
    {
        PromptStyle imageDefault = Style("generic");
        PromptStyle videoDefault = Style("generic-video", kind: "video");
        Assert.Equal("generic-video", PromptStyleResolver.ResolveImage([imageDefault, videoDefault], [], Image("v", "wan-21", "wan-21", video: true)).Style.Id);
        Assert.Equal("generic", PromptStyleResolver.ResolveImage([imageDefault], [], Image("v", "wan-21", "wan-21", video: true)).Style.Id);
        Assert.Equal("generic", PromptStyleResolver.ResolveImage([imageDefault, videoDefault], [], Image("i", "sdxl", "sdxl")).Style.Id);
    }

    [Fact]
    public void NoStyleAtAllGivesNoneAndAModelCoreCouldNotClassifyMatchesOnlyByName()
    {
        Assert.Null(PromptStyleResolver.ResolveImage([], [], Image()).Style);
        Assert.Equal("none", PromptStyleResolver.ResolveImage([], [], Image()).Via);
        PromptStyle byClass = Style("by-class", matches: "class:ideogram-4");
        PromptStyle byName = Style("by-name", matches: "glob:*mystery*");
        ImageModelFacts unknown = new("mystery.safetensors", null, null, null, false);
        Assert.Equal("by-name", PromptStyleResolver.ResolveImage([byClass, byName], [], unknown).Style.Id);
        Assert.Null(PromptStyleResolver.ResolveImage([byClass], [], unknown).Style);
        Assert.Null(PromptStyleResolver.ResolveImage([byClass], [], null).Style);
    }

    [Fact]
    public void ARuleBeatsTheStylesOwnConditionsAnExactNameBeatsAPatternAndOffTurnsStylesOff()
    {
        List<PromptStyle> styles = [Style("generic"), Style("ideogram-4", matches: "class:ideogram-4"), Style("mine")];
        ImageModelFacts model = Image("Ideo/ideogram4.safetensors");
        StyleResolution byGlob = PromptStyleResolver.ResolveImage(styles, [new StyleOverride("image", "*ideogram*", "generic")], model);
        Assert.Equal("generic", byGlob.Style.Id);
        Assert.Equal("override", byGlob.Via);
        StyleResolution exactWins = PromptStyleResolver.ResolveImage(styles, [new StyleOverride("image", "*ideogram*", "generic"), new StyleOverride("image", "ideo/IDEOGRAM4.safetensors", "mine")], model);
        Assert.Equal("mine", exactWins.Style.Id);
        StyleResolution off = PromptStyleResolver.ResolveImage(styles, [new StyleOverride("image", "*ideogram*", "off")], model);
        Assert.True(off.Off);
        Assert.Null(off.Style);
        // Another axis's rule, or a rule for another model, changes nothing.
        Assert.Equal("ideogram-4", PromptStyleResolver.ResolveImage(styles, [new StyleOverride("llm", "*ideogram*", "mine"), new StyleOverride("image", "*pony*", "mine")], model).Style.Id);
    }

    [Fact]
    public void ARuleNamingAMissingStyleIsIgnoredAndSaysSo()
    {
        List<PromptStyle> styles = [Style("generic")];
        StyleResolution resolution = PromptStyleResolver.ResolveImage(styles, [new StyleOverride("image", "*", "deleted-style")], Image());
        Assert.Equal("generic", resolution.Style.Id);
        Assert.Contains("does not exist", resolution.Reason);
    }

    // ---- choosing a style for a chat model ----

    [Fact]
    public void AFamilyConditionBeatsANameGlobAndAnExactIdBeatsBoth()
    {
        PromptStyle family = Style("family", "llm", matches: "family:qwen2");
        PromptStyle glob = Style("glob", "llm", matches: "glob:qwen*");
        PromptStyle exact = Style("exact", "llm", matches: "exact:qwen2.5-7b");
        LLMModelInfo model = Llm("qwen2.5-7b", "qwen2");
        Assert.Equal("exact", PromptStyleResolver.ResolveLlm([glob, family, exact], [], model).Style.Id);
        Assert.Equal("family", PromptStyleResolver.ResolveLlm([glob, family], [], model).Style.Id);
        Assert.Equal("glob", PromptStyleResolver.ResolveLlm([glob], [], model).Style.Id);
    }

    [Fact]
    public void AModelWithNoKnownFamilyIsStillMatchedByItsNameGlob()
    {
        PromptStyle family = Style("family", "llm", matches: "family:claude");
        PromptStyle glob = Style("glob", "llm", matches: "glob:anthropic/claude-*");
        LLMModelInfo gateway = Llm("anthropic/claude-sonnet-5", family: null);
        Assert.Equal("glob", PromptStyleResolver.ResolveLlm([family, glob], [], gateway).Style.Id);
        Assert.Equal("family", PromptStyleResolver.ResolveLlm([family, glob], [], Llm("claude-sonnet-5", "claude")).Style.Id);
    }

    [Fact]
    public void AChatStyleLimitedToATaskAppliesOnlyToThatTaskAndTheDefaultIsTheLastResort()
    {
        PromptStyle rewrite = Style("rewrite-only", "llm", matches: "family:qwen2", tasks: "rewrite");
        PromptStyle fallback = Style("fallback", "llm");
        LLMModelInfo model = Llm("m", "qwen2");
        Assert.Equal("rewrite-only", PromptStyleResolver.ResolveLlm([rewrite, fallback], [], model, "rewrite").Style.Id);
        StyleResolution chat = PromptStyleResolver.ResolveLlm([rewrite, fallback], [], model, "chat");
        Assert.Equal("fallback", chat.Style.Id);
        Assert.Equal("default", chat.Via);
        Assert.Equal("rewrite-only", PromptStyleResolver.ResolveLlm([rewrite, fallback], [], model).Style.Id);
        Assert.Null(PromptStyleResolver.ResolveLlm([rewrite], [], model, "chat").Style);
    }

    [Fact]
    public void AChatModelRuleWorksOnTheModelIdAndImageStylesAreNeverChosenForAChatModel()
    {
        List<PromptStyle> styles = [Style("image-one"), Style("chat-one", "llm", matches: "family:qwen2"), Style("chat-two", "llm")];
        LLMModelInfo model = Llm("qwen2.5-7b", "qwen2");
        Assert.Equal("chat-two", PromptStyleResolver.ResolveLlm(styles, [new StyleOverride("llm", "qwen*", "chat-two")], model).Style.Id);
        Assert.True(PromptStyleResolver.ResolveLlm(styles, [new StyleOverride("llm", "qwen*", "off")], model).Off);
        // A rule naming an image style does not apply to a chat model: the style is not one of its kind, so the rule is ignored.
        StyleResolution wrongKind = PromptStyleResolver.ResolveLlm(styles, [new StyleOverride("llm", "qwen*", "image-one")], model);
        Assert.Equal("chat-one", wrongKind.Style.Id);
    }
}
