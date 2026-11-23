using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.ImageTools;
using Hartsy.Extensions.LLMAssistant.Tools;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The image workflow tools: which installed model a typed name means (and the "did you mean" list), what the listings return (filters, bounds, what one user may see, nothing private by default),
/// and the settings (default model, the cap on images per call).</summary>
public class ImageToolsTests
{
    private static readonly string[] Installed =
    [
        "OfficialStableDiffusion/sd_xl_base_1.0.safetensors",
        "Flux/flux1-dev.safetensors",
        "Flux/flux1-schnell.safetensors",
        "anime/pony_diffusion_v6.safetensors",
        "realistic_vision_v5.safetensors",
    ];

    private static CatalogEntry Model(string name, string arch = "", string cls = "", string compat = "", int w = 0, int h = 0, string hint = "", string trigger = "", string[] tags = null, string desc = "", string title = "")
        => new(name, title, arch, cls, compat, w, h, desc, hint, trigger, tags ?? [], false, "");

    // ---- the resolver ----

    [Theory]
    [InlineData("Flux/flux1-dev.safetensors", "Flux/flux1-dev.safetensors")]
    [InlineData("flux/FLUX1-DEV.SAFETENSORS", "Flux/flux1-dev.safetensors")]
    [InlineData("Flux/flux1-dev", "Flux/flux1-dev.safetensors")]
    [InlineData("flux1-dev", "Flux/flux1-dev.safetensors")]
    [InlineData("sd_xl_base_1.0", "OfficialStableDiffusion/sd_xl_base_1.0.safetensors")]
    [InlineData("Flux\\flux1-schnell", "Flux/flux1-schnell.safetensors")]
    [InlineData("pony", "anime/pony_diffusion_v6.safetensors")]
    [InlineData("Realistic Vision v5", "realistic_vision_v5.safetensors")]
    public void ANameThatMeansOneInstalledModelIsResolvedToItsExactName(string typed, string expected)
    {
        ModelResolution resolution = ModelNameResolver.Resolve(typed, Installed);
        Assert.True(resolution.Found);
        Assert.Equal(expected, resolution.Name);
    }

    [Fact]
    public void ANameThatFitsSeveralModelsPicksNoneAndListsThem()
    {
        ModelResolution resolution = ModelNameResolver.Resolve("flux", Installed);
        Assert.False(resolution.Found);
        Assert.True(resolution.Ambiguous);
        Assert.Equal(["Flux/flux1-dev.safetensors", "Flux/flux1-schnell.safetensors"], resolution.Suggestions);
    }

    [Fact]
    public void TheSameFileNameInTwoFoldersIsAmbiguousNotGuessed()
    {
        ModelResolution resolution = ModelNameResolver.Resolve("model", ["a/model.safetensors", "b/model.safetensors"]);
        Assert.False(resolution.Found);
        Assert.True(resolution.Ambiguous);
        Assert.Equal(2, resolution.Suggestions.Count);
    }

    [Fact]
    public void ASpellingMistakeIsNeverAcceptedButGetsDidYouMeanSuggestions()
    {
        ModelResolution resolution = ModelNameResolver.Resolve("flux1-dve", Installed);
        Assert.False(resolution.Found);
        Assert.False(resolution.Ambiguous);
        Assert.Equal("Flux/flux1-dev.safetensors", resolution.Suggestions[0]);
    }

    [Fact]
    public void SuggestionsAreBoundedAndNothingFarOffIsSuggested()
    {
        List<string> many = [.. Enumerable.Range(0, 30).Select(i => $"model-v{i:00}.safetensors")];
        Assert.True(ModelNameResolver.Resolve("model-vxx", many).Suggestions.Count <= ModelNameResolver.MaxSuggestions);
        Assert.Empty(ModelNameResolver.Resolve("zzzzqqqq", Installed).Suggestions);
    }

    [Fact]
    public void NothingInstalledOrNothingTypedResolvesToNothing()
    {
        Assert.False(ModelNameResolver.Resolve("flux", []).Found);
        Assert.False(ModelNameResolver.Resolve("  ", Installed).Found);
        Assert.False(ModelNameResolver.Resolve(null, Installed).Found);
    }

