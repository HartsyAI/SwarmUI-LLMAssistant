using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>A one-shot reply with the model's reasoning apart from its answer, and why it stopped: <see cref="Stop"/> for a natural end,
/// <see cref="Length"/> when the token budget ran out (the answer may then be empty while reasoning holds what was written). A failed
/// generation is an exception, never a result.</summary>
/// <param name="Response">The answer, without reasoning.</param>
/// <param name="Reasoning">The reasoning the model wrote before (or around) its answer; empty when it wrote none.</param>
/// <param name="FinishReason"><see cref="Stop"/> or <see cref="Length"/>.</param>
public sealed record LLMGenerateResult(string Response, string Reasoning, string FinishReason)
{
    /// <summary>The model ended its reply.</summary>
    public const string Stop = "stop";

    /// <summary>The token budget ran out first.</summary>
    public const string Length = "length";

    /// <summary>The finish reason of a request that failed; only ever sent with <c>success: false</c>.</summary>
    public const string Error = "error";

    /// <summary>The result for a provider that returns only text: the whole text is the answer, and it is taken to have ended naturally.</summary>
    public static LLMGenerateResult FromText(string text) => new(text ?? "", "", Stop);

    /// <summary>The compact JSON the one-shot cache stores.</summary>
    public string ToJson() => new JObject { ["response"] = Response, ["reasoning"] = Reasoning, ["finish_reason"] = FinishReason }
        .ToString(Formatting.None);

    /// <summary>Reads <see cref="ToJson"/>'s form back; anything else (a plain-text entry) is a whole answer that ended naturally.</summary>
    public static LLMGenerateResult FromJson(string json)
    {
        if (string.IsNullOrEmpty(json) || json[0] != '{')
        {
            return FromText(json);
        }
        try
        {
            JObject obj = JObject.Parse(json);
            return new(obj.Value<string>("response") ?? "", obj.Value<string>("reasoning") ?? "", obj.Value<string>("finish_reason") ?? Stop);
        }
        catch (JsonReaderException)
        {
            return FromText(json);
        }
    }
}
