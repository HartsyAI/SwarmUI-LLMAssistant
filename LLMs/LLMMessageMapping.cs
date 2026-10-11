using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>Shared shape rules for tool turns: a stored tool call's arguments and result as the text or JSON a provider expects.</summary>
public static class LLMMessageMapping
{
    /// <summary>A stored call's arguments as a JSON object; a string holding JSON is parsed, anything else is an empty object.</summary>
    public static JObject ArgumentsObject(JToken arguments)
    {
        if (arguments is JObject obj) return obj;
        if (arguments is JValue value && value.Type == JTokenType.String)
        {
            try
            {
                return JObject.Parse(value.ToString());
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }
        return new JObject();
    }

    /// <summary>A stored call's arguments as the JSON text OpenAI-shaped APIs expect.</summary>
    public static string ArgumentsText(JToken arguments)
    {
        if (arguments is JValue value && value.Type == JTokenType.String)
        {
            return string.IsNullOrWhiteSpace(value.ToString()) ? "{}" : value.ToString();
        }
        return arguments is null ? "{}" : arguments.ToString(Formatting.None);
    }
}
