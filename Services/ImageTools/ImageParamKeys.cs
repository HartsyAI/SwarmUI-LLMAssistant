using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.LLMAssistant.Services.ImageTools;

/// <summary>Works on the raw parameter map a generation is built from by what each key <i>means</i> to core, not how it is spelled. Core cleans every key (<c>T2IParamTypes.CleanTypeName</c>: case and
/// punctuation do not matter, so <c>Images</c>, <c>images</c> and <c>"Batch Size"</c> are the same parameters) and follows its own aliases, and applies keys in order with the last one winning. A check that looks
/// at the literal key <c>images</c> alone is therefore bypassed by <c>params: { "Images": 5000 }</c>. Everything here goes through <see cref="CoreId"/>, which tests replace with a fake.</summary>
public static class ImageParamKeys
{
    /// <summary>The canonical ids (core's <c>T2IParamType.ID</c>) of the parameters the cap on images is about.</summary>
    public const string Images = "images", BatchSize = "batchsize", Model = "model";

    /// <summary>The keys of a raw generation request that are not parameters: core applies <c>presets</c> after every other key (so a preset named there would override the forced image count and the chosen
    /// model), and the other two carry the session and the metadata. A caller's <c>params</c> or a preset's own map may not set them.</summary>
    public static bool IsReserved(string key) => key is not null && (key.Equals("presets", StringComparison.OrdinalIgnoreCase) || key.Equals("session_id", StringComparison.OrdinalIgnoreCase) || key.Equals("extra_metadata", StringComparison.OrdinalIgnoreCase));

    /// <summary>Lays one layer of parameters (a preset's map, a call's <c>params</c>) over <paramref name="raw"/>, each key replacing an earlier spelling of the same parameter, and never a <see cref="IsReserved"/> key.</summary>
    public static void MergeLayer(JObject raw, IEnumerable<KeyValuePair<string, JToken>> layer, Func<string, string> idOf = null)
    {
        foreach (KeyValuePair<string, JToken> pair in layer)
        {
            if (!IsReserved(pair.Key))
            {
                Set(raw, pair.Key, pair.Value, idOf);
            }
        }
    }

    /// <summary>What core calls this key: the id of the parameter it names, following core's aliases; the cleaned key itself when core does not know it (or cannot be asked).</summary>
    public static string CoreId(string key)
    {
        string clean = T2IParamTypes.CleanTypeName(key ?? "");
        try
        {
            T2IParamType type = T2IParamTypes.GetType(key ?? "", null);
            if (type is not null)
            {
                return type.ID;
            }
        }
        catch (Exception)
        {
            // A provider of special parameter names that wants a request to look at: the cleaned name is the fallback.
        }
        return clean;
    }

    /// <summary>Which model a generation asks for, and where the name came from: the call's <c>model</c> argument, then <c>params.model</c>, then the model of a preset, then the user's default image model
    /// (source <c>model</c>, <c>params</c>, <c>preset</c>, <c>default</c>); empty for none.</summary>
    public static (string Requested, string Source) PickModel(string argModel, string paramsModel, string presetModel, string defaultModel)
    {
        return !string.IsNullOrWhiteSpace(argModel) ? (argModel.Trim(), "model")
            : !string.IsNullOrWhiteSpace(paramsModel) ? (paramsModel.Trim(), "params")
            : !string.IsNullOrWhiteSpace(presetModel) ? (presetModel.Trim(), "preset")
            : !string.IsNullOrWhiteSpace(defaultModel) ? (defaultModel.Trim(), "default")
            : ("", "");
    }

    /// <summary>The value of the parameter <paramref name="id"/>, from whichever key spelling holds it (the last one, as core applies them), or null.</summary>
    public static JToken Get(JObject raw, string id, Func<string, string> idOf = null)
    {
        idOf ??= CoreId;
        JToken found = null;
        foreach (JProperty property in raw.Properties())
        {
            if (idOf(property.Name) == id)
            {
                found = property.Value;
            }
        }
        return found;
    }

    /// <summary>Removes every key that is the parameter <paramref name="id"/>, however spelled.</summary>
    public static void Remove(JObject raw, string id, Func<string, string> idOf = null)
    {
        idOf ??= CoreId;
        foreach (string key in raw.Properties().Select(p => p.Name).Where(k => idOf(k) == id).ToList())
        {
            raw.Remove(key);
        }
    }

    /// <summary>Sets a parameter so that it is the only key for it: a later layer (a call's <c>params</c> over a preset) replaces an earlier spelling of the same parameter instead of sitting beside it.</summary>
    public static void Set(JObject raw, string key, JToken value, Func<string, string> idOf = null)
    {
        idOf ??= CoreId;
        Remove(raw, idOf(key), idOf);
        raw[key] = value;
    }

    /// <summary>Holds a generation to <paramref name="cap"/> images: removes every spelling of <c>images</c> and <c>batchsize</c> that a preset or <c>params</c> brought, then sets <c>images</c> to
    /// <paramref name="imageCount"/> and the batch size to what they asked for, but no more than keeps <c>images x batch</c> within <paramref name="cap"/> (a batch of 1 is not written). Returns the batch size.</summary>
    public static int EnforceImageCap(JObject raw, int imageCount, int cap, Func<string, string> idOf = null) => EnforceImageCapDetailed(raw, imageCount, cap, idOf).Batch;

    /// <summary>As <see cref="EnforceImageCap"/>, and also says what batch size was asked for before it was held (1 when none was).</summary>
    public static (int Batch, int Requested) EnforceImageCapDetailed(JObject raw, int imageCount, int cap, Func<string, string> idOf = null)
    {
        idOf ??= CoreId;
        int batch = 1;
        JToken asked = Get(raw, BatchSize, idOf);
        if (asked is not null && int.TryParse(asked.ToString(), out int parsed) && parsed > 1)
        {
            batch = parsed;
        }
        Remove(raw, Images, idOf);
        Remove(raw, BatchSize, idOf);
        int requested = batch;
        batch = Math.Clamp(batch, 1, Math.Max(1, cap / Math.Max(1, imageCount)));
        raw[Images] = imageCount;
        if (batch > 1)
        {
            raw[BatchSize] = batch;
        }
        return (batch, requested);
    }
}
