using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptStyles;

/// <summary>What a model file says about how it is prompted, from flat <c>modelspec.prompt_*</c> keys in its safetensors header (the ModelSpec convention core already reads for other fields). Any tool that
/// makes the file may write them; the extension only reads, never depends on who wrote them. Every field is optional: unset means "not stated", never "no". Values outside the known vocabulary are dropped,
/// so a file cannot make a style do something unexpected.</summary>
/// <param name="Format"><c>modelspec.prompt_format</c>: <see cref="PromptStyle.FormatText"/>, tags or json, or null.</param>
/// <param name="Negative"><c>modelspec.prompt_negative</c>: none, separate or inline, or null.</param>
/// <param name="Supports"><c>modelspec.prompt_supports</c>: a comma list from <see cref="PromptStyle.KnownSupports"/>, or null when the key is absent (an empty list means none).</param>
/// <param name="Boxes"><c>modelspec.prompt_boxes</c>: axis order and scale such as <c>yxyx-1000</c>, or null.</param>
/// <param name="Encoders"><c>modelspec.prompt_encoder</c>: a comma list from clip, t5, llm, vlm, or null.</param>
public sealed record ModelPromptFacts(string Format, string Negative, string[] Supports, string Boxes, string[] Encoders)
{
    /// <summary>The header keys this reads.</summary>
    public const string KeyFormat = "modelspec.prompt_format", KeyNegative = "modelspec.prompt_negative", KeySupports = "modelspec.prompt_supports", KeyBoxes = "modelspec.prompt_boxes",
        KeyEncoder = "modelspec.prompt_encoder";

    /// <summary>The text encoder kinds a file may name.</summary>
    public static readonly string[] KnownEncoders = ["clip", "t5", "llm", "vlm"];

    /// <summary>The longest value read from one key.</summary>
    public const int MaxValueChars = 200;

    /// <summary>Reads the facts from a header's <c>__metadata__</c> map, or returns null when it has none of the keys (or is null).</summary>
    public static ModelPromptFacts FromMetadata(JObject metadata)
    {
        if (metadata is null)
        {
            return null;
        }
        string format = Pick(Get(metadata, KeyFormat), PromptStyle.FormatText, PromptStyle.FormatTags, PromptStyle.FormatJson);
        string negative = Pick(Get(metadata, KeyNegative), PromptStyle.NegativeNone, PromptStyle.NegativeSeparate, PromptStyle.NegativeInline);
        string[] supports = List(Get(metadata, KeySupports), PromptStyle.KnownSupports);
        string[] encoders = List(Get(metadata, KeyEncoder), KnownEncoders);
        string boxes = ParseBoxes(Get(metadata, KeyBoxes));
        if (format is null && negative is null && supports is null && boxes is null && encoders is null)
        {
            return null;
        }
        return new ModelPromptFacts(format, negative, supports, boxes, encoders);
    }

    /// <summary>Whether the model's header says it takes <paramref name="capability"/> (an entry of <see cref="PromptStyle.KnownSupports"/>). False when the header does not say.</summary>
    public bool Has(string capability) => Supports is not null && Supports.Contains(capability, StringComparer.OrdinalIgnoreCase);

    /// <summary>A key's string value, trimmed and length-capped, or null when missing or not a string.</summary>
    private static string Get(JObject metadata, string key)
    {
        if (!metadata.TryGetValue(key, out JToken token) || token.Type != JTokenType.String)
        {
            return null;
        }
        string value = token.Value<string>()?.Trim();
        return value is null || value.Length > MaxValueChars ? null : value;
    }

    /// <summary>The value when it is one of <paramref name="allowed"/> (case-insensitive), lowercased; else null.</summary>
    private static string Pick(string value, params string[] allowed)
    {
        string lower = value?.ToLowerInvariant();
        return lower is not null && allowed.Contains(lower) ? lower : null;
    }

    /// <summary>The comma-separated entries of <paramref name="value"/> that are in <paramref name="allowed"/>, in order without repeats; null when the key was absent. Unknown entries are dropped.</summary>
    private static string[] List(string value, string[] allowed)
    {
        if (value is null)
        {
            return null;
        }
        return [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => v.ToLowerInvariant()).Where(allowed.Contains).Distinct()];
    }

    /// <summary>A box form <c>yxyx-1000</c> or <c>xyxy-1</c> (axis order, a dash, a positive whole scale), lowercased; else null.</summary>
    private static string ParseBoxes(string value)
    {
        string lower = value?.ToLowerInvariant();
        if (lower is null)
        {
            return null;
        }
        int dash = lower.IndexOf('-');
        if (dash <= 0 || lower[..dash] is not ("yxyx" or "xyxy") || !int.TryParse(lower[(dash + 1)..], out int scale) || scale <= 0)
        {
            return null;
        }
        return lower;
    }
}
