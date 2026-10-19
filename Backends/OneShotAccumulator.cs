using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using Hartsy.Extensions.LLMAssistant.LLMs;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>Folds the <see cref="LLMProviderBackend.GenerateLive"/> events of one request into an <see cref="LLMGenerateResult"/>: reasoning
/// the provider flags (<c>reasoning: true</c>, wrapped in <c>&lt;think&gt;</c> tags) or writes inline as a <c>&lt;think&gt;</c> block
/// (Qwen3) goes to <see cref="LLMGenerateResult.Reasoning"/>, the rest to the answer; a <c>length</c> stop is kept as the finish reason; an
/// <c>error</c> or <c>cancelled</c> stop makes <see cref="Finish"/> throw with the engine's message instead of returning an empty reply.</summary>
internal sealed class OneShotAccumulator
{
    private readonly StringBuilder _response = new();
    private readonly StringBuilder _reasoning = new();
    private string _error;
    private bool _length;
    private bool _cancelled;

    /// <summary>Takes one event.</summary>
    public void Add(JObject chunk)
    {
        _error ??= LLMProviderBackend.ErrorStopMessage(chunk);
        string stop = chunk.Value<string>("stopReason");
        _length |= stop == "length";
        _cancelled |= stop == "cancelled";
        if (chunk.Value<bool?>("reasoning") == true && chunk.TryGetValue("chunk", out JToken thought))
        {
            _reasoning.Append($"{thought}");
            return;
        }
        LLMProviderBackend.AppendGenerateChunk(_response, chunk);
    }

    /// <summary>The result, or a <see cref="SwarmReadableErrorException"/> carrying the failure for a request that stopped on an error or
    /// was cancelled.</summary>
    public LLMGenerateResult Finish(string model)
    {
        if (_error is not null)
        {
            throw new SwarmReadableErrorException($"The model '{model}' stopped with an error: {_error}");
        }
        if (_cancelled)
        {
            throw new SwarmReadableErrorException($"The request to '{model}' was cancelled before it finished.");
        }
        string response = _response.ToString();
        string reasoning = "";
        if (_reasoning.Length > 0)
        {
            // Flagged reasoning arrives inside the <think> tags the provider added; a block the stop left open is closed by it too.
            reasoning = HartsyLocalLLMProvider.TrySplitThinkBlocks(_reasoning.ToString(), out string flagged, out _) ? flagged ?? "" : _reasoning.ToString();
        }
        else if (HartsyLocalLLMProvider.TrySplitThinkBlocks(response, out string inline, out string answer))
        {
            reasoning = inline ?? "";
            response = answer;
        }
        return new LLMGenerateResult(response, reasoning, _length ? LLMGenerateResult.Length : LLMGenerateResult.Stop);
    }
}
