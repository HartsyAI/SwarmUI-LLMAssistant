using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using HartsyInference.Engine.Requests;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>How the local backend maps MOSS-VL's settings, video attachments and capabilities onto the engine's request and the model
/// list. No SwarmUI host and no weights.</summary>
public sealed class MossVlRequestMappingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moss-request-" + Guid.NewGuid().ToString("N"));

    public MossVlRequestMappingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ExtendedLLMInput Input() => new() { Model = "moss-vl-realtime", Temperature = 0.2, TopP = 0.9, MaxTokens = 256, Seed = 1 };

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("native", null)]
    [InlineData("bf16", null)]
    [InlineData("q3", null)]
    [InlineData("q8_0", "q8_0")]
    [InlineData(" Q4_K ", "q4_k")]
    [InlineData("q6_k", "q6_k")]
    [InlineData("q5_k", "q5_k")]
    [InlineData("q4_0", "q4_0")]
    public void WeightPrecisionReachesTheEngineOnlyAsAKnownBlockType(string setting, string expected) =>
        Assert.Equal(expected, HartsyLocalLLMProvider.WeightPrecisionFor(setting));

    [Fact]
    public void TheVideoAndPrecisionSettingsReachTheTextRequest()
    {
        HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings settings = new() { WeightPrecision = "q8_0", VideoFps = 0.5, VideoMaxFrames = 64 };
        TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(Input(), [], null, "vulkan:0", settings);
        Assert.Equal("q8_0", request.WeightPrecision);
        Assert.Equal(0.5, request.VideoFps);
        Assert.Equal(64, request.VideoMaxFrames);
    }

    [Fact]
    public void TheDefaultsLeaveThePrecisionAlone()
    {
        TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(Input(), [], null, "vulkan:0", new());
        Assert.Null(request.WeightPrecision);
        Assert.Equal(1.0, request.VideoFps);
        Assert.Equal(256, request.VideoMaxFrames);
    }

    [Fact]
    public void ZeroOrNegativeVideoSettingsFallBackToTheModelsOwnSampling()
    {
        TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(Input(), [], null, "vulkan:0", new() { VideoFps = 0, VideoMaxFrames = -1 });
        Assert.Null(request.VideoFps);
        Assert.Null(request.VideoMaxFrames);
    }

    [Fact]
    public void AVideoClipRidesOnTheMessage()
    {
        VideoClip clip = new() { Data = [1, 2, 3], Format = "mp4" };
        TextMessage message = HartsyLocalLLMProvider.ToTextMessage(new LLMMessage { Role = LLMRoles.User, Content = "what happens?" },
            "what happens?", images: null, splitReasoning: false, video: clip);
        Assert.Same(clip, message.Video);
        Assert.Null(message.Images);
        Assert.Null(HartsyLocalLLMProvider.ToTextMessage(new LLMMessage { Role = LLMRoles.User }, "hi", null).Video);
    }

    [Fact]
    public async Task ALocalVideoFileIsReadWithItsContainerFormat()
    {
        string path = Path.Combine(_dir, "clip.webm");
        await File.WriteAllBytesAsync(path, [9, 8, 7]);
        VideoClip clip = await HartsyLocalLLMProvider.ReadVideoAsync(
            [new LLMMediaAttachment { Type = "file", Data = path, MediaType = "video/webm" }], CancellationToken.None);
        Assert.Equal([9, 8, 7], clip.Data);
        Assert.Equal("webm", clip.Format);
    }

    [Theory]
    [InlineData("video/mp4", "mp4")]
    [InlineData("video/quicktime", "mov")]
    [InlineData("video/x-matroska", null)]
    public async Task InlineBase64VideoIsDecodedAndTheFormatMapped(string mediaType, string format)
    {
        VideoClip clip = await HartsyLocalLLMProvider.ReadVideoAsync(
            [new LLMMediaAttachment { Type = "base64", Data = Convert.ToBase64String([1, 2]), MediaType = mediaType }], CancellationToken.None);
        Assert.Equal([1, 2], clip.Data);
        Assert.Equal(format, clip.Format);
    }

    [Fact]
    public async Task OnlyTheFirstVideoIsUsedAndImagesAndUrlsAreIgnored()
    {
        string first = Path.Combine(_dir, "a.mp4");
        string second = Path.Combine(_dir, "b.mp4");
        await File.WriteAllBytesAsync(first, [1]);
        await File.WriteAllBytesAsync(second, [2]);
        VideoClip clip = await HartsyLocalLLMProvider.ReadVideoAsync(
        [
            new LLMMediaAttachment { Type = "base64", Data = "aGk=", MediaType = "image/png" },
            new LLMMediaAttachment { Type = "file", Data = first, MediaType = "video/mp4" },
            new LLMMediaAttachment { Type = "file", Data = second, MediaType = "video/mp4" },
        ], CancellationToken.None);
        Assert.Equal([1], clip.Data);
    }

    [Fact]
    public async Task NoVideoOrARemoteUrlGivesNoClip()
    {
        Assert.Null(await HartsyLocalLLMProvider.ReadVideoAsync([new LLMMediaAttachment { Type = "base64", Data = "aGk=", MediaType = "image/png" }], CancellationToken.None));
        Assert.Null(await HartsyLocalLLMProvider.ReadVideoAsync(
            [new LLMMediaAttachment { Type = "url", Data = "https://example.com/clip.mp4", MediaType = "video/mp4" }], CancellationToken.None));
    }

    [Fact]
    public void CapabilitiesBecomeModelListMetadataAndTags()
    {
        Dictionary<string, string> moss = HartsyLocalLLMProvider.CapabilityMetadata(new TextInputCapabilities(true, true, true));
        Assert.Equal("true", moss["vision"]);
        Assert.Equal("true", moss["video"]);
        Assert.Equal("true", moss["realtime"]);
        Assert.Equal("vision,video,realtime", moss["tags"]);

        Dictionary<string, string> imagesOnly = HartsyLocalLLMProvider.CapabilityMetadata(new TextInputCapabilities(true, false, false));
        Assert.Equal(["tags", "vision"], imagesOnly.Keys.Order().ToArray());

        // A text-only model gets nothing, so the chat UI treats its vision capability as unknown rather than refusing images.
        Assert.Empty(HartsyLocalLLMProvider.CapabilityMetadata(TextInputCapabilities.None));
    }

    [Theory]
    [InlineData("moss-vl-realtime", "nf4", true)]
    [InlineData("moss-vl-realtime", "FP8", true)]
    [InlineData("moss-vl-realtime", "gguf-q4k", true)]
    [InlineData("moss-vl-realtime", "awq", false)]
    [InlineData("flux-dev", "fp8", false)]
    [InlineData("", "nf4", false)]
    [InlineData(null, "nf4", false)]
    public void OnlyModelsWithDownloadableVariantsMayBeFetchedFromTheChatUi(string model, string variant, bool allowed)
    {
        Assert.Equal(allowed, HartsyLocalLLMProvider.IsDownloadableModel(model, variant, out _, out string error));
        Assert.Equal(allowed, error is null);
    }
}
