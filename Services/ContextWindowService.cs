using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>How many tokens a conversation may hold. The window is what the user set, else what the model's listing says
/// (<see cref="LLMModelInfo.ContextLength"/>: a GGUF header, a checkpoint config, or the endpoint's own field), else unknown (0),
/// and an unknown window is never guessed: callers show counts without a percentage and do not compact on their own.</summary>
public static class ContextWindowService
{
    /// <summary>The window in tokens for <paramref name="model"/>, or 0 when neither the settings nor the model say.
    /// <c>parameters.contextWindow</c> in <paramref name="settings"/> overrides the model's own figure when positive.</summary>
    public static int Resolve(LLMModelInfo model, JObject settings)
    {
        int overrideTokens = (settings?["parameters"] as JObject)?.Value<int?>("contextWindow") ?? 0;
        if (overrideTokens > 0)
        {
            return overrideTokens;
        }
        return model is { ContextLength: > 0 } ? model.ContextLength : 0;
    }

    /// <summary>A token count for <paramref name="text"/> from its length alone (about four characters a token), for the places that cannot
    /// ask a tokenizer. Counts the text as it would be sent, so it is a little low for code and a little high for prose.</summary>
    public static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);
}
