using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using HartsyInference.Engine.Requests;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Covers how a stored assistant reply's <c>&lt;think&gt;</c> block goes back to the engine: which real chat templates take
/// it as <c>reasoning_content</c> (<see cref="HartsyLocalLLMProvider.TakesReasoningContent(string)"/>), how a reply is split
/// (<see cref="HartsyLocalLLMProvider.TrySplitLeadingThink"/>), and that <see cref="HartsyLocalLLMProvider.ToTextMessage"/> leaves every
/// other message as it was.</summary>
public class ReasoningHistoryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chat_templates", name));

    [Theory]
    [InlineData("muse-spark-open.jinja")]
    [InlineData("gemma-4-e2b-it.jinja")]
    public void TakesReasoningContent_TemplatesThatReadOnlyTheField_AreTrue(string fixtureName)
    {
        string template = Fixture(fixtureName);
        Assert.DoesNotContain("</think>", template, StringComparison.Ordinal);
        Assert.True(HartsyLocalLLMProvider.TakesReasoningContent(template));
    }

    [Theory]
    [InlineData("qwen3-4b.jinja")]
    [InlineData("qwen3.5-0.8b.jinja")]
    [InlineData("glm-4.5.jinja")]
    public void TakesReasoningContent_TemplatesThatSplitThinkThemselves_AreFalse(string fixtureName)
    {
        // They read reasoning_content too, but fall back to splitting the content on </think>; their render stays exactly as before.
        string template = Fixture(fixtureName);
        Assert.Contains("reasoning_content", template, StringComparison.Ordinal);
        Assert.False(HartsyLocalLLMProvider.TakesReasoningContent(template));
    }

    [Theory]
    [InlineData("deepseek-r1-distill-qwen-1.5b.jinja")]
    [InlineData("deepseek-r1-distill-llama-8b.jinja")]
    [InlineData("qwen2.5-1.5b-instruct.jinja")]
    [InlineData("glm-4-9b-0414.jinja")]
    [InlineData("llama-3.2-1b-instruct.jinja")]
    [InlineData("mistral-7b-instruct-v0.3.jinja")]
    [InlineData("phi-3-mini-4k-instruct.jinja")]
    public void TakesReasoningContent_TemplatesThatNeverReadTheField_AreFalse(string fixtureName)
    {
        // R1 keeps the newlines after </think> in its own split, so a split on this side would change what it renders.
        string template = Fixture(fixtureName);
        Assert.DoesNotContain("reasoning_content", template, StringComparison.Ordinal);
        Assert.False(HartsyLocalLLMProvider.TakesReasoningContent(template));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TakesReasoningContent_NoTemplate_IsFalse(string template)
    {
        Assert.False(HartsyLocalLLMProvider.TakesReasoningContent(template));
    }

    [Fact]
    public void TakesReasoningContentFromFile_MissingFile_IsFalseNotThrow()
    {
        Assert.False(HartsyLocalLLMProvider.TakesReasoningContentFromFile("/no/such/file-ever.gguf"));
    }

    [Theory]
    [InlineData("<think>Let me think.</think>Hi.", "Let me think.", "Hi.")]
    [InlineData("<think>\nLet me think.\n</think>\n\nHi.", "Let me think.", "Hi.")]
    [InlineData("  \n<think>r</think>answer", "r", "answer")]
    [InlineData("<think>r</think>a<think>unclosed", "r", "a<think>unclosed")]
    [InlineData("<think>r</think>", "r", "")]
    [InlineData("<think>\r\nplan\r\n</think>\r\n\r\nAnswer", "plan", "Answer")]
    [InlineData("<think> r </think> a", "r", "a")]
    public void TrySplitLeadingThink_OneLeadingBlock_SplitsReasoningFromTheAnswer(string content, string reasoning, string answer)
    {
        Assert.True(HartsyLocalLLMProvider.TrySplitLeadingThink(content, out string gotReasoning, out string gotAnswer));
        Assert.Equal(reasoning, gotReasoning);
        Assert.Equal(answer, gotAnswer);
    }

    [Fact]
    public void TrySplitLeadingThink_EmptyBlock_HasNoReasoning()
    {
        Assert.True(HartsyLocalLLMProvider.TrySplitLeadingThink("<think>\n\n</think>\n\nHi.", out string reasoning, out string answer));
        Assert.Null(reasoning);
        Assert.Equal("Hi.", answer);
    }

    [Theory]
    [InlineData("Hi.")]
    [InlineData("Answer first <think>aside</think> then more.")]
    [InlineData("<think>never closed")]
    [InlineData("<think>a<think>b</think>c")]
    [InlineData("<think>r1</think><think>r2</think>answer")]
    [InlineData("<think>r1</think>text<think>r2</think>answer")]
    [InlineData("")]
    [InlineData(null)]
    public void TrySplitLeadingThink_AnythingElse_LeavesTheReplyAsItIs(string content)
    {
        Assert.False(HartsyLocalLLMProvider.TrySplitLeadingThink(content, out string reasoning, out string answer));
        Assert.Null(reasoning);
        Assert.Equal(content, answer);
    }

    [Fact]
    public void HasLeadingThinkReply_OnlyAnAssistantReplyStartingWithThinkCounts()
    {
        // BuildRequestAsync reads the model's template only when this is true, so a plain chat never opens the GGUF for it.
        Assert.False(HartsyLocalLLMProvider.HasLeadingThinkReply([]));
        Assert.False(HartsyLocalLLMProvider.HasLeadingThinkReply([
            new() { Role = LLMRoles.User, Content = "<think>quoted</think>" },
            new() { Role = LLMRoles.Assistant, Content = "Plain answer <think>aside</think>" },
            new() { Role = LLMRoles.Assistant, Content = null }
        ]));
        Assert.True(HartsyLocalLLMProvider.HasLeadingThinkReply([
            new() { Role = LLMRoles.User, Content = "hi" },
            new() { Role = LLMRoles.Assistant, Content = "\n<think>r</think>Hello" }
        ]));
    }

    [Fact]
    public void ToTextMessage_SplitReasoning_MovesTheLeadingBlockToReasoningContent()
    {
        LLMMessage reply = new() { Role = LLMRoles.Assistant, Content = "<think>The user said hi.</think>Hello!" };
        TextMessage msg = HartsyLocalLLMProvider.ToTextMessage(reply, reply.Content, images: null, splitReasoning: true);
        Assert.Equal(TextRole.Assistant, msg.Role);
        Assert.Equal("Hello!", msg.Content);
        Assert.Equal("The user said hi.", msg.ReasoningContent);
    }

    [Fact]
    public void ToTextMessage_WithoutSplitReasoning_LeavesTheReplyByteIdentical()
    {
        // What every template except Muse Spark's and Gemma 4's gets, Qwen3 and R1 included.
        LLMMessage reply = new() { Role = LLMRoles.Assistant, Content = "<think>\nThe user said hi.\n</think>\n\nHello!" };
        TextMessage msg = HartsyLocalLLMProvider.ToTextMessage(reply, reply.Content, images: null);
        Assert.Equal(reply.Content, msg.Content);
        Assert.Null(msg.ReasoningContent);
    }

    [Theory]
    [InlineData(LLMRoles.User)]
    [InlineData(LLMRoles.System)]
    [InlineData(LLMRoles.Tool)]
    public void ToTextMessage_SplitReasoning_OnlyTouchesAssistantTurns(string role)
    {
        LLMMessage message = new() { Role = role, Content = "<think>quoted</think>text" };
        TextMessage msg = HartsyLocalLLMProvider.ToTextMessage(message, message.Content, images: null, splitReasoning: true);
        Assert.Equal(message.Content, msg.Content);
        Assert.Null(msg.ReasoningContent);
    }

    [Fact]
    public void ToTextMessage_SplitReasoning_KeepsAMultiRoundReplyAsItIs()
    {
        LLMMessage reply = new() { Role = LLMRoles.Assistant, Content = "<think>r1</think><think>r2</think>Noon." };
        TextMessage msg = HartsyLocalLLMProvider.ToTextMessage(reply, reply.Content, images: null, splitReasoning: true);
        Assert.Equal(reply.Content, msg.Content);
        Assert.Null(msg.ReasoningContent);
    }
}
