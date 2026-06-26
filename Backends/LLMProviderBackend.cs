using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Extensions.LLMAssistant.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.Backends;

/// <summary>Shared base for the extension's removable LLM backends.
/// <para>Each concrete backend is both a Swarm <see cref="AbstractLLMBackend"/> (so it shows under
/// Server &gt; Backends with config UI + lifecycle) and an <see cref="ILLMProvider"/> (the rich path
/// the extension actually uses). This base self-registers the instance into
/// <see cref="LLMProviderRegistry"/> on <see cref="Init"/> and removes it on <see cref="Shutdown"/>,
/// bridges the thin core <c>Generate</c>/<c>GenerateLive</c> overloads to the rich ones, and
/// provides a default streaming-accumulator <see cref="Generate(ExtendedLLMInput)"/>.</para>
/// <para>This whole <c>Backends/</c> folder is the removable pack — delete it (and the
/// <c>LLMBackendPack.Register()</c> call) to fall back to a native Swarm LLM API.</para></summary>
public abstract class LLMProviderBackend : AbstractLLMBackend, ILLMProvider
{
    /// <summary>Short provider kind, eg <c>"anthropic"</c>. Combined with the backend instance id
    /// to form a unique <see cref="Id"/>.</summary>
    public abstract string ProviderKind { get; }

    /// <inheritdoc/>
    public string Id => $"{ProviderKind}:{AbstractBackendData?.ID ?? 0}";

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task GenerateLive(ExtendedLLMInput input, string batchId, Action<JObject> onChunk, CancellationToken ct);

    /// <inheritdoc/>
    public virtual int? CountTokens(string text) => null;

    /// <inheritdoc/>
    public async Task<string> Generate(ExtendedLLMInput input)
    {
        StringBuilder output = new();
        await GenerateLive(input, "0", j =>
        {
            if (j.TryGetValue("chunk", out JToken chunk))
            {
                output.Append($"{chunk}");
            }
            else if (j.TryGetValue("result", out JToken result))
            {
                output.Append($"{result}");
            }
        }, CancellationToken.None);
        return output.ToString();
    }

    /// <summary>Per-backend startup (set <see cref="AbstractBackend.Status"/>, validate config, etc.).</summary>
    protected abstract Task OnProviderInit();

    /// <summary>Per-backend teardown (release any held resources).</summary>
    protected abstract Task OnProviderShutdown();

    /// <inheritdoc/>
    public sealed override async Task Init()
    {
        await OnProviderInit();
        // Only expose usable backends to the dispatcher; an errored/disabled one shouldn't be
        // picked as the fallback "first provider".
        if (Status is BackendStatus.RUNNING or BackendStatus.IDLE)
        {
            LLMProviderRegistry.Register(this);
        }
    }

    /// <inheritdoc/>
    public sealed override async Task Shutdown()
    {
        LLMProviderRegistry.Unregister(Id);
        await OnProviderShutdown();
    }

    // --- Thin core-API bridges (rarely used; the extension drives the rich ILLMProvider path) ---

    /// <inheritdoc/>
    public override Task<string> Generate(SwarmUI.LLMs.LLMParamInput user_input) => Generate(ToRich(user_input));

    /// <inheritdoc/>
    public override Task GenerateLive(SwarmUI.LLMs.LLMParamInput user_input, string batchId, Action<JObject> takeOutput)
        => GenerateLive(ToRich(user_input), batchId, takeOutput, CancellationToken.None);

    /// <summary>Adapts the minimal upstream <c>LLMParamInput</c> to the extension's rich input.</summary>
    private static ExtendedLLMInput ToRich(SwarmUI.LLMs.LLMParamInput core)
    {
        ExtendedLLMInput rich = new() { UserMessage = core.UserMessage, Model = core.Model };
        if (!string.IsNullOrEmpty(core.UserMessage))
        {
            rich.Messages.Add(new LLMMessage { Role = LLMRoles.User, Content = core.UserMessage });
        }
        return rich;
    }

    /// <inheritdoc/>
    public override async Task<bool> FreeMemory(bool systemRam) => false;
}
