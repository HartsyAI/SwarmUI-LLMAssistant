using Hartsy.Extensions.LLMAssistant.Services.ImageTools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The second review of the image tools: a preset named in <c>params</c> cannot override the cap or the model, model names are kept exactly, non-image families are left out, a default model is
/// only given to a user who may use it, and a stopped call returns at once when it was stopped before it began.</summary>
public class ImageToolsSecondReviewTests
{
    private static string FakeId(string key) => new([.. (key ?? "").ToLowerInvariant().Where(c => c is >= 'a' and <= 'z')]);

    // ---- 1. presets named in params ----

    [Theory]
    [InlineData("presets")]
    [InlineData("Presets")]
    [InlineData("PRESETS")]
    [InlineData("session_id")]
    [InlineData("extra_metadata")]
    public void ReservedKeysAreNeverTakenFromParamsOrAPreset(string key)
    {
        Assert.True(ImageParamKeys.IsReserved(key));
        JObject raw = new() { ["prompt"] = "x" };
        ImageParamKeys.MergeLayer(raw, new JObject { [key] = new JArray("Big Preset"), ["steps"] = 5 }.Properties().Select(p => new KeyValuePair<string, JToken>(p.Name, p.Value)), FakeId);
        Assert.Equal(["prompt", "steps"], raw.Properties().Select(p => p.Name));
    }

    [Fact]
    public void ThePresetsKeyIsNotAParameterSoNormalKeysAreStillMerged()
    {
        Assert.False(ImageParamKeys.IsReserved("steps"));
        Assert.False(ImageParamKeys.IsReserved("model"));
        Assert.False(ImageParamKeys.IsReserved(null));
        JObject raw = new() { ["Model"] = "a", ["steps"] = 1 };
        ImageParamKeys.MergeLayer(raw, new Dictionary<string, JToken> { ["model"] = "b", ["presets"] = "p" }, FakeId);
        Assert.Equal("b", (string)ImageParamKeys.Get(raw, "model", FakeId));
        Assert.Equal(["steps", "model"], raw.Properties().Select(p => p.Name));
    }

    [Fact]
    public void ACutBatchSizeIsReportedWithWhatWasAsked()
    {
        JObject raw = new() { ["batchsize"] = 100 };
        (int batch, int asked) = ImageParamKeys.EnforceImageCapDetailed(raw, 2, 4, FakeId);
        Assert.Equal((2, 100), (batch, asked));
        (batch, asked) = ImageParamKeys.EnforceImageCapDetailed(new JObject(), 2, 4, FakeId);
        Assert.Equal((1, 1), (batch, asked));
        (batch, asked) = ImageParamKeys.EnforceImageCapDetailed(new JObject { ["batchsize"] = 2 }, 2, 4, FakeId);
        Assert.Equal((2, 2), (batch, asked));
    }

    // ---- 2. a call stopped before it began ----

    [Fact]
    public async Task ACallThatIsAlreadyStoppedMakesNothingAndSaysSo()
    {
        using CancellationTokenSource stopped = new();
        stopped.Cancel();
        BatchOutcome outcome = await ImageBatch.RunAsync(null, 2, [], stopped.Token);
        Assert.True(outcome.Cancelled);
        Assert.Empty(outcome.Images);
        Assert.Null(outcome.Error);
    }

    // ---- 3. families ----

    [Theory]
    [InlineData("pid", "pid", false, true)]
    [InlineData("seedvr2", "seedvr2", false, true)]
    [InlineData("seedvr2", "seedvr2/vae", false, true)]
    [InlineData("sam3", "sam3", false, true)]
    [InlineData("wan-21", "wan-21-14b", true, true)]
    [InlineData("nvidia-cosmos-predict2-t2i-2b", "nvidia-cosmos-predict2-t2i-2b", true, false)]
    [InlineData("nvidia-cosmos-predict2-t2i-14b", "nvidia-cosmos-predict2-t2i-14b", true, false)]
    [InlineData("flux-1", "flux.1-dev", false, false)]
    [InlineData("stable-diffusion-xl-v1", "stable-diffusion-xl-v1-base", false, false)]
    [InlineData("", "", false, false)]
    public void OnlyImageGeneratorsAreInTheImageModelList(string compat, string architecture, bool flaggedVideo, bool leftOut)
    {
        Assert.Equal(leftOut, ImageCatalog.IsNonImageFamily(compat, architecture, flaggedVideo));
    }

    [Fact]
    public void AFamilyThatMerelyStartsLikeANonImageOneIsStillAnImageModel()
    {
        Assert.False(ImageCatalog.IsNonImageFamily("pidgin-v1", "pidgin-v1-base", false));
        Assert.False(ImageCatalog.IsNonImageFamily("sam30", "sam30", false));
    }

