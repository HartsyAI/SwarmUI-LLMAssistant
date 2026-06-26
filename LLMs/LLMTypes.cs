using Newtonsoft.Json.Linq;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Standard chat role identifiers.
/// <para>These used to live in SwarmUI core (a branch that expanded the native LLM API). The
/// extension now owns them so it builds against the upstream skeleton — the actual backends live
/// in the removable <c>Backends/</c> pack behind <see cref="ILLMProvider"/>.</para></summary>
public static class LLMRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>A single inline media attachment on a chat message (eg an image for a vision model).</summary>
public class LLMMediaAttachment
{
    /// <summary><c>"url"</c> or <c>"base64"</c>.</summary>
    public string Type;
    /// <summary>The URL or raw base64 payload, per <see cref="Type"/>.</summary>
    public string Data;
    /// <summary>MIME type (eg <c>image/png</c>) — used when <see cref="Type"/> is <c>"base64"</c>.</summary>
    public string MediaType;
}

/// <summary>A single message in a conversation handed to a provider.</summary>
public class LLMMessage
{
    public string Role;
    public string Content;
    /// <summary>Optional inline media (images, etc.) for multimodal providers.</summary>
    public List<LLMMediaAttachment> Media;
}

/// <summary>Describes one model advertised by a provider. Providers populate as much metadata as
/// they can; missing fields stay at their sentinel defaults and are dropped from the wire JSON.</summary>
public class LLMModelInfo
{
    public string Id;
    public string Name;
    public string Provider;
    public int BackendId;
    public long SizeBytes = -1;
    public int ContextLength = -1;
    public string Family;
    public string Quantization;
    public bool IsLoaded;
    public Dictionary<string, string> Metadata = [];

    /// <summary>Serializes to the JSON shape the chat UI expects (snake_case keys; sentinel and
    /// empty fields omitted). Previously <c>LLMAPI.ModelInfoToJson</c> in core.</summary>
    public JObject ToJson()
    {
        JObject obj = new()
        {
            ["id"] = Id,
            ["name"] = Name ?? Id,
            ["provider"] = Provider ?? "",
            ["backend_id"] = BackendId,
            ["is_loaded"] = IsLoaded
        };
        if (SizeBytes >= 0)
        {
            obj["size_bytes"] = SizeBytes;
        }
        if (ContextLength >= 0)
        {
            obj["context_length"] = ContextLength;
        }
        if (!string.IsNullOrEmpty(Family))
        {
            obj["family"] = Family;
        }
        if (!string.IsNullOrEmpty(Quantization))
        {
            obj["quantization"] = Quantization;
        }
        if (Metadata is not null && Metadata.Count > 0)
        {
            JObject meta = [];
            foreach (KeyValuePair<string, string> kvp in Metadata)
            {
                meta[kvp.Key] = kvp.Value;
            }
            obj["metadata"] = meta;
        }
        return obj;
    }
}
