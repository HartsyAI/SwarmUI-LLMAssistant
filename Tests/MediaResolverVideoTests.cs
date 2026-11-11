using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins how a video attachment reaches a provider: its MIME is guessed from the extension, a local video stays on disk as
/// a <c>"file"</c> attachment (never base64'd into memory), an oversized or missing one is skipped, and images still get inlined.</summary>
public class MediaResolverVideoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llma-resolver-tests-" + Guid.NewGuid().ToString("N"));

    public MediaResolverVideoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.MP4", "video/mp4")]
    [InlineData("clip.m4v", "video/mp4")]
    [InlineData("clip.webm", "video/webm")]
    [InlineData("clip.mov", "video/quicktime")]
    [InlineData("pic.png", "image/png")]
    [InlineData("pic.jpg", "image/jpeg")]
    [InlineData("unknown.bin", "image/png")]
    public void GuessMimeFromExt_MapsVideoExtensions(string file, string expected)
    {
        Assert.Equal(expected, ImageInputResolver.GuessMimeFromExt(file));
    }

    [Fact]
    public void LocalVideo_IsReturnedAsAFileReference_NotBase64()
    {
        string path = Write("a.mp4", new byte[2048]);
        List<LLMMediaAttachment> resolved = MediaResolver.ResolveForLLM(
            [new LLMMediaAttachment { Type = "url", Data = "View/u/llm_assistant/uploads/t/a.mp4", MediaType = "video/mp4" }],
            _ => path);
        LLMMediaAttachment att = Assert.Single(resolved);
        Assert.Equal("file", att.Type);
        Assert.Equal(path, att.Data);
        Assert.Equal("video/mp4", att.MediaType);
        Assert.True(att.IsVideo);
    }

    [Fact]
    public void VideoWithoutAMediaType_IsRecognizedByExtension()
    {
        string path = Write("b.mov", new byte[16]);
        List<LLMMediaAttachment> resolved = MediaResolver.ResolveForLLM(
            [new LLMMediaAttachment { Type = "url", Data = "View/u/b.mov" }], _ => path);
        LLMMediaAttachment att = Assert.Single(resolved);
        Assert.Equal("file", att.Type);
        Assert.Equal("video/quicktime", att.MediaType);
    }

    [Fact]
    public void LocalImage_IsStillBase64d()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        string path = Write("a.png", png);
        List<LLMMediaAttachment> resolved = MediaResolver.ResolveForLLM(
            [new LLMMediaAttachment { Type = "url", Data = "View/u/a.png", MediaType = "image/png" }], _ => path);
        LLMMediaAttachment att = Assert.Single(resolved);
        Assert.Equal("base64", att.Type);
        Assert.Equal(Convert.ToBase64String(png), att.Data);
        Assert.False(att.IsVideo);
    }

    [Fact]
    public void ImageAndVideoInOneMessage_KeepTheirOwnShapes()
    {
        string png = Write("a.png", [1, 2, 3]);
        string mp4 = Write("a.mp4", new byte[32]);
        List<LLMMediaAttachment> resolved = MediaResolver.ResolveForLLM(
        [
            new LLMMediaAttachment { Type = "url", Data = "View/u/a.png", MediaType = "image/png" },
            new LLMMediaAttachment { Type = "url", Data = "View/u/a.mp4", MediaType = "video/mp4" }
        ], url => url.EndsWith(".png") ? png : mp4);
        Assert.Equal(["base64", "file"], resolved.Select(a => a.Type));
    }

    [Fact]
    public void VideoOverTheCap_IsSkipped()
    {
        string path = Path.Combine(_dir, "huge.mp4");
        using (FileStream fs = File.Create(path))
        {
            fs.SetLength(MediaStorageService.MaxVideoBytes + 1L);
        }
        List<LLMMediaAttachment> resolved = MediaResolver.ResolveForLLM(
            [new LLMMediaAttachment { Type = "url", Data = "View/u/huge.mp4", MediaType = "video/mp4" }], _ => path);
        Assert.Null(resolved);
    }

    [Fact]
    public void VideoAtTheCap_IsKept()
    {
        string path = Path.Combine(_dir, "max.mp4");
        using (FileStream fs = File.Create(path))
        {
            fs.SetLength(MediaStorageService.MaxVideoBytes);
        }
        List<LLMMediaAttachment> resolved = MediaResolver.ResolveForLLM(
            [new LLMMediaAttachment { Type = "url", Data = "View/u/max.mp4", MediaType = "video/mp4" }], _ => path);
        Assert.Equal("file", Assert.Single(resolved).Type);
    }

    [Fact]
    public void MissingFileAndTraversal_AreSkipped()
    {
        Assert.Null(MediaResolver.ResolveForLLM(
            [new LLMMediaAttachment { Type = "url", Data = "View/u/gone.mp4", MediaType = "video/mp4" }], _ => Path.Combine(_dir, "gone.mp4")));
        Assert.Null(MediaResolver.ResolveForLLM(
            [new LLMMediaAttachment { Type = "url", Data = "View/../../etc/x.mp4", MediaType = "video/mp4" }], _ => null));
    }

    [Fact]
    public void RemoteAndInlineVideo_PassThroughUnchanged()
    {
        LLMMediaAttachment remote = new() { Type = "url", Data = "https://example.com/c.mp4", MediaType = "video/mp4" };
        LLMMediaAttachment inline = new() { Type = "base64", Data = "AAAA", MediaType = "video/webm" };
        List<LLMMediaAttachment> resolved = MediaResolver.ResolveForLLM([remote, inline], _ => throw new InvalidOperationException("must not resolve a path"));
        Assert.Same(remote, resolved[0]);
        Assert.Same(inline, resolved[1]);
    }

    [Theory]
    [InlineData("video/mp4", true)]
    [InlineData("VIDEO/WEBM", true)]
    [InlineData("image/png", false)]
    [InlineData(null, false)]
    public void IsVideo_FollowsTheMediaType(string mediaType, bool expected)
    {
        Assert.Equal(expected, new LLMMediaAttachment { MediaType = mediaType }.IsVideo);
    }
}
