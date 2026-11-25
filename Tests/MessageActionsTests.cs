using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Where a regenerate or a retry starts from (<see cref="MessageActions.TryResolveReplyPoint"/>).</summary>
public class MessageActionsTests
{
    /// <summary>A chat with one answered turn (u1, a1) and a second user message (u2) that got no reply.</summary>
    private static JObject Thread() => new()
    {
        ["id"] = "t1",
        ["activeLeafId"] = "u2",
        ["messages"] = new JArray(
            new JObject { ["id"] = "u1", ["parentId"] = null, ["role"] = "user", ["content"] = "one" },
            new JObject { ["id"] = "a1", ["parentId"] = "u1", ["role"] = "assistant", ["content"] = "reply" },
            new JObject { ["id"] = "u2", ["parentId"] = "a1", ["role"] = "user", ["content"] = "two" })
    };

    [Theory]
    [InlineData("a1", "u1")]
    [InlineData("u2", "u2")]
    [InlineData("u1", "u1")]
    public void A_reply_is_asked_for_again_from_its_parent_and_a_user_message_from_itself(string messageId, string expected)
    {
        Assert.True(MessageActions.TryResolveReplyPoint(Thread(), messageId, out string replyTo, out string error));
        Assert.Equal(expected, replyTo);
        Assert.Null(error);
    }

    [Fact]
    public void A_message_that_is_not_in_the_chat_is_refused()
    {
        Assert.False(MessageActions.TryResolveReplyPoint(Thread(), "nope", out string replyTo, out string error));
        Assert.Null(replyTo);
        Assert.Contains("'nope' not found", error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_reply_with_nothing_before_it_is_refused(bool explicitNull)
    {
        JObject reply = new() { ["id"] = "a0", ["role"] = "assistant", ["content"] = "hello" };
        if (explicitNull)
        {
            reply["parentId"] = JValue.CreateNull();
        }
        JObject thread = new() { ["messages"] = new JArray(reply) };
        Assert.False(MessageActions.TryResolveReplyPoint(thread, "a0", out string replyTo, out string error));
        Assert.Null(replyTo);
        Assert.Contains("no preceding turn", error);
    }

    [Fact]
    public void A_chat_with_no_messages_is_refused()
    {
        Assert.False(MessageActions.TryResolveReplyPoint(new JObject(), "a1", out _, out string error));
        Assert.NotNull(error);
    }
}
