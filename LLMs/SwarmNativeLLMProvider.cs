using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.LLMs;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>Cutover bridge: exposes SwarmUI's own running <see cref="AbstractLLMBackend"/>s through the
/// extension's <see cref="ILLMProvider"/> seam.
/// <para><b>Disabled by default.</b> The bundled <c>Backends/</c> pack supplies the runtimes today. When
/// SwarmUI ships a first-class native LLM API you'd rather use, delete the pack and call
/// <see cref="Register"/> from <c>LLMAssistantExtension.OnInit</c> instead of <c>LLMBackendPack.Register()</c>
/// — nothing else in the extension changes.</para>
/// <para>The mapping below is intentionally minimal because the current upstream skeleton
/// (<c>AbstractLLMBackend</c>) only exposes <c>Generate</c> / <c>GenerateLive</c> over a thin
/// <c>LLMParamInput</c> (no model listing, no system/history/params/media, no cancellation). When the
/// native API grows those, enrich <see cref="ToCore"/> and <see cref="ListModels"/> here.</para></summary>
public sealed class SwarmNativeLLMProvider : ILLMProvider
{
    /// <inheritdoc/>
    public string Id => "swarm-native";

    /// <inheritdoc/>
    public string DisplayName => "Native SwarmUI LLM";

    /// <summary>Registers a single instance of this bridge into the provider registry.</summary>
    public static void Register() => LLMProviderRegistry.Register(new SwarmNativeLLMProvider());

    private static AbstractLLMBackend FirstRunning()
    {
        AbstractLLMBackend backend = Program.Backends.RunningBackendsOfType<AbstractLLMBackend>().FirstOrDefault();
        return backend ?? throw new SwarmReadableErrorException("No native LLM backend is running. Add one in Server > Backends.");
    }

    /// <summary>Adapts the extension's rich input to the minimal upstream <c>LLMParamInput</c>.</summary>
    private static LLMParamInput ToCore(ExtendedLLMInput input)
    {
        // TODO(native-api): map Messages / SystemPrompt / params / media once the native API carries them.
        return new LLMParamInput { UserMessage = input.UserMessage, Model = input.Model };
    }

    /// <inheritdoc/>
    public Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default)
    {
        // The upstream skeleton has no model-listing API yet; surface nothing rather than guessing.
        return Task.FromResult(new List<LLMModelInfo>());
    }

    /// <inheritdoc/>
    public Task<string> Generate(ExtendedLLMInput input) => FirstRunning().Generate(ToCore(input));

    /// <inheritdoc/>
    public Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct)
    {
        // Skeleton GenerateLive has no CancellationToken; honor ct best-effort before dispatching.
        ct.ThrowIfCancellationRequested();
        // Core's seam is Action-based — this disabled-by-default bridge blocks per event at that
        // boundary; the extension's own providers await the callback natively.
        return FirstRunning().GenerateLive(ToCore(input), batchId, j => onChunk(j).GetAwaiter().GetResult());
    }

    /// <inheritdoc/>
    public int? CountTokens(string text) => null; // No native tokenizer access — caller uses the heuristic.

    /// <inheritdoc/>
    public async Task<bool> Unload()
    {
        // Free every running native LLM backend's memory (best-effort; ignores absence of a backend).
        bool freed = false;
        foreach (AbstractLLMBackend backend in Program.Backends.RunningBackendsOfType<AbstractLLMBackend>())
        {
            try { freed |= await backend.FreeMemory(true); }
            catch { /* one backend failing shouldn't block the rest */ }
        }
        return freed;
    }
}
