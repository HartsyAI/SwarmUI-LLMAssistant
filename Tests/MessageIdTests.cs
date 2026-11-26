using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The id a message joins a chat under: one that can go into the page, and nobody else's.</summary>
public class MessageIdTests
{
    [Theory]
    [InlineData("mwx0gro5-9254tsa", true)]
    [InlineData("0ec570bad7354bac8120872729815e08", true)]
    [InlineData("a_b", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("quo\"te", false)]
    [InlineData("sp ace", false)]
    [InlineData("a]b", false)]
    [InlineData("café", false)]
    public void Only_letters_digits_dashes_and_underscores_are_kept(string id, bool valid) => Assert.Equal(valid, ThreadStorageService.IsValidMessageId(id));

    [Fact]
    public void An_id_has_a_longest_length()
    {
        Assert.True(ThreadStorageService.IsValidMessageId(new string('x', ThreadStorageService.MaxMessageIdChars)));
        Assert.False(ThreadStorageService.IsValidMessageId(new string('x', ThreadStorageService.MaxMessageIdChars + 1)));
    }

    [Fact]
    public void A_free_id_is_kept_and_a_missing_taken_or_unusable_one_is_replaced()
    {
        JArray messages = [new JObject { ["id"] = "taken" }];
        JObject free = new() { ["id"] = "free" };
        ThreadStorageService.ClaimMessageId(messages, free);
        Assert.Equal("free", free["id"].ToString());
        foreach (JObject message in new[] { new JObject(), new JObject { ["id"] = "taken" }, new JObject { ["id"] = "bad id" }, new JObject { ["id"] = JValue.CreateNull() } })
        {
            ThreadStorageService.ClaimMessageId(messages, message);
            string id = message["id"].ToString();
            Assert.True(ThreadStorageService.IsValidMessageId(id), id);
            Assert.NotEqual("taken", id);
            Assert.NotEqual("bad id", id);
        }
    }
}
