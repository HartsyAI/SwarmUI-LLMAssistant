using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using HartsyInference.Engine.Requests;
using Xunit;
using LocalSettings = Hartsy.Extensions.LLMAssistant.Backends.HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins that the request's sampling defaults have one source, <see cref="ExtendedLLMInput"/>'s <c>Default*</c> constants: the
/// field initializers, the chat endpoints' parameter resolution and the local engine's fallbacks must all agree with them.</summary>
public class SamplingDefaultsTests
{
    [Fact]
    public void ExtendedLLMInputFieldsStartAtTheSharedDefaults()
    {
        ExtendedLLMInput input = new();

        Assert.Equal(ExtendedLLMInput.DefaultTemperature, input.Temperature);
        Assert.Equal(ExtendedLLMInput.DefaultMaxTokens, input.MaxTokens);
        Assert.Equal(ExtendedLLMInput.DefaultTopP, input.TopP);
    }

    [Fact]
    public void ChatEndpointsResolvesMissingParametersToTheSharedDefaults()
    {
        ExtendedLLMInput input = new();

        ChatEndpoints.ApplyParameters(input, null, -1, -1);

        Assert.Equal(ExtendedLLMInput.DefaultTemperature, input.Temperature);
        Assert.Equal(ExtendedLLMInput.DefaultMaxTokens, input.MaxTokens);
        Assert.Equal(ExtendedLLMInput.DefaultTopP, input.TopP);
    }

    [Fact]
    public void LocalEngineFallsBackToTheSharedDefaultsForUnsetSampling()
    {
        // Zero means "not set" for both: the engine must not run with no nucleus cutoff or a zero-token budget.
        ExtendedLLMInput input = new() { MaxTokens = 0, TopP = 0 };

        TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(input, [], null, "cpu", new LocalSettings());

        Assert.Equal(ExtendedLLMInput.DefaultMaxTokens, request.MaxTokens);
        Assert.Equal(ExtendedLLMInput.DefaultTopP, request.TopP);
    }

    [Fact]
    public void LocalEnginePassesAnExplicitTopPThrough()
    {
        ExtendedLLMInput input = new() { TopP = 0.5 };

        TextRequest request = HartsyLocalLLMProvider.BuildRequestCore(input, [], null, "cpu", new LocalSettings());

        Assert.Equal(0.5, request.TopP);
    }
}
