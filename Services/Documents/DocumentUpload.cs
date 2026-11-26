using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>A file as an API request carries it: base64 (or a <c>data:</c> URI), checked against the size limit before it is decoded.</summary>
public static class DocumentUpload
{
    /// <summary>The bytes of a request's <c>data</c> field. The bytes are null when it cannot be used, with the reason, and <c>TooLarge</c> says the reason is its size. Any type a
    /// data URI claims is ignored: what a file is is decided from its content and name.</summary>
    public static (byte[] Bytes, string Error, bool TooLarge) Decode(JToken data, int maxBytes)
    {
        string raw = data is JValue { Type: JTokenType.String } value ? value.Value<string>() : null;
        if (string.IsNullOrEmpty(raw))
        {
            return (null, "data (the file as base64) is required.", false);
        }
        int start = raw.StartsWith("data:", StringComparison.Ordinal) ? raw.IndexOf(',') + 1 : 0;
        string over = $"That file is too large (the limit is {maxBytes / (1024 * 1024)} MB).";
        // Refused on the encoded length before a buffer for it is made (base64 is 4 characters to 3 bytes; the slack covers line breaks in it).
        if ((long)(raw.Length - start) * 3 / 4 > maxBytes + maxBytes / 20L + 4)
        {
            return (null, over, true);
        }
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(raw[start..]);
        }
        catch (FormatException)
        {
            return (null, "data is not valid base64.", false);
        }
        return bytes.Length > maxBytes ? (null, over, true) : (bytes, null, false);
    }
}
