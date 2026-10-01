using Hartsy.Extensions.LLMAssistant.Backends;
using HartsyInference.Tools.Parsing;
using SwarmUI.Backends;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins the family-detection gate added after independent review: <c>HartsyLocalLLMProvider</c>
/// installs only the Hermes/Qwen tool-call format for its whole engine instance
/// (<c>OnProviderInit</c>), so native tool calling must only be claimed for a model whose own convention is
/// that one — everything else has to keep using the tag-prompt convention that worked before
/// <c>StructuredToolCalling</c> existed, or its tool calls silently stop working (the model renders tools in
/// its own native, non-Hermes format, which the installed filter can't parse, and <c>ApplyToolsToInput</c>
/// — thinking native is in play — never falls back to the tag prompt either).</summary>
public class HermesFamilyDetectionTests
{
    [Theory]
    [InlineData("Qwen3-4B-Q4_K_M.gguf")]
    [InlineData("qwen2.5-7b-instruct.Q4_K_M.gguf")]
    [InlineData("NousResearch-Hermes-3-Llama-3.1-8B.gguf")] // "hermes" substring wins even though "llama" is also present
    [InlineData("glm-4-9b-chat.gguf")]
    [InlineData("DeepSeek-R1-Distill-Qwen-7B.gguf")]
    [InlineData("QWEN3-4B.GGUF")] // case-insensitive
    public void IsHermesFamilyModel_KnownHermesFamilyNames_IsTrue(string modelId)
    {
        Assert.True(HartsyLocalLLMProvider.IsHermesFamilyModel(modelId));
    }

    [Theory]
    [InlineData("Llama-3.2-1B-Instruct-Q4_K_M.gguf")] // docs/Research/TOOL_CALLING.md's own Llama3 target
    [InlineData("Mistral-7B-Instruct-v0.3.Q4_K_M.gguf")] // its own Mistral target
    [InlineData("gemma-4-E2B-it-Q4_K_M.gguf")] // its own Gemma target
    [InlineData("mixtral-8x7b-instruct.gguf")]
    [InlineData("some-random-finetune-7b.gguf")] // unrecognized -- must NOT default to Hermes like
                                                   // ToolCallFormats.Detect would; the safe default here is false
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsHermesFamilyModel_NonHermesOrUnrecognizedNames_IsFalse(string modelId)
    {
        Assert.False(HartsyLocalLLMProvider.IsHermesFamilyModel(modelId));
    }

    [Fact]
    public void IsHermesFamilyModel_DeliberatelyDiffersFromToolCallFormatsDetect_OnAnUnrecognizedName()
    {
        // The whole point of NOT reusing Detect as a yes/no gate: Detect's own fallback is Hermes for
        // anything it doesn't recognize (it always has to return SOME parser), which would be a false
        // positive here. Pin that the two deliberately disagree on this input.
        const string unrecognized = "some-brand-new-model-nobody-has-seen.gguf";
        Assert.Equal(ToolCallFormat.Hermes, ToolCallFormats.Detect(unrecognized));
        Assert.False(HartsyLocalLLMProvider.IsHermesFamilyModel(unrecognized));
    }

    [Fact]
    public void NonHermesModel_StructuredToolCallingScenario_FallsBackToTheTagConvention()
    {
        // This is the regression scenario itself, expressed at the point this PR actually changed:
        // BuildRequestAsync's Tools-gating (SupportsNativeToolCallingFor) and ApplyToolsToInput's tag-prompt
        // skip both key off the SAME check. For a non-Hermes model it must be false even with the operator
        // setting on, so TextRequest.Tools is never set for it (keeping the engine's Hermes-only filter inert
        // for this request) and ApplyToolsToInput still injects the tag prompt -- the already-proven-working
        // path OneShotToolLoopSimulationTests.NativeToolCallingOff_StillUsesTheTagScan_AsToday exercises end
        // to end (a <tool_call> tag as literal chunk text, parsed and dispatched normally).
        const string llamaModel = "Llama-3.2-1B-Instruct-Q4_K_M.gguf";
        const bool structuredToolCallingOn = true;
        bool wouldGoNative = structuredToolCallingOn && HartsyLocalLLMProvider.IsHermesFamilyModel(llamaModel);
        Assert.False(wouldGoNative);
    }

    /// <summary>Same checks, but against a real, directly-constructed <see cref="HartsyLocalLLMProvider"/>
    /// instance rather than only the extracted static helper -- <see cref="AbstractBackend.SettingsRaw"/> is
    /// a plain public field, so this needs neither <c>Program</c>/a live SwarmUI host nor a call into
    /// <c>OnProviderInit</c> (which would need both). <see cref="HartsyLocalLLMProvider.StreamToolLoopAsync"/>/
    /// <c>GenerateLive</c> themselves go further (<c>BuildRequestAsync</c>'s <c>ResolvePath</c> reads
    /// <c>Program.ServerSettings.Paths.ActualModelRoots</c>, a host static this standalone test process never
    /// initializes), which is why those two stay untested here — this is as far as "the real provider,
    /// not just its extracted statics" goes without one.</summary>
    [Fact]
    public void SupportsNativeToolCallingFor_RealProviderInstance_RequiresBothTheSettingAndTheFamily()
    {
        HartsyLocalLLMProvider provider = new()
        {
            SettingsRaw = new HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings { StructuredToolCalling = true }
        };
        Assert.True(provider.SupportsNativeToolCalling); // provider-wide, model-unaware -- true whenever the setting is
        Assert.True(provider.SupportsNativeToolCallingFor("Qwen3-4B-Q4_K_M.gguf"));
        Assert.False(provider.SupportsNativeToolCallingFor("Llama-3.2-1B-Instruct-Q4_K_M.gguf"));
        Assert.False(provider.SupportsNativeToolCallingFor(null));
    }

    [Fact]
    public void SupportsNativeToolCallingFor_RealProviderInstance_SettingOffMeansFalseForEveryModel()
    {
        HartsyLocalLLMProvider provider = new()
        {
            SettingsRaw = new HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings { StructuredToolCalling = false }
        };
        Assert.False(provider.SupportsNativeToolCalling);
        Assert.False(provider.SupportsNativeToolCallingFor("Qwen3-4B-Q4_K_M.gguf")); // Hermes family, but the setting is off
    }
}
