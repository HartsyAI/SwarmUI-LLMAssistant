using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Which approval mode a reply uses: the request's own, else the one saved on the chat, else auto.</summary>
public class ChatEndpointsApprovalModeTests
{
    [Theory]
    [InlineData("{\"approvalMode\":\"ask\"}", "{\"approvalMode\":\"plan\"}", "ask")]
    [InlineData("{}", "{\"approvalMode\":\"plan\"}", "plan")]
    [InlineData("{\"approvalMode\":\"nonsense\"}", "{\"approvalMode\":\"ask\"}", "ask")]
    [InlineData("{}", "{}", "auto")]
    [InlineData("{\"approvalMode\":\"PLAN\"}", "{}", "plan")]
    public void TheRequestWinsThenTheChatThenAuto(string request, string thread, string expected)
    {
        Assert.Equal(expected, ChatEndpoints.ResolveApprovalMode(JObject.Parse(request), JObject.Parse(thread)));
    }

    [Fact]
    public void ANullThreadAndRequestIsAuto()
    {
        Assert.Equal("auto", ChatEndpoints.ResolveApprovalMode(null, null));
    }
}
