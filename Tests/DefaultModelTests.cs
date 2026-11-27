using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>A request that names no model must still reach a backend that cannot answer without a name, as the local GGUF engine cannot. The mock server of the browser specs sits behind a backend that fills
/// its own default in, so it never sees such a request; the provider here is as strict as the local one. The registry is process-wide, so these run with the other tests that change it.</summary>
[Collection("LLMProviderRegistry")]
public sealed class DefaultModelTests
{
    /// <summary>A provider that, like the local engine, refuses a request naming no model it has.</summary>
    private sealed class StrictProvider(string defaultModel, params string[] models) : ILLMProvider
    {
        /// <summary>The model of each request answered.</summary>
        public readonly List<string> Asked = [];

        public string Id { get; } = "strict-" + Guid.NewGuid().ToString("N");
        public string DisplayName => Id;
        public string DefaultModelId => defaultModel;
        public Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default) => Task.FromResult(models.Select(m => new LLMModelInfo { Id = m }).ToList());
        public Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default)
        {
            if (input.Model != defaultModel && !models.Contains(input.Model))
            {
                throw new InvalidOperationException($"LLM model '{input.Model}' not found in the LLM model folder(s).");
            }
            Asked.Add(input.Model);
            return Task.FromResult("A rewritten cat.");
        }
        public Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct) => Task.CompletedTask;
        public int? CountTokens(string text) => null;
        public Task<bool> Unload() => Task.FromResult(false);
    }

    /// <summary>Runs <paramref name="test"/> with only these providers registered, and puts the registry back as it was.</summary>
    private static async Task WithProviders(ILLMProvider[] providers, Func<Task> test)
    {
        List<ILLMProvider> before = [.. LLMProviderRegistry.All];
        foreach (ILLMProvider p in before)
        {
            LLMProviderRegistry.Unregister(p.Id);
        }
        foreach (ILLMProvider p in providers)
        {
            LLMProviderRegistry.Register(p);
        }
        try
        {
            await test();
        }
        finally
        {
            foreach (ILLMProvider p in providers)
            {
                LLMProviderRegistry.Unregister(p.Id);
            }
            foreach (ILLMProvider p in before)
            {
                LLMProviderRegistry.Register(p);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ARequestWithNoModelGoesOutWithTheFirstListedOne(string none)
    {
        StrictProvider local = new(null, "first.gguf", "second.gguf");
        await WithProviders([local], async () =>
        {
            Assert.Equal("A rewritten cat.", await LLMDispatcher.Generate(ExtendedLLMInput.Create("cat", null, none)));
            Assert.Equal("A rewritten cat.", (await LLMDispatcher.GenerateDetailed(ExtendedLLMInput.Create("cat", null, none))).Response);
            Assert.Equal(["first.gguf", "first.gguf"], local.Asked);
        });
    }

    [Fact]
    public async Task TheBackendsOwnDefaultComesBeforeItsListAndANamedModelIsKept()
    {
        StrictProvider remote = new("its-default", "listed");
        await WithProviders([remote], async () =>
        {
            await LLMDispatcher.Generate(ExtendedLLMInput.Create("cat"));
            await LLMDispatcher.Generate(ExtendedLLMInput.Create("cat", null, "listed"));
            Assert.Equal(["its-default", "listed"], remote.Asked);
        });
    }

    [Fact]
    public async Task ARewriteThatNamesNoModelIsWrittenByTheDefaultOne()
    {
        StrictProvider local = new(null, "writer.gguf");
        await WithProviders([local], async () =>
        {
            PromptStyle style = new("s", "image", "s", "d", [], "text", "", "none", [], [], "", "Write a prompt.", "bundled");
            RewriteSettings settings = new("", 5, 2048, 0.4);
            RewriteRequest request = new("cat", "", "img.safetensors", 0, 0, "", "", Fresh: true);
            RewriteResult result = await PromptRewriter.RunAsync(style, request, settings, PromptRewriteService.DispatcherGenerate(request, settings, null), default);
            Assert.False(result.FellBack, string.Join(" ", result.Warnings));
            Assert.Equal("A rewritten cat.", result.Text);
            Assert.Equal(["writer.gguf"], local.Asked);
        });
    }

    [Theory]
    [InlineData("asked", "saved", "chat", "asked")]
    [InlineData("", "saved", "chat", "saved")]
    [InlineData(null, " saved ", "chat", "saved")]
    [InlineData("", "", "chat", "chat")]
    [InlineData("", " ", " chat ", "chat")]
    [InlineData("", "", null, "")]
    public void TheWritingModelIsTheOneAskedForThenTheSettingThenTheChats(string asked, string saved, string chat, string expected)
        => Assert.Equal(expected, PromptRewriteService.WritingModel(asked, saved, chat));
}
