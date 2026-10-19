using System.Text.RegularExpressions;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins the composer's Thinking toggle wiring in the shipped page and scripts (no browser runs in this project): the button sits
/// in the chat panel's composer, a click cycles Auto → On → Off and is remembered, and every request built by <c>llmaBuildPayload</c>
/// (send, edit, regenerate, compare) carries <c>enableThinking</c> true or false, or leaves it out for Auto so the template default applies.
/// The server side of the field is <see cref="WebAPI.ChatEndpoints.ReadOptionalBool(Newtonsoft.Json.Linq.JObject, string)"/>.</summary>
public class ThinkingToggleWiringTests
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
    public void ChatPanelComposer_HasTheToggleButton_StartingAtAuto()
    {
        string html = Read("Tabs", "Text2Image", "LLM Assistant.html");
        int panel = html.IndexOf("id=\"llma-chat-panel\"", StringComparison.Ordinal);
        int actions = html.IndexOf("class=\"llma-input-actions-left\"", panel, StringComparison.Ordinal);
        int button = html.IndexOf("id=\"llma-thinking-toggle-btn\"", StringComparison.Ordinal);
        int send = html.IndexOf("id=\"llma-send-btn\"", StringComparison.Ordinal);
        Assert.True(panel >= 0 && actions > panel && button > actions && send > button, "The toggle must sit in the chat panel's composer actions.");
        Assert.Matches(new Regex("class=\"llma-thinking-toggle-label\">Auto<"), html);
    }

    [Fact]
    public void ModesCycle_AutoOnOff_AndMapToTheRequestField()
    {
        string utils = Read("Assets", "utils.js");
        Assert.Contains("thinkingMode:      'auto'", utils);
        Assert.Contains("LLMA_THINKING_MODES = Object.freeze(['auto', 'on', 'off'])", utils);
        Assert.Contains("if (mode === 'on') return true;", utils);
        Assert.Contains("if (mode === 'off') return false;", utils);
        Assert.Contains("return undefined;", utils);
    }

    [Fact]
    public void Click_CyclesAndRemembersTheMode()
    {
        string composer = Read("Assets", "chat", "chat-composer.js");
        Assert.Contains("document.getElementById('llma-thinking-toggle-btn')", composer);
        Assert.Contains("LLMAState.thinkingMode = llmaNextThinkingMode(LLMAState.thinkingMode);", composer);
        Assert.Contains("llmaSaveThinkingMode(LLMAState.thinkingMode);", composer);
        Assert.Contains("LLMAState.thinkingMode = llmaLoadThinkingMode();", composer);
    }

    [Fact]
    public void EveryRequestPayload_CarriesTheModesValue()
    {
        string pipeline = Read("Assets", "chat", "chat-pipeline.js");
        int builder = pipeline.IndexOf("function llmaBuildPayload(", StringComparison.Ordinal);
        int field = pipeline.IndexOf("payload.enableThinking = enableThinking;", StringComparison.Ordinal);
        int end = pipeline.IndexOf("return payload;", builder, StringComparison.Ordinal);
        Assert.True(builder >= 0 && field > builder && end > field, "llmaBuildPayload must set enableThinking.");
        Assert.Contains("const enableThinking = llmaThinkingRequestValue(LLMAState.thinkingMode);", pipeline);
        Assert.Contains("if (enableThinking !== undefined) payload.enableThinking = enableThinking;", pipeline);
    }
}