    [Fact]
    public void TheDidYouMeanErrorNamesTheCandidatesTheExactNameAndWhereToLook()
    {
        ModelResolution resolution = ModelNameResolver.Resolve("flux1-dve", Installed);
        string error = ImageCatalogService.NotFound("flux1-dve", resolution, Installed);
        Assert.Contains("Did you mean: Flux/flux1-dev.safetensors", error);
        Assert.Contains("Use the exact name", error);
        string none = ImageCatalogService.NotFound("zzzz", ModelNameResolver.Resolve("zzzz", Installed), Installed);
        Assert.DoesNotContain("Did you mean", none);
        Assert.Contains("Installed image models", none);
        Assert.Contains("No image models are installed", ImageCatalogService.NotFound("x", ModelNameResolver.Resolve("x", []), []));
    }

    [Fact]
    public void TheModelHintListsTheFirstFewNamesAndCountsTheRest()
    {
        Assert.Contains("No image models are installed", ImageCatalog.ModelHint([]));
        string few = ImageCatalog.ModelHint(Installed);
        Assert.StartsWith("Installed image models:", few);
        Assert.Contains("realistic_vision_v5.safetensors", few);
        string many = ImageCatalog.ModelHint([.. Enumerable.Range(0, 20).Select(i => $"m{i:00}")]);
        Assert.Contains("m07", many);
        Assert.DoesNotContain("m08", many);
        Assert.Contains("and 12 more", many);
        Assert.Contains("list_image_models", many);
    }

    // ---- model and LoRA listings ----

    private static readonly CatalogEntry[] Models =
    [
        Model("Flux/flux1-dev.safetensors", "Flux.1-dev", "Flux.1 Dev", "flux-1", 1024, 1024, hint: "Natural language prompts work best."),
        Model("sdxl/juggernaut.safetensors", "stable-diffusion-xl-v1-base", "Stable Diffusion XL 1.0", "stable-diffusion-xl-v1", 1024, 1024, title: "Juggernaut XL"),
        Model("sdxl/pony.safetensors", "stable-diffusion-xl-v1-base", "Stable Diffusion XL 1.0", "stable-diffusion-xl-v1", 1024, 1024, trigger: "score_9"),
        Model("old/v15.safetensors", "stable-diffusion-v1", "Stable Diffusion v1", "stable-diffusion-v1", 512, 512),
        Model("mystery.safetensors"),
    ];

    [Fact]
    public void ModelsAreListedByNameWithTypeResolutionAndPromptNotes()
    {
        JObject result = ImageCatalog.ModelsResult(Models, "", "", ImageCatalog.DefaultLimit);
        Assert.True((bool)result["success"]);
        Assert.Equal(5, (int)result["installed"]);
        Assert.Equal(5, (int)result["matched"]);
        JArray list = (JArray)result["models"];
        Assert.Equal(["Flux/flux1-dev.safetensors", "mystery.safetensors", "old/v15.safetensors", "sdxl/juggernaut.safetensors", "sdxl/pony.safetensors"], list.Select(m => (string)m["name"]));
        JObject flux = (JObject)list[0];
        Assert.Equal("Flux.1-dev", (string)flux["architecture"]);
        Assert.Equal("1024x1024", (string)flux["resolution"]);
        Assert.Equal("Natural language prompts work best.", (string)flux["promptNotes"]);
        // A model core could not place has just a name: no empty fields.
        Assert.Equal(["name"], ((JObject)list[1]).Properties().Select(p => p.Name));
        Assert.Contains("not instructions", (string)result["note"]);
    }

