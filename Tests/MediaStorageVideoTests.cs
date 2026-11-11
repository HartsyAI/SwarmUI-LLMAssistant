using Hartsy.Extensions.LLMAssistant.Services;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins the video upload rules in <see cref="MediaStorageService"/> (the host-independent core,
/// <c>SaveVideoToAsync</c>, writing into a temp dir): the mp4/webm/mov allowlist, the container signature check, the size cap,
/// the thread/message name sanitizing that keeps writes inside the uploads root, and the returned shape.</summary>
public class MediaStorageVideoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llma-video-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>An ISO base media start: a 4-byte box size, then <c>ftyp</c>, a brand and padding.</summary>
    private static byte[] Mp4Bytes(int length = 64)
    {
        byte[] b = new byte[length];
        b[3] = 0x18;
        "ftypisom"u8.CopyTo(b.AsSpan(4));
        return b;
    }

    /// <summary>An EBML start (<c>1A 45 DF A3</c>) followed by padding.</summary>
    private static byte[] WebmBytes(int length = 64)
    {
        byte[] b = new byte[length];
        b[0] = 0x1A; b[1] = 0x45; b[2] = 0xDF; b[3] = 0xA3;
        return b;
    }

    private static string DataUri(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    [Fact]
    public void Cap_IsSixtyFourMegabytes()
    {
        Assert.Equal(64 * 1024 * 1024, MediaStorageService.MaxVideoBytes);
    }

    [Theory]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("video/quicktime", ".mov")]
    public async Task IsoBaseMedia_IsAccepted_ForMp4AndMov(string mime, string ext)
    {
        MediaStorageService.StoredVideo stored = await MediaStorageService.SaveVideoToAsync(_root, "thread1", "msg1", DataUri(mime, Mp4Bytes()));
        Assert.Equal(mime, stored.MimeType);
        Assert.EndsWith(ext, stored.FullPath);
        Assert.True(File.Exists(stored.FullPath));
    }

    [Fact]
    public async Task Ebml_IsAccepted_ForWebm()
    {
        MediaStorageService.StoredVideo stored = await MediaStorageService.SaveVideoToAsync(_root, "thread1", "msg1", DataUri("video/webm", WebmBytes()));
        Assert.Equal("video/webm", stored.MimeType);
        Assert.EndsWith(".webm", stored.FullPath);
    }

    [Fact]
    public async Task ReturnedShape_HasPathMimeAndByteCount_AndStoresTheBytesUnchanged()
    {
        byte[] bytes = Mp4Bytes(200);
        MediaStorageService.StoredVideo stored = await MediaStorageService.SaveVideoToAsync(_root, "thread1", "msg1", DataUri("video/mp4", bytes));
        Assert.Equal(200, stored.BytesWritten);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stored.FullPath));
        // The URL needs the server's output root, so the host-independent core leaves it for SaveVideoAsync to fill in.
        Assert.Null(stored.Url);
    }

    [Fact]
    public async Task MimeCasingAndParameters_AreNormalized()
    {
        MediaStorageService.StoredVideo stored = await MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri("VIDEO/MP4;codecs=avc1", Mp4Bytes()));
        Assert.Equal("video/mp4", stored.MimeType);
    }

    [Theory]
    [InlineData("video/x-matroska")]
    [InlineData("video/avi")]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    public async Task OtherTypes_AreRejected_WithTheAllowedList(string mime)
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri(mime, Mp4Bytes())));
        Assert.Contains("Unsupported video type", ex.Message);
        Assert.Contains("mp4, webm, mov", ex.Message);
    }

    [Fact]
    public async Task Mp4Mime_WithWebmBytes_IsRejected_AndNothingIsWritten()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri("video/mp4", WebmBytes())));
        Assert.Contains("signature", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "t")));
    }

    [Fact]
    public async Task WebmMime_WithMp4Bytes_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri("video/webm", Mp4Bytes())));
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData("video/webm")]
    public async Task ArbitraryBytes_AreRejected(string mime)
    {
        byte[] script = System.Text.Encoding.ASCII.GetBytes("<script>alert(1)</script> not a video at all");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri(mime, script)));
    }

    [Fact]
    public async Task TooShortToHoldASignature_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri("video/webm", [0x1A, 0x45, 0xDF, 0xA3])));
    }

    [Fact]
    public async Task OverTheCap_IsRejected_AtTheEncodedAndDecodedChecks()
    {
        // A clearly oversized payload trips the length pre-check; one just over the cap trips the decoded-size check.
        InvalidOperationException big = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri("video/mp4", Mp4Bytes(4096)), maxBytes: 1024));
        Assert.Contains("too large", big.Message);
        InvalidOperationException edge = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri("video/mp4", Mp4Bytes(1025)), maxBytes: 1024));
        Assert.Contains("too large", edge.Message);
        // Exactly at the cap is fine.
        MediaStorageService.StoredVideo ok = await MediaStorageService.SaveVideoToAsync(_root, "t", "m", DataUri("video/mp4", Mp4Bytes(1024)), maxBytes: 1024);
        Assert.Equal(1024, ok.BytesWritten);
    }

    [Fact]
    public async Task InvalidBase64_IsRejected_WithAReadableMessage()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MediaStorageService.SaveVideoToAsync(_root, "t", "m", "data:video/mp4;base64,!!!not base64!!!"));
        Assert.Contains("base64", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a data uri")]
    [InlineData("data:video/mp4;base64")]
    public async Task NotADataUri_ReturnsNull(string input)
    {
        Assert.Null(await MediaStorageService.SaveVideoToAsync(_root, "t", "m", input));
    }

    [Fact]
    public async Task ThreadAndMessageIds_AreSanitized_SoTheFileStaysUnderTheUploadsRoot()
    {
        MediaStorageService.StoredVideo stored = await MediaStorageService.SaveVideoToAsync(
            _root, "../../escape", "..\\..\\evil/name", DataUri("video/mp4", Mp4Bytes()));
        string rootFull = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(rootFull, stored.FullPath);
        string relative = Path.GetRelativePath(_root, stored.FullPath);
        Assert.Equal(2, relative.Split(Path.DirectorySeparatorChar).Length);
        Assert.DoesNotContain("..", relative);
    }

    [Fact]
    public async Task MissingIds_GetFallbackNames_InTheSameThreadFolderImagesUse()
    {
        MediaStorageService.StoredVideo stored = await MediaStorageService.SaveVideoToAsync(_root, "", "", DataUri("video/mp4", Mp4Bytes()));
        string[] parts = Path.GetRelativePath(_root, stored.FullPath).Split(Path.DirectorySeparatorChar);
        Assert.Equal("_thread", parts[0]);
        Assert.StartsWith("vid-", parts[1]);
        Assert.Equal("t1", MediaStorageService.SanitizeIdComponent("t1", "x"));
        Assert.Equal("x", MediaStorageService.SanitizeIdComponent("///", "x"));
    }

    [Fact]
    public async Task FileLandsAtThreadSlashMessage_WithTheMimeExtension()
    {
        MediaStorageService.StoredVideo stored = await MediaStorageService.SaveVideoToAsync(_root, "thr-1", "msg_2", DataUri("video/quicktime", Mp4Bytes()));
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "thr-1", "msg_2.mov")), stored.FullPath);
    }

    [Theory]
    [InlineData("video/mp4", true)]
    [InlineData("video/quicktime", true)]
    [InlineData("video/webm", false)]
    public void HasVideoSignature_MatchesTheContainerFamily(string mime, bool iso)
    {
        Assert.Equal(iso, MediaStorageService.HasVideoSignature(Mp4Bytes(), mime));
        Assert.Equal(!iso, MediaStorageService.HasVideoSignature(WebmBytes(), mime));
        Assert.False(MediaStorageService.HasVideoSignature(null, mime));
    }
}
