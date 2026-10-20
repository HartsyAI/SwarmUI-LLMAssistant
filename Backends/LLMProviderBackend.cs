using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.LLMs;
using SwarmUI.Utils;
using Hartsy.Extensions.LLMAssistant.LLMs;

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
        string engineError = null;
        await GenerateLive(input, "0", j =>
        {
            engineError ??= ErrorStopMessage(j);
            AppendGenerateChunk(output, j);
            return Task.CompletedTask;
        }, ct);
        // A failed generation must not read as a reply the model chose to leave empty: the one-shot routes report it as an error.
        if (engineError is not null)
        {
            throw new SwarmReadableErrorException($"The model '{input.Model}' stopped with an error: {engineError}");
        }
        return output.ToString();
    }

    /// <summary>Generates a full response with the reasoning apart from the answer and the finish reason (see <see cref="OneShotAccumulator"/>):
    /// a reply that ran out of tokens mid-reasoning comes back with its partial reasoning, an empty answer and <c>length</c>; an engine
    /// failure throws.</summary>
    public async Task<LLMGenerateResult> GenerateDetailed(ExtendedLLMInput input, CancellationToken ct = default)
    {
        OneShotAccumulator accumulator = new();
        await GenerateLive(input, "0", j =>
        {
            accumulator.Add(j);
            return Task.CompletedTask;
        }, ct);
        return accumulator.Finish(input.Model);
    }

    /// <summary>The message of a <c>{stopReason: "error"}</c> event (its <c>error</c> text, else a generic one), or null for any other event.</summary>
    internal static string ErrorStopMessage(JObject chunk)
    {
        if (chunk.Value<string>("stopReason") != "error")
        {
            return null;
        }
        string message = chunk.Value<string>("error");
        return string.IsNullOrWhiteSpace(message) ? "the backend reported a failure without a message" : message;
    }

    /// <summary>The one-shot accumulation step: folds one <see cref="GenerateLive"/> event into <paramref name="output"/>. Answer text and
    /// result text are kept; reasoning the provider flags (<c>reasoning: true</c>) and native tool calls are not part of a one-shot reply.</summary>
    internal static void AppendGenerateChunk(StringBuilder output, JObject chunk)
    {
        if (chunk.TryGetValue("chunk", out JToken text))
        {
            // Reasoning a provider reports apart from the answer (flagged) is shown in chat, never part of a one-shot reply.
            if (chunk.Value<bool?>("reasoning") != true)
            {
                output.Append($"{text}");
            }
        }
        else if (chunk.TryGetValue("result", out JToken result))
        {
            output.Append($"{result}");
        }
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