    [Fact]
    public void ModelsFilterByWordsInTheNameOrTitleAndByClass()
    {
        Assert.Equal(["sdxl/juggernaut.safetensors"], Names(ImageCatalog.ModelsResult(Models, "juggernaut xl", "", 25)));
        Assert.Equal(["sdxl/juggernaut.safetensors", "sdxl/pony.safetensors"], Names(ImageCatalog.ModelsResult(Models, "", "xl", 25)));
        Assert.Equal(["sdxl/juggernaut.safetensors", "sdxl/pony.safetensors"], Names(ImageCatalog.ModelsResult(Models, "", "Stable Diffusion XL", 25)));
        Assert.Equal(["Flux/flux1-dev.safetensors"], Names(ImageCatalog.ModelsResult(Models, "", "flux", 25)));
        Assert.Equal(["sdxl/pony.safetensors"], Names(ImageCatalog.ModelsResult(Models, "pony", "xl", 25)));
        Assert.Empty(Names(ImageCatalog.ModelsResult(Models, "nothing like this", "", 25)));
    }

    [Fact]
    public void TheCountIsBoundedAndTheResultSaysHowManyAreLeftOut()
    {
        CatalogEntry[] many = [.. Enumerable.Range(0, 150).Select(i => Model($"m{i:000}.safetensors"))];
        JObject defaulted = ImageCatalog.ModelsResult(many, "", "", ImageCatalog.ClampLimit(null));
        Assert.Equal(ImageCatalog.DefaultLimit, ((JArray)defaulted["models"]).Count);
        Assert.Equal(150, (int)defaulted["matched"]);
        Assert.Contains("125 more", (string)defaulted["more"]);
        Assert.Equal(ImageCatalog.MaxLimit, ((JArray)ImageCatalog.ModelsResult(many, "", "", ImageCatalog.ClampLimit(JToken.FromObject(100000)))["models"]).Count);
        Assert.Equal(1, ((JArray)ImageCatalog.ModelsResult(many, "", "", ImageCatalog.ClampLimit(JToken.FromObject(-5)))["models"]).Count);
        Assert.Equal(ImageCatalog.DefaultLimit, ImageCatalog.ClampLimit(JToken.FromObject("junk")));
        Assert.Equal(7, ImageCatalog.ClampLimit(JToken.FromObject("7")));
    }

    [Fact]
    public void AHugeDescriptionIsCutAndTheWholeResultStaysUnderItsCharacterBudget()
    {
        string big = new('x', 5000);
        CatalogEntry[] heavy = [.. Enumerable.Range(0, 100).Select(i => Model($"m{i:000}.safetensors", desc: big, hint: big, trigger: big, tags: [.. Enumerable.Range(0, 40).Select(t => $"tag{t}")]))];
        JObject result = ImageCatalog.ModelsResult(heavy, "", "", 100);
        JObject first = (JObject)((JArray)result["models"])[0];
        Assert.True(((string)first["description"]).Length <= ImageCatalog.MaxNoteChars + 3);
        Assert.Equal(ImageCatalog.MaxTags, ((JArray)first["tags"]).Count);
        Assert.True(result.ToString(Newtonsoft.Json.Formatting.None).Length < ImageCatalog.MaxResultChars + 3000);
        Assert.True((int)result["shown"] < 100);
        Assert.Contains("more match", (string)result["more"]);
    }

    [Fact]
    public void AUserSeesOnlyTheModelsTheyAreAllowed()
    {
        Func<string, bool> alice = name => !name.StartsWith("sdxl/");
        Func<string, bool> bob = name => name.StartsWith("sdxl/");
        JObject forAlice = ImageCatalog.ModelsResult(Models, "", "", 25, alice);
        JObject forBob = ImageCatalog.ModelsResult(Models, "", "", 25, bob);
        Assert.DoesNotContain(Names(forAlice), n => n.StartsWith("sdxl/"));
        Assert.All(Names(forBob), n => Assert.StartsWith("sdxl/", n));
        // What is hidden is not counted or hinted at either.
        Assert.Equal(3, (int)forAlice["installed"]);
        Assert.Equal(2, (int)forBob["installed"]);
        // Even a filter that names a hidden model finds nothing.
        Assert.Empty(Names(ImageCatalog.ModelsResult(Models, "pony", "", 25, alice)));
    }

