using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: generate an image via SwarmUI's native T2I engine, driven by the
/// caller's saved presets. The user's preset list is injected into the tool description per-call
/// (see <see cref="EnrichForUser"/>) so the LLM can pick a preset by name; if none is picked,
/// the user's configured default preset is used. With no presets at all, the tool returns an
/// instructive error rather than silently using engine defaults.</summary>
public class GenerateImageTool : ToolHandler
{
    public override string HandlerId => ToolConstants.GenerateImage;

    public const string ConfigDefaultPreset = "defaultPreset";

    public override JObject EnrichForUser(JObject toolDef, Session session)
    {
        if (toolDef is null || session?.User is null)
        {
            return toolDef;
        }
        List<T2IPreset> presets = UserPresetCache.GetPresets(session.User) ?? [];
        if (presets.Count == 0)
        {
            return toolDef;
        }
        JObject enriched = (JObject)toolDef.DeepClone();
        // Append a presets list to the description so the LLM can pick one by name.
        StringBuilder sb = new();
        sb.Append(enriched["description"]?.ToString() ?? "");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Available image presets for this user (pass the preset name in the `preset` argument to use one):");
        foreach (T2IPreset preset in presets)
        {
            sb.Append("- ").Append(preset.Title);
            if (!string.IsNullOrWhiteSpace(preset.Description))
            {
                sb.Append(" — ").Append(preset.Description);
            }
            sb.AppendLine();
        }
        string defaultPreset = ToolConfigService.GetConfig(HandlerId, session.User)[ConfigDefaultPreset]?.ToString();
        if (!string.IsNullOrEmpty(defaultPreset))
        {
            sb.AppendLine();
            sb.Append("If no `preset` is provided, the user's default preset (`").Append(defaultPreset).AppendLine("`) will be used.");
        }
        enriched["description"] = sb.ToString().TrimEnd();
        // Add a `preset` property with an enum so the LLM picks from the actual list.
        if (enriched["parameters"] is JObject parameters && parameters["properties"] is JObject properties)
        {
            JArray presetEnum = [];
            foreach (T2IPreset preset in presets)
            {
                presetEnum.Add(preset.Title);
            }
            properties["preset"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = presetEnum,
                ["description"] = "Name of the preset to use (must match one of the available presets shown in the description). Optional — falls back to the user's default if unset."
            };
        }
        return enriched;
    }

    public override async Task<JObject> Execute(JObject args, Session session, CancellationToken ct)
    {
        string prompt = args["prompt"]?.ToString();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new JObject { ["success"] = false, ["error"] = "prompt is required" };
        }
        string aspect = args["aspect"]?.ToString() ?? "square";
        (int width, int height) = aspect.ToLowerInvariant() switch
        {
            "portrait" => (832, 1216),
            "landscape" => (1216, 832),
            _ => (1024, 1024)
        };
        // Resolve which preset to apply: explicit arg → user's configured default → error.
        string requestedPreset = args["preset"]?.ToString();
        string defaultPreset = session?.User is null
            ? null
            : ToolConfigService.GetConfig(HandlerId, session.User)[ConfigDefaultPreset]?.ToString();
        string presetToUse = !string.IsNullOrWhiteSpace(requestedPreset) ? requestedPreset : defaultPreset;
        if (string.IsNullOrWhiteSpace(presetToUse))
        {
            List<T2IPreset> available = UserPresetCache.GetPresets(session?.User);
            if (available.Count == 0)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "No image presets are configured for this user. Open SwarmUI's Generate tab, configure your model and parameters, then click 'Save Preset' to create one. After that, set it as the default in LLM Assistant settings → Generate Image tool config."
                };
            }
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"No preset selected. Either pick one in the `preset` argument (available: {string.Join(", ", available.Select(p => p.Title))}) or set a default preset in LLM Assistant settings → Generate Image tool config."
            };
        }
        // Validate the preset actually exists for this user (avoids cryptic T2IAPI errors).
        if (session?.User is not null && UserPresetCache.GetPreset(session.User, presetToUse) is null)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"Preset '{presetToUse}' was not found. Available presets: {string.Join(", ", UserPresetCache.GetPresets(session.User).Select(p => p.Title))}."
            };
        }
        JObject rawInput = new()
        {
            ["prompt"] = prompt,
            ["width"] = width,
            ["height"] = height,
            ["presets"] = new JArray(presetToUse)
        };
        try
        {
            JObject result = await T2IAPI.GenerateText2Image(session, 1, rawInput);
            if (result.TryGetValue("error", out JToken err))
            {
                return new JObject { ["success"] = false, ["error"] = err.ToString() };
            }
            if (result.TryGetValue("images", out JToken imagesToken) && imagesToken is JArray images && images.Count > 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["imageUrl"] = images[0].ToString(),
                    ["prompt"] = prompt,
                    ["preset"] = presetToUse,
                    ["width"] = width,
                    ["height"] = height
                };
            }
            return new JObject { ["success"] = false, ["error"] = "No image was generated." };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] GenerateImageTool failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
