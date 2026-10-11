using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.LLMs;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.Backends;

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
    public abstract Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct);

    /// <inheritdoc/>
    public virtual int? CountTokens(string text) => null;

    /// <inheritdoc/>
    public async Task<bool> Unload() => await FreeMemory(true);

    /// <inheritdoc/>
    public virtual IAsyncEnumerable<HartsyInference.Engine.Requests.TextChunk> StreamAsync(ExtendedLLMInput input, CancellationToken ct)
        => LLMChunkStream.FromFrames((frame, token) => GenerateLive(input, "0", frame, token), ct);

    /// <inheritdoc/>
    public async Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default)
    {
        StringBuilder output = new();
        await GenerateLive(input, "0", j =>
        {
            // Text only: a one-shot caller has no loop to run a tool call, so a native call is not part of the reply.
            if (j["chunk"] is { } chunk) output.Append(chunk.ToString());
            else if (j["result"] is { } result) output.Append(result.ToString());
            return Task.CompletedTask;
        }, ct);
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

    /// <inheritdoc/>
    public override Task<string> Generate(LLMParamInput user_input) => Generate(ToRich(user_input));

    /// <inheritdoc/>
    public override Task GenerateLive(LLMParamInput user_input, string batchId, Action<JObject> takeOutput)
        => GenerateLive(ToRich(user_input), batchId, j => { takeOutput(j); return Task.CompletedTask; }, CancellationToken.None);

    /// <summary>Adapts the minimal upstream <c>LLMParamInput</c> to the extension's rich input.</summary>
    private static ExtendedLLMInput ToRich(LLMParamInput core)
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