    [Fact]
    public void LorasListTheirBaseFamilyTriggerWordsAndTags()
    {
        CatalogEntry[] loras =
        [
            Model("styles/watercolor.safetensors", "stable-diffusion-xl-v1/lora", "SDXL LoRA", "stable-diffusion-xl-v1", trigger: "watercolor style", tags: ["style", "paint"]) with { DefaultWeight = "0.8" },
            Model("chars/hero_flux.safetensors", "flux.1/lora", "Flux LoRA", "flux-1", trigger: "hero_person", tags: ["character"]),
            Model("old/sd15_detail.safetensors", "stable-diffusion-v1/lora", "SD1 LoRA", "stable-diffusion-v1"),
        ];
        JObject all = ImageCatalog.LorasResult(loras, "", "", 25);
        Assert.Equal(3, ((JArray)all["loras"]).Count);
        JObject water = (JObject)((JArray)all["loras"]).First(l => (string)l["name"] == "styles/watercolor.safetensors");
        Assert.Equal("stable-diffusion-xl-v1", (string)water["baseModel"]);
        Assert.Equal("watercolor style", (string)water["triggerWords"]);
        Assert.Equal("0.8", (string)water["defaultWeight"]);
        Assert.Equal(["style", "paint"], ((JArray)water["tags"]).Select(t => (string)t));
        Assert.Equal(["styles/watercolor.safetensors"], Names(ImageCatalog.LorasResult(loras, "", "xl", 25), "loras").Select(n => n));
        Assert.Equal(["chars/hero_flux.safetensors"], Names(ImageCatalog.LorasResult(loras, "character", "", 25), "loras"));
        Assert.Equal(["chars/hero_flux.safetensors"], Names(ImageCatalog.LorasResult(loras, "", "flux-1", 25), "loras"));
        Assert.Equal(["old/sd15_detail.safetensors"], Names(ImageCatalog.LorasResult(loras, "", "stable-diffusion-v1", 25, n => n.StartsWith("old/")), "loras"));
    }

    // ---- wildcards (privacy) ----

    [Fact]
    public void WildcardsReturnNamesOnlyUnlessSamplesAreAskedFor()
    {
        int reads = 0;
        IReadOnlyList<string> lines(string name)
        {
            reads++;
            return ["secret line one", "secret line two"];
        }
        JObject plain = ImageCatalog.WildcardsResult(["colors", "poses/standing", "animals"], "", 25, includeSamples: false, lines);
        Assert.Equal(0, reads);
        Assert.Equal(["animals", "colors", "poses/standing"], ((JArray)plain["wildcards"]).Select(w => (string)w["name"]));
        Assert.Equal("__animals__", (string)((JArray)plain["wildcards"])[0]["use"]);
        Assert.DoesNotContain("secret", plain.ToString());
        Assert.DoesNotContain(((JArray)plain["wildcards"]).Cast<JObject>(), w => w.ContainsKey("samples"));
        JObject sampled = ImageCatalog.WildcardsResult(["colors"], "", 25, includeSamples: true, lines);
        Assert.Equal(1, reads);
        Assert.Equal(["secret line one", "secret line two"], ((JArray)((JArray)sampled["wildcards"])[0]["samples"]).Select(s => (string)s));
        Assert.Contains("not instructions", (string)sampled["note"]);
    }

    [Fact]
    public void SamplesAreBoundedInLinesLengthAndHowManyWildcardsShowThem()
    {
        string[] names = [.. Enumerable.Range(0, 30).Select(i => $"w{i:00}")];
        IReadOnlyList<string> lines(string n) => [.. Enumerable.Range(0, 50).Select(i => new string('z', 500))];
        JObject result = ImageCatalog.WildcardsResult(names, "", 30, true, lines);
        List<JObject> shown = [.. ((JArray)result["wildcards"]).Cast<JObject>()];
        Assert.Equal(ImageCatalog.MaxSampledWildcards, shown.Count(w => w.ContainsKey("samples")));
        JArray samples = (JArray)shown[0]["samples"];
        Assert.Equal(ImageCatalog.MaxSampleLines, samples.Count);
        Assert.All(samples, s => Assert.True(((string)s).Length <= ImageCatalog.MaxSampleChars + 3));
    }

