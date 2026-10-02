using System.Runtime.CompilerServices;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>A scripted fake <see cref="ITextService"/>: replays one <see cref="TextChunk"/> sequence per call to
/// <see cref="StreamAsync"/>, in order, and records every request it was handed — the contract
/// <c>HartsyLocalLLMProvider.StreamToolLoopAsync</c> gives <see cref="HartsyInference.Tools.ToolLoop.RunAsync"/>
/// (<c>Engine.Text</c>), without a real engine or GGUF weights. A call past the scripted rounds is a test bug,
/// not a silent empty stream.</summary>
internal sealed class ScriptedTextService : ITextService
{
    private readonly List<IReadOnlyList<TextChunk>> _rounds;
    private int _callIndex;

    /// <summary>Every request <see cref="StreamAsync"/> was called with, in call order.</summary>
    public List<TextRequest> SeenRequests { get; } = [];

    public ScriptedTextService(params IReadOnlyList<TextChunk>[] rounds) => _rounds = [.. rounds];

    public async IAsyncEnumerable<TextChunk> StreamAsync(ModelSpec spec, TextRequest request,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        SeenRequests.Add(request);
        Assert.True(_callIndex < _rounds.Count, "ScriptedTextService.StreamAsync called more times than scripted.");
        foreach (TextChunk chunk in _rounds[_callIndex++])
        {
            await Task.Yield();
            cancel.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    public Task<TextResult> GenerateAsync(ModelSpec spec, TextRequest request, CancellationToken cancel = default)
        => throw new NotSupportedException("ToolLoop only calls StreamAsync.");

    public int CountTokens(ModelSpec spec, string text) => (text?.Length ?? 0) / 4;

    public bool Unload(string device = null) => false;
}
