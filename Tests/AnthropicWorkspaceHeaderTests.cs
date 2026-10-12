using Hartsy.Extensions.LLMAssistant.Backends;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The workspace header is sent only when a workspace is known, so a key scoped to a workspace is unaffected,
/// and an unscoped key can be pointed at its workspace without a code change.</summary>
public class AnthropicWorkspaceHeaderTests
{
    private static HttpRequestMessage Build(string workspaceId)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        AnthropicLLMProvider.ApplyHeaders(request, "test-key", workspaceId);
        return request;
    }

    [Fact]
    public void WorkspaceHeader_IsOmitted_WhenNoWorkspaceIsKnown()
    {
        HttpRequestMessage request = Build(null);
        Assert.False(request.Headers.Contains("anthropic-workspace-id"));
        Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public void WorkspaceHeader_IsSent_WhenAWorkspaceIsKnown()
    {
        HttpRequestMessage request = Build("wrkspc_test");
        Assert.Equal("wrkspc_test", request.Headers.GetValues("anthropic-workspace-id").Single());
    }

    [Fact]
    public void WorkspaceHeader_IsOmitted_WhenTheWorkspaceIsBlank()
    {
        Assert.False(Build("   ").Headers.Contains("anthropic-workspace-id"));
    }
}