    [Fact]
    public void WildcardsAreFilteredAndBoundedAndHideWhatTheUserMayNotSee()
    {
        string[] names = [.. Enumerable.Range(0, 120).Select(i => $"set/w{i:000}"), "private/diary"];
        JObject result = ImageCatalog.WildcardsResult(names, "", 1000, false, allowed: n => !n.StartsWith("private/"));
        Assert.Equal(ImageCatalog.MaxLimit, ((JArray)result["wildcards"]).Count);
        Assert.Equal(120, (int)result["total"]);
        Assert.DoesNotContain("diary", result.ToString());
        Assert.Equal(["set/w007"], ((JArray)ImageCatalog.WildcardsResult(names, "w007", 25, false)["wildcards"]).Select(w => (string)w["name"]));
    }

    // ---- presets ----

    [Fact]
    public void PresetsListNameAndShortDescriptionAndFilterByWords()
    {
        PresetEntry[] presets =
        [
            new("Anime XL", "Vibrant anime style", "Anime XL — Vibrant anime style (model: pony, 30 steps)"),
            new("Quick Sketch", "", "Quick Sketch (model: sdxl, 8 steps)"),
            new("Photo", new string('d', 900), ""),
        ];
        JObject all = ImageCatalog.PresetsResult(presets, "", 25);
        Assert.Equal(["Anime XL", "Photo", "Quick Sketch"], ((JArray)all["presets"]).Select(p => (string)p["name"]));
        JObject anime = (JObject)((JArray)all["presets"])[0];
        Assert.Equal("Vibrant anime style", (string)anime["description"]);
        Assert.Contains("pony", (string)anime["summary"]);
        Assert.True(((string)((JArray)all["presets"])[1]["description"]).Length <= ImageCatalog.MaxNoteChars + 3);
        Assert.False(((JObject)((JArray)all["presets"])[2]).ContainsKey("description"));
        Assert.Equal(["Anime XL"], ((JArray)ImageCatalog.PresetsResult(presets, "vibrant", 25)["presets"]).Select(p => (string)p["name"]));
        Assert.Equal(1, ((JArray)ImageCatalog.PresetsResult(presets, "", 1)["presets"]).Count);
        Assert.Equal(3, (int)ImageCatalog.PresetsResult(presets, "", 1)["total"]);
    }

    // ---- the settings ----

    [Fact]
    public void WithNothingSavedThereIsNoDefaultModelAndTheLimitIsFour()
    {
        ImageToolSettings settings = ImageToolSettings.From(null, null);
        Assert.Equal("", settings.DefaultModel);
        Assert.Equal(4, settings.MaxImages);
        Assert.Equal(ImageToolSettings.Default, settings);
    }

    [Fact]
    public void TheUsersOwnDefaultModelWinsAndAnEmptyOneInheritsTheSharedOne()
    {
        JObject shared = new() { ["defaultModel"] = "shared/model.safetensors" };
        Assert.Equal("shared/model.safetensors", ImageToolSettings.From(shared, null).DefaultModel);
        Assert.Equal("mine.safetensors", ImageToolSettings.From(shared, new JObject { ["defaultModel"] = " mine.safetensors " }).DefaultModel);
        Assert.Equal("shared/model.safetensors", ImageToolSettings.From(shared, new JObject { ["defaultModel"] = "" }).DefaultModel);
        Assert.Equal("shared/model.safetensors", ImageToolSettings.From(shared, new JObject { ["defaultModel"] = 5 }).DefaultModel);
    }

    [Fact]
    public void OnlyTheSharedLayerSetsTheLimitAndItIsHeldToItsRange()
    {
        Assert.Equal(8, ImageToolSettings.From(new JObject { ["maxImages"] = 8 }, null).MaxImages);
        // A user cannot raise (or lower) the server's limit for themselves.
        Assert.Equal(4, ImageToolSettings.From(null, new JObject { ["maxImages"] = 12 }).MaxImages);
        Assert.Equal(2, ImageToolSettings.From(new JObject { ["maxImages"] = 2 }, new JObject { ["maxImages"] = 12 }).MaxImages);
        Assert.Equal(ImageToolSettings.HardMaxImages, ImageToolSettings.From(new JObject { ["maxImages"] = 9999 }, null).MaxImages);
        Assert.Equal(1, ImageToolSettings.From(new JObject { ["maxImages"] = 0 }, null).MaxImages);
        Assert.Equal(4, ImageToolSettings.From(new JObject { ["maxImages"] = "lots" }, null).MaxImages);
        Assert.Equal(4, ImageToolSettings.From(new JObject { ["maxImages"] = 2.5 }, null).MaxImages);
    }

