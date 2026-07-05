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
    /// <summary>The message's chat role (see <see cref="LLMRoles"/>).</summary>
    public string Role;
    /// <summary>The message text.</summary>
    public string Content;
    /// <summary>Optional inline media (images, etc.) for multimodal providers.</summary>
    public List<LLMMediaAttachment> Media;
}

/// <summary>Describes one model advertised by a provider. Providers populate as much metadata as
/// they can; missing fields stay at their sentinel defaults and are dropped from the wire JSON.</summary>
public class LLMModelInfo
{
    /// <summary>Stable model id used to select this model in requests.</summary>
    public string Id;
    /// <summary>Human-facing model name, falls back to <see cref="Id"/> if unset.</summary>
    public string Name;
    /// <summary>Owning provider id (eg <c>"hartsy-local"</c>).</summary>
    public string Provider;
    /// <summary>The backend instance (GPU/device) this model is advertised on.</summary>
    public int BackendId;
    /// <summary>On-disk model size in bytes, or -1 if unknown.</summary>
    public long SizeBytes = -1;
    /// <summary>Max context length in tokens, or -1 if unknown.</summary>
    public int ContextLength = -1;
    /// <summary>Model family (eg <c>"qwen2"</c>), or null if unknown.</summary>
    public string Family;
    /// <summary>Quantization scheme (eg <c>"Q4_K_M"</c>), or null if unknown.</summary>
    public string Quantization;
    /// <summary>Whether the model is currently resident (loaded) on its backend.</summary>
    public bool IsLoaded;
    /// <summary>Free-form provider-specific metadata (eg device, vision capability) surfaced to the UI.</summary>
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
