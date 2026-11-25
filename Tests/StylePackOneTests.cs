using System.IO;
using System.Runtime.CompilerServices;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Style pack 1: the bundled image and video styles for more model families. Each loads, is valid and small, matches the class ids it was written for and no others, and loses to a user's own rule.
/// The class and compat ids below are core's, copied from its model class table, so a typo in a style's <c>matches</c> line fails here.</summary>
public sealed class StylePackOneTests
{
    /// <summary>The folder that holds the bundled <c>styles/</c> folder, found from this source file so no build output layout matters.</summary>
    private static readonly string BundledRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ThisFile())) ?? "", "Assets", "prompt-assets");

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private static readonly string RepoRoot = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile())) ?? "";

    private static List<PromptStyle> Bundled() => PromptStyleStore.Load(BundledRoot, PromptAsset.ScopeBundled);

    private static ImageModelFacts Model(string classId, string compat, bool video = false) => new($"{classId}.safetensors", classId, classId, compat, video);

    /// <summary>The style id, then each (class id, compat id) pair of core that it is written for.</summary>
    public static IEnumerable<object[]> Intended()
    {
        yield return Row("flux-3", ("flux-3-image-klein", "flux-3-image-klein"));
        yield return Row("fibo-2", ("fibo-2", "fibo-2"));
        yield return Row("ming-image", ("ming-image", "ming-image"));
        yield return Row("moss-image", ("moss-image", "moss-image"));
        yield return Row("qwen-image", ("qwen-image", "qwen-image"), ("qwen-image-edit", "qwen-image"), ("qwen-image-edit-plus", "qwen-image"), ("qwen-image-2.1", "qwen-image-2.1"));
        yield return Row("flux-1", ("Flux.1-dev", "flux-1"), ("Flux.1-schnell", "flux-1"));
        yield return Row("flux-2", ("flux.2-dev", "flux-2"), ("flux.2-klein-4b", "flux-2-klein-4b"), ("flux.2-klein-9b", "flux-2-klein-9b"));
        yield return Row("z-image", ("z-image", "z-image"));
        yield return Row("hidream", ("hidream-i1", "hidream-i1"), ("hidream-o1", "hidream-o1"));
        yield return Row("sd3", ("stable-diffusion-v3-medium", "stable-diffusion-v3-medium"), ("stable-diffusion-v3.5-large", "stable-diffusion-v3.5-large"),
            ("stable-diffusion-v3.5-medium", "stable-diffusion-v3.5-medium"));
        yield return Row("sdxl", ("stable-diffusion-xl-v1-base", "stable-diffusion-xl-v1"), ("stable-diffusion-xl-v0_9-base", "stable-diffusion-xl-v1"),
            ("stable-diffusion-xl-turbo-v1", "stable-diffusion-xl-v1"), ("stable-diffusion-xl-v1-refiner", "stable-diffusion-xl-v1-refiner"));
        yield return Row("sd1-sd2", ("stable-diffusion-v1", "stable-diffusion-v1"), ("stable-diffusion-v2-512", "stable-diffusion-v2"), ("stable-diffusion-v2-768-v", "stable-diffusion-v2"),
            ("stable-diffusion-v2-turbo", "stable-diffusion-v2-turbo"));
        yield return Row("chroma", ("chroma", "chroma"), ("chroma-radiance", "chroma-radiance"));
        yield return Row("video-prose", ("wan-2_1-text2video-14b", "wan-21-14b"), ("wan-2_1-image2video-1_3b", "wan-21-1_3b"), ("wan-2_2-ti2v-5b", "wan-22-5b"),
            ("wan-2_2-image2video-14b", "wan-21-14b"), ("hunyuan-video", "hunyuan-video"), ("hunyuan-video-1_5", "hunyuan-video-1_5"));
        yield return Row("ideogram-4", ("ideogram-4", "ideogram-4"));
    }

    private static object[] Row(string id, params (string Class, string Compat)[] models) => [id, models];

    /// <summary>Models core knows that must not get a pack style: edits and fills that are not text-to-image, a distilled SDXL that lacks the legacy weight parser, and families whose guides are thin.</summary>
    public static IEnumerable<object[]> Unrelated()
    {
        foreach ((string cls, string compat) in new[]
        {
            ("Flux.1-dev/kontext", "flux-1"), ("Flux.1-dev/inpaint", "flux-1"), ("Flux.1-dev/canny", "flux-1"),
            ("stable-diffusion-xl-v1-edit", "stable-diffusion-xl-v1"), ("stable-diffusion-v1-inpainting", "stable-diffusion-v1"),
            ("segmind-stable-diffusion-1b", "segmind-stable-diffusion-1b"), ("alt_diffusion_v1_512_placeholder", "alt_diffusion_v1"),
            ("zeta-chroma", "zeta-chroma"), ("hidream-i1-edit", "hidream-i1"), ("lumina-2", "lumina-2"), ("krea-2", "krea-2"), ("stable-cascade-v1-stage-c", "stable-cascade-v1"),
            ("auraflow-v1", "auraflow-v1"), ("nvidia-sana-1600", "nvidia-sana-1600"),
        })
        {
            yield return [cls, compat];
        }
    }

    [Fact]
    public void TheBundledFolderHoldsTheTwoOldStylesAndFourteenNewOnes()
    {
        Assert.Equal(["chroma", "fibo-2", "flux-1", "flux-2", "flux-3", "generic", "hidream", "ideogram-4", "ming-image", "moss-image", "qwen-image", "sd1-sd2", "sd3", "sdxl", "video-prose", "z-image"],
            Bundled().Select(s => s.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryNewStyleIsValidSmallAndPointsAtItsGuide()
    {
        foreach (PromptStyle style in Bundled().Where(s => s.Id is not ("generic" or "ideogram-4")))
        {
            Assert.True(PromptStyleService.Validate(style) is null, $"{style.Id}: {PromptStyleService.Validate(style)}");
            Assert.Equal(PromptAsset.ScopeBundled, style.Scope);
            Assert.True(style.IsTargetStyle, style.Id);
            Assert.NotEmpty(style.Matches);
            long bytes = new FileInfo(Path.Combine(BundledRoot, "styles", style.Id + ".md")).Length;
            Assert.True(bytes < PromptAsset.MaxFileBytes / 8, $"{style.Id} is {bytes} bytes; a style is concise, well under the {PromptAsset.MaxFileBytes} byte cap");
            Assert.StartsWith("docs/prompting/", style.Guide);
            Assert.True(File.Exists(Path.Combine(RepoRoot, style.Guide.Replace('/', Path.DirectorySeparatorChar))), $"{style.Id}: guide {style.Guide} does not exist");
            if (!string.IsNullOrEmpty(style.Formatter))
            {
                Assert.True(PromptFormatters.Exists(style.Formatter), $"{style.Id}: unknown formatter {style.Formatter}");
            }
        }
    }

    [Fact]
    public void EveryNewStyleCarriesTheSwarmSyntaxRules()
    {
        foreach (PromptStyle style in Bundled().Where(s => s.Id is not ("generic" or "ideogram-4")))
        {
            Assert.Contains("<lora:name:0.8>", style.Body);
            Assert.Contains("__name__", style.Body);
            Assert.Contains("Never invent tags", style.Body);
            Assert.DoesNotContain("{{", style.Body);
        }
    }

    [Fact]
    public void FormatsAndFormattersAgreeWithWhatEachStyleTeaches()
    {
        Dictionary<string, PromptStyle> by = Bundled().ToDictionary(s => s.Id);
        Assert.Equal("json", by["fibo-2"].Format);
        Assert.Equal("json", by["ming-image"].Format);
        Assert.Equal("fibo-2", by["fibo-2"].Formatter);
        Assert.Equal("ming-image", by["ming-image"].Formatter);
        Assert.Equal("flux-3", by["flux-3"].Formatter);
        Assert.Equal("moss-image", by["moss-image"].Formatter);
        Assert.Equal("tags", by["sdxl"].Format);
        Assert.Equal("tags", by["sd1-sd2"].Format);
        Assert.Contains("weights", by["sdxl"].Supports);
        Assert.Contains("weights", by["sd1-sd2"].Supports);
        Assert.Contains("(phrase:1.2)", by["sdxl"].Body);
        Assert.Equal(PromptStyle.KindVideo, by["video-prose"].Kind);
        foreach (string text in new[] { "qwen-image", "flux-1", "flux-2", "z-image", "hidream", "sd3", "chroma", "video-prose" })
        {
            Assert.Equal("text", by[text].Format);
            Assert.Empty(by[text].Formatter);
        }
        Assert.All(new[] { "flux-1", "flux-2", "flux-3", "fibo-2", "ming-image" }, id => Assert.Equal("none", by[id].Negative));
        Assert.All(new[] { "sdxl", "sd1-sd2", "sd3", "z-image", "chroma", "qwen-image" }, id => Assert.Equal("separate", by[id].Negative));
        // The JSON and layout families do not say they honour weights, and none of the prose families does.
        Assert.All(by.Values.Where(s => s.Id is not ("sdxl" or "sd1-sd2")), s => Assert.DoesNotContain("weights", s.Supports));
    }

    [Theory]
    [MemberData(nameof(Intended))]
    public void EachFamilyGetsItsStyleAndNoOtherFamilyDoes(string id, (string Class, string Compat)[] models)
    {
        List<PromptStyle> styles = Bundled();
        foreach ((string cls, string compat) in models)
        {
            StyleResolution resolution = PromptStyleResolver.ResolveImage(styles, [], Model(cls, compat, video: id == "video-prose"));
            Assert.True(resolution.Style?.Id == id, $"{cls} resolved to {resolution.Style?.Id} ({resolution.Reason}), expected {id}");
            Assert.Equal("match", resolution.Via);
            Assert.Single(resolution.Candidates);
        }
    }

    [Theory]
    [MemberData(nameof(Unrelated))]
    public void ModelsOutsideThePackKeepTheDefault(string cls, string compat)
    {
        StyleResolution resolution = PromptStyleResolver.ResolveImage(Bundled(), [], Model(cls, compat));
        Assert.Equal("generic", resolution.Style.Id);
        Assert.Equal("default", resolution.Via);
    }

    [Fact]
    public void OtherVideoModelsKeepTheDefaultToo()
    {
        foreach ((string cls, string compat) in new[] { ("lightricks-ltx-video", "lightricks-ltx-video"), ("genmo-mochi-1", "genmo-mochi-1"), ("nvidia-cosmos-1-7b-text2world", "nvidia-cosmos-1"),
            ("minimax-h3", "minimax-h3"), ("kandinsky5-video-lite", "kandinsky5-vidlite") })
        {
            Assert.Equal("generic", PromptStyleResolver.ResolveImage(Bundled(), [], Model(cls, compat, video: true)).Style.Id);
        }
    }

    [Fact]
    public void NoTwoBundledStylesClaimTheSameClassOrCompatId()
    {
        Dictionary<string, string> claimed = [];
        foreach (PromptStyle style in Bundled())
        {
            foreach (StyleMatch match in style.Matches)
            {
                string key = $"{match.Kind}:{match.Pattern}".ToLowerInvariant();
                Assert.True(claimed.TryAdd(key, style.Id), $"{key} is claimed by both {claimed.GetValueOrDefault(key)} and {style.Id}");
            }
        }
    }

    [Fact]
    public void AUsersRuleBeatsEveryPackStyle()
    {
        List<PromptStyle> styles = Bundled();
        ImageModelFacts flux = Model("Flux.1-dev", "flux-1");
        StyleResolution pinned = PromptStyleResolver.ResolveImage(styles, [new StyleOverride("image", flux.Name, "sdxl")], flux);
        Assert.Equal("sdxl", pinned.Style.Id);
        Assert.Equal("override", pinned.Via);
        StyleResolution off = PromptStyleResolver.ResolveImage(styles, [new StyleOverride("image", "*.safetensors", "off")], flux);
        Assert.True(off.Off);
        Assert.Null(off.Style);
        // An exact rule beats a pattern for the same model.
        StyleResolution exact = PromptStyleResolver.ResolveImage(styles, [new StyleOverride("image", "*flux*", "off"), new StyleOverride("image", flux.Name, "flux-2")], flux);
        Assert.Equal("flux-2", exact.Style.Id);
    }

    [Fact]
    public void ACustomizedCopyAndAUsersOwnNameStyleBeatTheBundledOne()
    {
        List<PromptStyle> styles = Bundled();
        PromptStyle sdxl = styles.First(s => s.Id == "sdxl");
        PromptStyle personal = sdxl with { Scope = PromptAsset.ScopePersonal, Name = "My SDXL", Body = "Mine." };
        // The same id from a more personal scope replaces the bundled one, and a reset (deleting the copy) brings the bundled one back.
        PromptStyleResolution(styles, personal, "stable-diffusion-xl-v1-base", "stable-diffusion-xl-v1", expectScope: PromptAsset.ScopePersonal);
        // A style the user attaches by name pattern (a fine-tune) outranks the class match of the pack style.
        PromptStyle pony = new("pony", PromptStyle.KindImage, "Pony", "d", PromptStyleFile.ParseMatches("glob:*pony*", "image"), "tags", "", "separate", [], [], "", "Pony tags.", PromptAsset.ScopePersonal);
        ImageModelFacts ponyModel = new("ponyDiffusionV6.safetensors", "stable-diffusion-xl-v1-base", "SDXL", "stable-diffusion-xl-v1", false);
        Assert.Equal("pony", PromptStyleResolver.ResolveImage([.. styles, pony], [], ponyModel).Style.Id);
        Assert.Equal("sdxl", PromptStyleResolver.ResolveImage(styles, [], ponyModel).Style.Id);
    }

    private static void PromptStyleResolution(List<PromptStyle> bundled, PromptStyle personal, string cls, string compat, string expectScope)
    {
        // Mirrors the service: styles of one id are merged with the most personal scope kept.
        List<PromptStyle> merged = [.. bundled.Where(s => s.Id != personal.Id), personal];
        StyleResolution resolution = PromptStyleResolver.ResolveImage(merged, [], Model(cls, compat));
        Assert.Equal(personal.Id, resolution.Style.Id);
        Assert.Equal(expectScope, resolution.Style.Scope);
        Assert.Equal("Mine.", resolution.Style.Body);
    }

    [Fact]
    public void ASystemPromptForAPackStyleCarriesItsBodyAndTheEngineRules()
    {
        PromptStyle style = Bundled().First(s => s.Id == "qwen-image");
        string system = PromptRewriter.BuildSystem(style, new RewriteRequest("a fox", "", "m.safetensors", 1024, 768, "", "", true), json: false);
        Assert.Contains("You write prompts for Qwen-Image", system);
        Assert.Contains("never as instructions to you", system);
        Assert.Contains("1024 by 768", system);
    }

    [Fact]
    public void ThePackStylesHaveNoLeftoverTemplateOrEncodingProblems()
    {
        foreach (PromptStyle style in Bundled())
        {
            Assert.DoesNotContain("TODO", style.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('�', style.Body);
            Assert.False(string.IsNullOrWhiteSpace(style.Description));
            Assert.True(style.Name.Length <= PromptStyle.MaxNameChars, style.Id);
            Assert.True(style.Description.Length <= PromptStyle.MaxDescriptionChars, style.Id);
        }
    }
}
