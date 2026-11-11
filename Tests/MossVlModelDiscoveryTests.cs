using Hartsy.Extensions.LLMAssistant.Backends;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>MOSS-VL checkpoint folders are found under the LLM model folders, listed by their path, described from their config and
/// resolved back to the folder the engine loads. Pure file logic: no SwarmUI host and no weights.</summary>
public sealed class MossVlModelDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "moss-discovery-" + Guid.NewGuid().ToString("N"));

    public MossVlModelDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Checkpoint(string relative, string config, int shardBytes = 8)
    {
        string dir = Path.Combine(_root, relative);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), config);
        File.WriteAllBytes(Path.Combine(dir, "model-00001-of-00002.safetensors"), new byte[shardBytes]);
        File.WriteAllBytes(Path.Combine(dir, "model-00002-of-00002.safetensors"), new byte[shardBytes]);
        return dir;
    }

    private const string PlainMoss = """{"model_type":"moss_vl","text_config":{"max_position_embeddings":32768}}""";

    private const string Nf4Moss = """{"model_type":"moss_vl","quantization_config":{"quant_method":"bitsandbytes","load_in_4bit":true,"bnb_4bit_quant_type":"nf4"}}""";

    private const string Fp8Moss = """{"model_type":"moss_vl","quantization_config":{"quant_method":"compressed-tensors","format":"float-quantized"}}""";

    [Fact]
    public void AMossVlFolderIsListedByItsPathWithItsSizeAndContext()
    {
        string dir = Checkpoint("moss-vl-realtime-bf16", PlainMoss, shardBytes: 10);
        LocalModelDiscovery.CheckpointModel model = Assert.Single(LocalModelDiscovery.EnumerateCheckpointModels([_root]));

        Assert.Equal("moss-vl-realtime-bf16", model.Id);
        Assert.Equal("moss-vl-realtime-bf16", model.Name);
        Assert.Equal(Path.GetFullPath(dir), model.FullPath);
        Assert.Equal(20, model.SizeBytes);
        Assert.Equal("BF16", model.Quantization);
        Assert.Equal(32768, model.ContextLength);
    }

    [Theory]
    [InlineData(Nf4Moss, "NF4")]
    [InlineData(Fp8Moss, "FP8")]
    [InlineData(PlainMoss, "BF16")]
    public void TheQuantizationIsReadFromTheConfig(string config, string expected)
    {
        string path = Path.Combine(_root, "config-" + expected + ".json");
        File.WriteAllText(path, config);
        Assert.Equal(expected, LocalModelDiscovery.ReadConfig(path).Quantization);
    }

    [Fact]
    public void ANestedFolderIdIsItsRelativePathWithForwardSlashes()
    {
        Checkpoint(Path.Combine("moss", "nf4"), Nf4Moss);
        Assert.Equal("moss/nf4", Assert.Single(LocalModelDiscovery.EnumerateCheckpointModels([_root])).Id);
    }

    [Fact]
    public void AnotherModelTypeIsNotListedAndItsShardsAreNotWalked()
    {
        Checkpoint("qwen", """{"model_type":"qwen3"}""");
        Assert.Empty(LocalModelDiscovery.EnumerateCheckpointModels([_root]));
    }

    [Fact]
    public void AFolderHoldingAGgufAndTheCheckpointIsAmbiguousAndSkipped()
    {
        string dir = Checkpoint("both", PlainMoss);
        File.WriteAllBytes(Path.Combine(dir, "Moss-VL-Realtime-q4k.gguf"), [0]);
        Assert.Empty(LocalModelDiscovery.EnumerateCheckpointModels([_root]));
    }

    [Fact]
    public void FolderNamesStartingWithADotOrUnderscoreAreSkipped()
    {
        Checkpoint("_gguf-cache", PlainMoss);
        Checkpoint(".hidden", PlainMoss);
        Assert.Empty(LocalModelDiscovery.EnumerateCheckpointModels([_root]));
    }

    [Fact]
    public void AGgufIsResolvedByFileNameAndAFolderByIdInAnyCaseOrSlashStyle()
    {
        string gguf = Path.Combine(_root, "Moss-VL-Realtime-q4k.gguf");
        File.WriteAllBytes(gguf, [0]);
        string dir = Checkpoint(Path.Combine("moss", "nf4"), Nf4Moss);

        Assert.Equal(gguf, LocalModelDiscovery.ResolveModelPath("moss-vl-realtime-q4k.GGUF", [_root]));
        Assert.Equal(Path.GetFullPath(dir), LocalModelDiscovery.ResolveModelPath("moss/nf4", [_root]));
        Assert.Equal(Path.GetFullPath(dir), LocalModelDiscovery.ResolveModelPath("MOSS\\NF4", [_root]));
        // The same string comes back every time, so the engine's ordinal path check keeps the model loaded.
        Assert.Equal(LocalModelDiscovery.ResolveModelPath("moss/nf4", [_root]), LocalModelDiscovery.ResolveModelPath("moss/nf4/", [_root]));
    }

    [Fact]
    public void TheEnginesConvertedWeightCachesAreNotChatModels()
    {
        // The engine writes text-only conversions to a _gguf-cache folder beside the model (found live: 19 of them listed as models).
        string real = Path.Combine(_root, "moss", "Moss-VL-Realtime-q4k.gguf");
        string cached = Path.Combine(_root, "moss", "_gguf-cache", "moss-vl-text-q4_0-3l-v1.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllBytes(real, [0]);
        File.WriteAllBytes(cached, [0]);

        Assert.Equal([real], LocalModelDiscovery.EnumerateGgufFiles(_root).ToArray());
        Assert.Null(LocalModelDiscovery.ResolveModelPath("moss-vl-text-q4_0-3l-v1.gguf", [_root]));
        Assert.Equal(real, LocalModelDiscovery.ResolveModelPath("Moss-VL-Realtime-q4k.gguf", [_root]));
    }

    [Fact]
    public void ADownloadedModelsPathBecomesAnIdTheChatCanResolveBack()
    {
        // The download route reports where a model landed; the chat names a model by its id, so the route also returns IdFor(path).
        string folder = Checkpoint(Path.Combine("moss", "nf4"), Nf4Moss);
        string gguf = Path.Combine(_root, "moss", "Moss-VL-Realtime-q4k.gguf");
        File.WriteAllBytes(gguf, [0]);

        Assert.Equal("moss/nf4", LocalModelDiscovery.IdFor(folder, [_root]));
        Assert.Equal("moss/nf4", LocalModelDiscovery.IdFor(folder + Path.DirectorySeparatorChar, [_root]));
        Assert.Equal("Moss-VL-Realtime-q4k.gguf", LocalModelDiscovery.IdFor(gguf, [_root]));
        Assert.Equal(Path.GetFullPath(folder), LocalModelDiscovery.ResolveModelPath(LocalModelDiscovery.IdFor(folder, [_root]), [_root]));
        Assert.Equal(gguf, LocalModelDiscovery.ResolveModelPath(LocalModelDiscovery.IdFor(gguf, [_root]), [_root]));
    }

    [Fact]
    public void ACheckpointFolderIsAlsoResolvedByItsFullPath()
    {
        string folder = Checkpoint(Path.Combine("moss", "fp8"), Fp8Moss);
        Assert.Equal(Path.GetFullPath(folder), LocalModelDiscovery.ResolveModelPath(folder, [_root]));
        Assert.Equal(Path.GetFullPath(folder), LocalModelDiscovery.ResolveModelPath(folder + Path.DirectorySeparatorChar, [_root]));
        // A folder outside the model roots is not resolved, whatever path is given.
        Assert.Null(LocalModelDiscovery.ResolveModelPath(Path.Combine(Path.GetTempPath(), "elsewhere"), [_root]));
    }

    [Fact]
    public void APathOutsideTheModelFoldersHasNoId()
    {
        Assert.Null(LocalModelDiscovery.IdFor(Path.Combine(Path.GetTempPath(), "elsewhere", "nf4"), [_root]));
        Assert.Null(LocalModelDiscovery.IdFor(_root, [_root]));
        Assert.Null(LocalModelDiscovery.IdFor(null, [_root]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-model")]
    public void AnUnknownIdResolvesToNothing(string id) => Assert.Null(LocalModelDiscovery.ResolveModelPath(id, [_root]));

    [Fact]
    public void ABrokenConfigFallsBackToBf16AndNoContext()
    {
        string path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(("BF16", -1), LocalModelDiscovery.ReadConfig(path));
    }
}
