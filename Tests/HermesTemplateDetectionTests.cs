using Hartsy.Extensions.LLMAssistant.Backends;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins <see cref="HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(string)"/>/
/// <see cref="HartsyLocalLLMProvider.InstructsHermesJsonToolCallsFromFile"/> — the real chat-template
/// inspection that replaced the filename-based <c>IsHermesFamilyModel</c> after independent review
/// demonstrated it misclassifying DeepSeek's R1 distillations (and, checked while fixing it, two more real
/// families that keep a literal <c>&lt;tool_call&gt;</c> tag but switch the body to an XML form: Qwen3.5 and
/// GLM-4.5) — against real chat templates, not synthetic approximations of them. Every fixture under
/// <c>Fixtures/chat_templates/</c> is the actual, unmodified <c>tokenizer.chat_template</c> string from a real
/// checkpoint; <c>Fixtures/chat_templates/SOURCES.md</c> says where each one came from and reasons through
/// what it actually instructs.</summary>
public class HermesTemplateDetectionTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chat_templates", name));

    [Theory]
    [InlineData("qwen3-4b.jinja")]
    [InlineData("qwen2.5-1.5b-instruct.jinja")]
    public void InstructsHermesJsonToolCalls_QwenHermesTemplates_IsTrue(string fixtureName)
    {
        Assert.True(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(Fixture(fixtureName)));
    }

    [Fact]
    public void InstructsHermesJsonToolCalls_Qwen35_IsFalse_XmlFunctionArgumentsNotHermesJson()
    {
        // What this PR's fix adds beyond the original filename check: Qwen3.5-0.8B keeps the literal
        // <tool_call> tag (a plain "does it contain the tag" check would say yes), but the body it instructs
        // is Qwen3-Coder's <function=name><parameter=key>value</parameter></function> XML form -- never a
        // JSON object with "name"/"arguments" keys. The installed Hermes-JSON filter cannot parse that, so
        // this must be false.
        string template = Fixture("qwen3.5-0.8b.jinja");
        Assert.Contains("<tool_call>", template, StringComparison.Ordinal); // sanity: the tag IS present
        Assert.DoesNotContain("\"name\"", template, StringComparison.Ordinal); // but never as a JSON key
        Assert.DoesNotContain("\"arguments\"", template, StringComparison.Ordinal);
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(template));
    }

    [Theory]
    [InlineData("deepseek-r1-distill-qwen-1.5b.jinja")] // the Qwen-architecture base -- "qwen" in the old filename check
    [InlineData("deepseek-r1-distill-llama-8b.jinja")]  // the Llama-architecture base -- "deepseek" alone matched it too
    public void InstructsHermesJsonToolCalls_DeepSeekR1Distill_IsFalse_TheRegressionIndependentReviewFound(string fixtureName)
    {
        // Both bases: DeepSeek's R1 distillation overwrites the base model's own chat template with its own
        // <｜tool▁calls▁begin｜>... delimiter scheme (note the fullwidth ｜, not ASCII |) -- no `tools`
        // reference, no <tool_call> tag, on either architecture. This is the exact false positive independent
        // review demonstrated against the previous filename-based IsHermesFamilyModel, which matched both
        // ("deepseek" alone, or "qwen" for this base) despite neither template instructing Hermes JSON at all.
        string template = Fixture(fixtureName);
        Assert.DoesNotContain("<tool_call>", template, StringComparison.Ordinal);
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(template));
    }

    [Fact]
    public void InstructsHermesJsonToolCalls_Glm4_0414_IsFalse_DescribesToolsInProseNotATag()
    {
        // References `tools`, but describes each one as a raw JSON schema dump in prose (in Chinese) --
        // never instructs a <tool_call> tag at all.
        string template = Fixture("glm-4-9b-0414.jinja");
        Assert.Contains("tools", template, StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", template, StringComparison.Ordinal);
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(template));
    }

    [Fact]
    public void InstructsHermesJsonToolCalls_Glm45_IsFalse_XmlArgKeyValueNotHermesJson()
    {
        // GLM-4.5 (fetched from its published chat_template.jinja -- not on disk locally): also keeps a
        // literal <tool_call> tag, but the body is <arg_key>.../<arg_value>... XML pairs, not a JSON object.
        string template = Fixture("glm-4.5.jinja");
        Assert.Contains("<tool_call>", template, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\"", template, StringComparison.Ordinal);
        Assert.DoesNotContain("\"arguments\"", template, StringComparison.Ordinal);
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(template));
    }

    [Fact]
    public void InstructsHermesJsonToolCalls_Llama32_IsFalse_BareParametersObjectNoTag()
    {
        // References `tools`, instructs a bare {"name": ..., "parameters": ...} object -- note "parameters",
        // not "arguments" -- with no surrounding tag at all.
        string template = Fixture("llama-3.2-1b-instruct.jinja");
        Assert.Contains("tools", template, StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", template, StringComparison.Ordinal);
        Assert.Contains("\"name\"", template, StringComparison.Ordinal);
        Assert.DoesNotContain("\"arguments\"", template, StringComparison.Ordinal); // "parameters" instead
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(template));
    }

    [Theory]
    [InlineData("mistral-7b-instruct-v0.3.jinja")]
    [InlineData("phi-3-mini-4k-instruct.jinja")]
    public void InstructsHermesJsonToolCalls_NoToolCallingInstructionsAtAll_IsFalse(string fixtureName)
    {
        string template = Fixture(fixtureName);
        Assert.DoesNotContain("tools", template, StringComparison.Ordinal);
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(template));
    }

    [Fact]
    public void InstructsHermesJsonToolCalls_Gemma4_IsFalse_PipedTagIsNotTheLiteralHermesTag()
    {
        // References `tools`, but its tag is <|tool_call> / <tool_call|> (a leading/trailing pipe) -- neither
        // contains the literal substring "<tool_call>" the Hermes filter and this check both key on.
        string template = Fixture("gemma-4-e2b-it.jinja");
        Assert.Contains("tools", template, StringComparison.Ordinal);
        Assert.Contains("<|tool_call>", template, StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", template, StringComparison.Ordinal);
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(template));
    }

    [Fact]
    public void InstructsHermesJsonToolCalls_ChatMlWithNoToolsAtAll_IsFalse()
    {
        // A synthetic plain-ChatML template (not from a real checkpoint, unlike every other fixture here) --
        // the simplest possible "no tool-calling story whatsoever" case, including no `tools` reference.
        const string plainChatMl = "{% for message in messages %}<|im_start|>{{ message['role'] }}\n{{ message['content'] }}<|im_end|>\n{% endfor %}";
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(plainChatMl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InstructsHermesJsonToolCalls_NullOrBlank_IsFalse(string template)
    {
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(template));
    }

    [Fact]
    public void InstructsHermesJsonToolCalls_EscapedQuotes_AreNormalizedBeforeMatching()
    {
        // "after normalizing \" -> \"": a template arriving with its quotes backslash-escaped (eg if the
        // metadata round-tripped through something that JSON-encoded the string) must classify identically to
        // the same template with literal quotes.
        string escaped = "{%- if tools -%}<tool_call>\\n{\\\"name\\\": \\\"x\\\", \\\"arguments\\\": {}}\\n</tool_call>{%- endif -%}";
        Assert.True(HartsyLocalLLMProvider.InstructsHermesJsonToolCalls(escaped));
    }

    [Fact]
    public void InstructsHermesJsonToolCallsFromFile_RealLocalQwen3Gguf_IsTrue()
    {
        // The one test the task asks for that runs a real local GGUF path through the actual GgufLoader-backed
        // reader, not a fixture string. Skips (passes trivially) rather than failing when this machine doesn't
        // have the model on disk -- the model-storage mount is specific to the dev box this was written on,
        // same as the engine repo's own [RealWeights]-gated tests.
        const string path = "/mnt/model-storage/Models/llm/qwen3/Qwen3-4B-Q4_K_M.gguf";
        if (!File.Exists(path))
        {
            return;
        }
        Assert.True(HartsyLocalLLMProvider.InstructsHermesJsonToolCallsFromFile(path));
    }

    [Fact]
    public void InstructsHermesJsonToolCallsFromFile_MissingFile_IsFalseNotThrow()
    {
        Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCallsFromFile("/no/such/file-ever.gguf"));
    }

    [Fact]
    public void InstructsHermesJsonToolCallsFromFile_CachesByPathLengthAndWriteTime()
    {
        // The cache key is (path, length, last-write-time), so replacing a file's content (changing its
        // length) at the same path must be reflected, not served from a stale cached verdict.
        string tempPath = Path.Combine(Path.GetTempPath(), $"w4_hermes_cache_test_{Guid.NewGuid():N}.gguf");
        try
        {
            File.WriteAllBytes(tempPath, new byte[] { 1, 2, 3 }); // not a real GGUF -- Load() will fail -> false, cached
            Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCallsFromFile(tempPath));
            Assert.False(HartsyLocalLLMProvider.InstructsHermesJsonToolCallsFromFile(tempPath)); // second call hits the cache, same answer
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