    // ---- 4. names are exact, descriptions are cleaned ----

    [Fact]
    public void ANameIsKeptExactlyAsItIsSoItCanBePassedBack()
    {
        Assert.Equal("dir/Model  v2.safetensors", CatalogEntry.FromNet(new JObject { ["name"] = "dir/Model  v2.safetensors" }).Name);
        Assert.Equal(" odd name .safetensors", CatalogEntry.FromNet(new JObject { ["name"] = " odd name .safetensors" }).Name);
        // The same exact name is what the resolver finds.
        Assert.Equal("a/Model  v2.safetensors", ModelNameResolver.Resolve("a/Model  v2.safetensors", ["a/Model  v2.safetensors", "a/Model v2.safetensors"]).Name);
    }

    [Fact]
    public void ANameWithAControlCharacterOrTooLongIsDropped()
    {
        Assert.Equal("", CatalogEntry.FromNet(new JObject { ["name"] = "a\nb.safetensors" }).Name);
        Assert.Equal("", CatalogEntry.FromNet(new JObject { ["name"] = "a\0b.safetensors" }).Name);
        Assert.Equal("", CatalogEntry.FromNet(new JObject { ["name"] = new string('n', CatalogEntry.MaxNameChars + 1) }).Name);
        Assert.Empty(ImageCatalog.Filter([CatalogEntry.FromNet(new JObject { ["name"] = "a\nb" })], "", ""));
    }

    [Fact]
    public void InvisibleCharactersAreStrippedFromDescriptiveText()
    {
        // U+E0049 U+E0067 ... are "tag" characters (invisible text); each is a surrogate pair.
        string tagged = "ok" + char.ConvertFromUtf32(0xE0049) + char.ConvertFromUtf32(0xE0067) + char.ConvertFromUtf32(0xE007F) + "fine";
        Assert.Equal("okfine", ImageCatalog.Clean(tagged, 50));
        Assert.Equal("ab", ImageCatalog.Clean("a​b‌‍⁠﻿", 50));
        Assert.Equal("a b", ImageCatalog.Clean("a ​ b", 50));
        // Real text with surrogate pairs (an emoji) is not damaged.
        Assert.Equal("ok \U0001F600", ImageCatalog.Clean("ok \U0001F600", 50));
        JObject json = ImageCatalog.ModelJson(CatalogEntry.FromNet(new JObject { ["name"] = "m", ["description"] = tagged, ["trigger_phrase"] = "x​y" }));
        Assert.Equal("okfine", (string)json["description"]);
        Assert.Equal("xy", (string)json["triggerPhrase"]);
    }

    // ---- 5. who is given which default model ----

    [Fact]
    public void ADefaultModelIsOnlyGivenToAUserWhoMayUseIt()
    {
        JObject shared = new() { ["defaultModel"] = "team/model.safetensors" };
        JObject personal = new() { ["defaultModel"] = "mine.safetensors" };
        Assert.Equal("mine.safetensors", ImageToolSettings.EffectiveModel(shared, personal, _ => true));
        // Their own is not allowed (an admin changed their roles): the shared one is next.
        Assert.Equal("team/model.safetensors", ImageToolSettings.EffectiveModel(shared, personal, n => n.StartsWith("team/")));
        // Neither is allowed: nothing, and so no name to leak into a tool description or a route's reply.
        Assert.Equal("", ImageToolSettings.EffectiveModel(shared, personal, _ => false));
        // Only the shared one set, and not allowed.
        Assert.Equal("", ImageToolSettings.EffectiveModel(shared, null, _ => false));
        Assert.Equal("", ImageToolSettings.EffectiveModel(null, null, _ => true));
        Assert.Equal("team/model.safetensors", ImageToolSettings.EffectiveModel(shared, new JObject { ["defaultModel"] = "" }, _ => true));
    }

    // ---- wildcards need core's model access ----

    [Fact]
    public async Task WildcardsAreRefusedToAnAnonymousCaller()
    {
        Assert.NotNull(ImageCatalogSource.Denied(null));
        JObject result = ImageCatalogService.ListWildcards(null, new JObject());
        Assert.False((bool)result["success"]);
        Assert.Contains("signed-in", (string)result["error"]);
        JObject models = await ImageCatalogService.ListModelsAsync(null, new JObject());
        Assert.False((bool)models["success"]);
        // Nothing of the wildcards list is in a refusal.
        Assert.False(result.ContainsKey("wildcards"));
    }
}
