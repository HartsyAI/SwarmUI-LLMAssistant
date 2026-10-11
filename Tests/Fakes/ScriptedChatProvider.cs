using Hartsy.Extensions.LLMAssistant.LLMs;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Tests.Fakes;

/// <summary>A provider whose model turns are scripted: each <see cref="StreamAsync"/> call plays the next round and records the input it was given.</summary>
internal sealed class ScriptedChatProvider(params TextChunk[][] rounds) : ILLMProvider
{
    private int _next;

    public List<ExtendedLLMInput> Inputs { get; } = [];

    public string Id => "scripted";

    public string DisplayName => "Scripted";

    public Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default) => Task.FromResult(new List<LLMModelInfo>());

    public Task<string> Generate(ExtendedLLMInput input, CancellationToken ct = default) => Task.FromResult("");

    public Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct) => Task.CompletedTask;

    public int? CountTokens(string text) => null;

    public Task<bool> Unload() => Task.FromResult(false);

    public async IAsyncEnumerable<TextChunk> StreamAsync(ExtendedLLMInput input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Inputs.Add(input);
        TextChunk[] script = rounds[Math.Min(_next++, rounds.Length - 1)];
        foreach (TextChunk chunk in script)
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
        }
        await Task.CompletedTask;
    }
}