    [Theory]
    [InlineData(1, 4, 1, false)]
    [InlineData(4, 4, 4, false)]
    [InlineData(5, 4, 4, true)]
    [InlineData(100, 4, 4, true)]
    [InlineData(0, 4, 1, false)]
    [InlineData(-3, 4, 1, false)]
    [InlineData(3, 2, 2, true)]
    public void AnImageCountIsHeldBetweenOneAndTheLimit(int asked, int cap, int expected, bool capped)
    {
        ImageToolSettings settings = new("", cap);
        Assert.Equal(expected, settings.ClampImages(asked, out bool wasCapped));
        Assert.Equal(capped, wasCapped);
    }

    // ---- the tools ----

    [Fact]
    public void TheCatalogToolsOnlyReadSoTheyRunWithoutAskingAndAreOnByDefault()
    {
        Assert.Equal(ToolRisk.Read, new ListImageModelsTool().Risk);
        Assert.Equal(ToolRisk.Read, new ListLorasTool().Risk);
        Assert.Equal(ToolRisk.Read, new ListWildcardsTool().Risk);
        Assert.Equal(ToolRisk.Read, new ListPresetsTool().Risk);
        Assert.Equal(ApprovalDecision.Allow, ApprovalPolicy.Decide("ask", new ListImageModelsTool().Risk, false));
        Assert.Equal(ApprovalDecision.Allow, ApprovalPolicy.Decide("plan", new ListPresetsTool().Risk, false));
        Assert.Equal(["list_image_models", "list_loras", "list_wildcards", "list_presets"], ToolConstants.ImageCatalogIds);
        Assert.All(ToolConstants.ImageCatalogIds, id => Assert.Contains(id, ToolConstants.BuiltInIds));
        JObject definitions = ImageToolDefinitions.Build("now");
        Assert.Equal(ToolConstants.ImageCatalogIds.OrderBy(i => i), definitions.Properties().Select(p => p.Name).OrderBy(i => i));
        foreach (JProperty property in definitions.Properties())
        {
            JObject tool = (JObject)property.Value;
            Assert.True((bool)tool["enabled"]);
            Assert.Equal(property.Name, (string)tool["handlerId"]);
            Assert.Null(tool["parameters"]["required"]);
        }
    }

    [Fact]
    public void ThePrivacyDefaultAndTheNeverGuessAdviceAreWrittenInTheDescriptions()
    {
        JObject definitions = ImageToolDefinitions.Build("now");
        Assert.Contains("BEFORE generate_image", (string)definitions["list_image_models"]["description"]);
        Assert.Contains("Returns names only", (string)definitions["list_wildcards"]["description"]);
        Assert.Contains("Off by default", (string)definitions["list_wildcards"]["parameters"]["properties"]["includeSamples"]["description"]);
        Assert.Contains("Never guess a model name", ImageToolDefinitions.ModelGuidance);
    }

    [Fact]
    public void GenerateImageDescriptionAndSchemaAskForAModelAndAnImageCount()
    {
        JObject tool = (JObject)ToolRegistryService.BuildDefaultTools()["generate_image"];
        Assert.Contains("list_image_models", (string)tool["description"]);
        JObject properties = (JObject)tool["parameters"]["properties"];
        Assert.Equal("integer", (string)properties["images"]["type"]);
        Assert.Equal("string", (string)properties["model"]["type"]);
        Assert.Equal(["prompt"], ((JArray)tool["parameters"]["required"]).Select(r => (string)r));
        // A batch takes a while: the call gets longer than the default minute.
        Assert.True(new GenerateImageTool().TimeoutFor(tool) >= TimeSpan.FromMinutes(5));
    }

    private static List<string> Names(JObject result, string key = "models") => [.. ((JArray)result[key]).Select(m => (string)m["name"])];
}
