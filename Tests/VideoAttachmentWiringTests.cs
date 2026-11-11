using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins the video-attachment wiring in the shipped page and scripts (no browser runs in this project): the composer's file
/// input accepts mp4/webm/mov next to images, <c>llmaIsVideoModel</c> exists and is strict, the paperclip narrows <c>accept</c> by
/// capability, the video upload route is registered beside the image one, and messages render a <c>&lt;video&gt;</c> for video media.</summary>
public class VideoAttachmentWiringTests
{
    private static string ExtensionRoot()
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SwarmUI-LLMAssistant.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("SwarmUI-LLMAssistant.csproj not found above the test output.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([ExtensionRoot(), .. parts]));

    [Fact]
    public void AttachInput_AcceptsImagesAndMp4WebmMov_WithATranslatedTitle()
    {
        string html = Read("Tabs", "Text2Image", "LLM Assistant.html");
        Assert.Contains("id=\"llma-attach-file\" accept=\"image/*,video/mp4,video/webm,video/quicktime\"", html);
        int label = html.IndexOf("id=\"llma-attach-label\"", StringComparison.Ordinal);
        int end = html.IndexOf('>', label);
        string tag = html[label..end];
        Assert.Contains("title=\"Attach image or video\"", tag);
        Assert.Contains("translate", html[(html.LastIndexOf('<', label))..end]);
    }

    [Fact]
    public void IsVideoModel_Exists_IsStrict_AndIsExported()
    {
        string utils = Read("Assets", "utils.js");
        Assert.Contains("function llmaIsVideoModel(model)", utils);
        Assert.Contains("return model.metadata.video === true || model.metadata.video === 'true';", utils);
        Assert.Contains("window.llmaIsVideoModel = llmaIsVideoModel;", utils);
        Assert.Contains("const LLMA_MAX_VIDEO_BYTES = 64 * 1024 * 1024;", utils);
    }

    [Fact]
    public void Paperclip_NarrowsTheAcceptListByCapability()
    {
        string js = Read("Assets", "llmassistant.js");
        int fn = js.IndexOf("function llmaUpdateAttachAvailability()", StringComparison.Ordinal);
        Assert.True(fn >= 0);
        string body = js[fn..];
        Assert.Contains("llmaIsVideoModel(model)", body);
        Assert.Contains("fileInput.accept = 'image/*';", body);
        Assert.Contains("translate('Attach image or video')", body);
    }

    [Fact]
    public void Composer_GatesVideoDrops_WithAVideoSpecificToast()
    {
        string composer = Read("Assets", "chat", "chat-composer.js");
        Assert.Contains("llmaAttachKind(f) === 'video'", composer);
        string attach = Read("Assets", "chat", "chat-attachments.js");
        Assert.Contains("This model doesn't support video input. Pick a video model such as MOSS-VL.", attach);
        Assert.Contains("LLMAssistantUploadChatVideo", attach);
        Assert.Contains("URL.revokeObjectURL(att.objectUrl)", attach);
    }

    [Fact]
    public void VideoOnlyMessage_GetsAVideoPlaceholder()
    {
        string pipeline = Read("Assets", "chat", "chat-pipeline.js");
        Assert.Contains("'(video)'", pipeline);
    }

    [Fact]
    public void Messages_RenderAVideoPlayer_ForVideoMedia()
    {
        string render = Read("Assets", "chat", "chat-render.js");
        Assert.Contains("type.startsWith('video/')", render);
        Assert.Contains("el.controls = true;", render);
        Assert.Contains("el.preload = 'metadata';", render);
        Assert.Contains("document.createElement(isVideo ? 'video' : 'img')", render);
        Assert.Contains("video.llma-msg-image", Read("Assets", "llma-chat.css"));
    }

    [Fact]
    public void UploadRoute_IsRegisteredNextToTheImageRoute_WithTheSamePermission()
    {
        string api = Read("WebAPI", "LLMAssistantAPI.cs").Replace("\r\n", "\n");
        Assert.Contains("API.RegisterAPICall(ChatEndpoints.LLMAssistantUploadChatImage, true, PermChat);\n        API.RegisterAPICall(ChatEndpoints.LLMAssistantUploadChatVideo, true, PermChat);", api);
    }
}
