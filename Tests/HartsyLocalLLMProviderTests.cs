using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using HartsyInference.Engine.Requests;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Unit tests for <see cref="HartsyLocalLLMProvider"/>'s pure, static role/message mapping
/// (<c>RoleFor</c>/<c>ToTextMessage</c>, both <c>internal</c> via <c>InternalsVisibleTo</c>). These run with no
/// SwarmUI host, no <c>Program.ServerSettings</c> and no constructed provider instance — the provider's settings
/// (<c>SettingsRaw</c>) are populated by SwarmUI's backend framework at runtime, which this project does not have,
/// so only the static helpers that don't read <c>Settings</c> are exercised here.</summary>
public class HartsyLocalLLMProviderTests
{
    [Theory]
    [InlineData(LLMRoles.System, TextRole.System)]
    [InlineData(LLMRoles.Assistant, TextRole.Assistant)]
    [InlineData(LLMRoles.User, TextRole.User)]
    [InlineData(LLMRoles.Tool, TextRole.Tool)]
    [InlineData("something-unrecognized", TextRole.User)]
    public void RoleFor_MapsEachKnownRole(string input, TextRole expected)
    {
        Assert.Equal(expected, HartsyLocalLLMProvider.RoleFor(input));
    }

    [Fact]
    public void ToTextMessage_ToolRole_CarriesCallIdAndName()
    {
        // Before this fix, RoleFor's switch fell through to TextRole.User for any role it didn't recognize,
        // including "tool" — silently losing the tool-call id/name the engine's chat templates need to render
        // a `tool` turn correctly (Qwen's <tool_response>, the Jinja tool_call_id/name fields).
        LLMMessage toolResult = new()
        {
            Role = LLMRoles.Tool,
            Content = "{\"success\":true}",
            ToolCallId = "call_123",
            Name = "get_time"
        };
        TextMessage msg = HartsyLocalLLMProvider.ToTextMessage(toolResult, toolResult.Content, images: null);
        Assert.Equal(TextRole.Tool, msg.Role);
        Assert.Equal("{\"success\":true}", msg.Content);
        Assert.Equal("call_123", msg.ToolCallId);
        Assert.Equal("get_time", msg.Name);
    }

    [Theory]
    [InlineData(LLMRoles.User)]
    [InlineData(LLMRoles.Assistant)]
    [InlineData(LLMRoles.System)]
    public void ToTextMessage_NonToolRole_NeverCarriesCallIdOrName(string role)
    {
        // A non-tool message that happens to have ToolCallId/Name set (eg stale data on a reused LLMMessage)
        // must not leak them onto the TextMessage -- those fields are only meaningful for a Tool turn.
        LLMMessage msg = new() { Role = role, Content = "hi", ToolCallId = "leaked", Name = "leaked" };
        TextMessage result = HartsyLocalLLMProvider.ToTextMessage(msg, msg.Content, images: null);
        Assert.Null(result.ToolCallId);
        Assert.Null(result.Name);
    }

    [Fact]
    public void ToTextMessage_NoImages_LeavesImagesNull()
    {
        LLMMessage msg = new() { Role = LLMRoles.User, Content = "hello" };
        TextMessage result = HartsyLocalLLMProvider.ToTextMessage(msg, msg.Content, images: null);
        Assert.Null(result.Images);
    }
}
