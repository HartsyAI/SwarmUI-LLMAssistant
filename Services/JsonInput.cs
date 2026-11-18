using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Reading values out of a request body.</summary>
public static class JsonInput
{
    /// <summary>A JSON object from a token that is an object or a string holding one (the browser sends either); null when it is neither.</summary>
    public static JObject ReadObject(JToken token)
    {
        if (token is JObject obj)
        {
            return obj;
        }
        if (token?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.ToString()))
        {
            try
            {
                return JObject.Parse(token.ToString());
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }
        return null;
    }
}
