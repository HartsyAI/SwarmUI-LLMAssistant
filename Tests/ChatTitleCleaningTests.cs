using Hartsy.Extensions.LLMAssistant.WebAPI;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins <see cref="ChatEndpoints.CleanGeneratedTitle"/>: usable titles pass through tidied, and a
/// reasoning model's "thinking out loud" is rejected instead of being saved as the chat title.</summary>
public class ChatTitleCleaningTests
{
    [Theory]
    [InlineData("Sunset Lighthouse Prompt", "Sunset Lighthouse Prompt")]
    [InlineData("Title: \"Fixing Docker Networking\"", "Fixing Docker Networking")]
    [InlineData("**Python Sort Help**\nsome extra line", "Python Sort Help")]
    [InlineData("<think>The user wants a title.</think>Landing Page Draft", "Landing Page Draft")]
    public void CleanGeneratedTitle_UsableOutput_IsTidied(string raw, string expected)
    {
        Assert.Equal(expected, ChatEndpoints.CleanGeneratedTitle(raw));
    }

    [Theory]
    [InlineData("Okay, let's tackle this. The user wants a short title for a chat conversation")]
    [InlineData("The user is asking to implement DeepSeek support")]
    [InlineData("<think>Okay, so the user wants a title and I should")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("...")]
    public void CleanGeneratedTitle_ReasoningOrJunk_IsRejected(string raw)
    {
        Assert.Null(ChatEndpoints.CleanGeneratedTitle(raw));
    }
}
