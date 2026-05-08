using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: generate an image via SwarmUI's native T2I engine.
///
/// <para>Supports three modes — and you can mix them:</para>
/// <list type="bullet">
/// <item><b>Preset</b>: pick one or more saved presets by name. Multiple presets stack in order
/// (later ones override earlier). The user's preset list (with model/steps/dimensions surfaced)
/// is injected into the tool description so the LLM can pick intelligently.</item>
/// <item><b>Raw params</b>: pass a <c>params</c> object with any T2I parameter (model, steps,
/// sampler, cfgscale, loras, etc.) — applied <i>on top of</i> any preset. Lets the LLM tweak a
/// preset for a single call ("use Anime preset but bump steps to 50") or do a one-off generation
/// without any preset at all.</item>
/// <item><b>Aspect</b>: shorthand for square/portrait/landscape dimensions. Only applied if
/// neither the chosen preset(s) nor <c>params</c> already set width/height.</item>
/// </list>
///
/// <para>Resolution order: presets applied in declared order → <c>params</c> applied last
/// (overrides). All param validation is delegated to T2IAPI — invalid keys become warnings and
/// are skipped. The handler never inspects the per-param semantics itself.</para></summary>
public class GenerateImageTool : ToolHandler
{
    public override string HandlerId => ToolConstants.GenerateImage;

    public const string ConfigDefaultPreset = "defaultPreset";

    /// <summary>Param-map keys that signal dimensions are explicitly set (so we shouldn't
    /// auto-apply <c>aspect</c>). Centralized so the same logic is consistent across modes.</summary>
    private static readonly HashSet<string> DimensionKeys = new(StringComparer.OrdinalIgnoreCase) { "width", "height" };

    public override JObject EnrichForUser(JObject toolDef, Session session, string assistantId = null)
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
        sb.AppendLine("Available image presets for this user (pass one or more preset names in the `preset` argument):");
        foreach (T2IPreset preset in presets)
        {
            sb.Append("- ").AppendLine(FormatPresetSummary(preset));
        }
        // Default preset comes from per-assistant config first (so each character can have its own
        // visual identity), falling back to the user's per-account default.
        string defaultPreset = ToolConfigService.GetConfig(HandlerId, session.User, assistantId)[ConfigDefaultPreset]?.ToString();
        if (!string.IsNullOrEmpty(defaultPreset))
        {
            sb.AppendLine();
            sb.Append("If no `preset` is provided, the default preset (`").Append(defaultPreset).AppendLine("`) will be used.");
        }
        enriched["description"] = sb.ToString().TrimEnd();
        // The `preset` property accepts either a single name or an array — stacking is supported.
        // We can't express "string|string[] with enum constraint" in JSON Schema cleanly, so we
        // describe the array shape in prose and keep the type loose.
        if (enriched["parameters"] is JObject parameters && parameters["properties"] is JObject properties)
        {
            JArray presetEnum = [];
            foreach (T2IPreset preset in presets)
            {
                presetEnum.Add(preset.Title);
            }
            properties["preset"] = new JObject
            {
                ["type"] = new JArray("string", "array"),
                ["enum"] = presetEnum, // applies when string-shaped; arrays bypass enum validation
                ["description"] = "Preset name(s) to apply. Pass a single string for one preset, or an array of strings to stack multiple (later ones override earlier). Each name must match a preset shown in the description. Optional — leave blank to use raw `params` only or to fall back to the user's default."
            };
        }
        return enriched;
    }

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        Session session = ctx.Session;
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "generate_image requires an authenticated user." };
        }
        string prompt = args["prompt"]?.ToString();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new JObject { ["success"] = false, ["error"] = "prompt is required" };
        }
        // ---- Resolve presets ----
        // Accept string OR string[]. If neither, fall back to the assistant/user default preset.
        List<string> presetNames = ParsePresetNames(args["preset"]);
        if (presetNames.Count == 0)
        {
            string defaultPreset = ToolConfigService.GetConfig(HandlerId, session.User, ctx.AssistantId)[ConfigDefaultPreset]?.ToString();
            if (!string.IsNullOrWhiteSpace(defaultPreset))
            {
                presetNames.Add(defaultPreset);
            }
        }
        // Validate every named preset exists. Reject the call cleanly if any is missing —
        // partial application would be confusing.
        foreach (string name in presetNames)
        {
            if (UserPresetCache.GetPreset(session.User, name) is null)
            {
                List<T2IPreset> available = UserPresetCache.GetPresets(session.User);
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = available.Count == 0
                        ? $"Preset '{name}' was not found and this user has no presets saved. Use the `params` argument to do a one-off generation instead, or save a preset on the Generate tab first."
                        : $"Preset '{name}' was not found. Available presets: {string.Join(", ", available.Select(p => p.Title))}."
                };
            }
        }
        // ---- Build the rawInput in precedence order: presets → params → prompt ----
        // Pre-merge presets ourselves so `params` can override (T2IAPI applies presets after raw
        // params natively, which is the wrong order for us — we want params to be the final word).
        JObject rawInput = [];
        List<string> presetsApplied = [];
        foreach (string name in presetNames)
        {
            T2IPreset preset = UserPresetCache.GetPreset(session.User, name);
            foreach (KeyValuePair<string, string> kv in preset.ParamMap)
            {
                rawInput[kv.Key] = kv.Value;
            }
            presetsApplied.Add(preset.Title);
        }
        // Apply user-supplied raw params last so they override the preset stack.
        if (args["params"] is JObject paramsArg)
        {
            foreach (JProperty prop in paramsArg.Properties())
            {
                rawInput[prop.Name] = prop.Value;
            }
        }
        // ---- Resolve init_image (image-edit / img2img workflows) ----
        // Accepts: data URI, Output/... URL, https:// URL, or raw base64. Decoded inline so the
        // T2I engine receives base64 like any other IMAGE-typed param. Pair this with a preset
        // whose model supports image input (Flux Kontext, OmniGen, SDXL inpaint, etc.) for edits
        // like "remove this person" or "change the style to oil painting".
        string initImageInput = args["initImage"]?.ToString();
        if (!string.IsNullOrWhiteSpace(initImageInput))
        {
            try
            {
                ImageInputResolver.ResolvedImage img = await ImageInputResolver.ResolveAsync(initImageInput, ctx.Ct);
                rawInput["init_image"] = img.DataUri;
            }
            catch (InvalidOperationException ex)
            {
                return new JObject { ["success"] = false, ["error"] = $"initImage: {ex.Message}" };
            }
            // Optional creativity override (denoising strength). Pass through as the canonical
            // T2I param id; T2IAPI clamps + validates.
            JToken creativity = args["initImageCreativity"];
            if (creativity is not null && creativity.Type != JTokenType.Null)
            {
                rawInput["init_image_creativity"] = creativity;
            }
        }
        // Aspect: only applied if no preset / no params already set dimensions. Otherwise the
        // preset's own dimensions win — what the user/preset author intended.
        bool hasDimensions = rawInput.ContainsKey("width") || rawInput.ContainsKey("height");
        string aspect = args["aspect"]?.ToString();
        if (!hasDimensions)
        {
            (int w, int h) = (aspect ?? "square").ToLowerInvariant() switch
            {
                "portrait" => (832, 1216),
                "landscape" => (1216, 832),
                _ => (1024, 1024)
            };
            rawInput["width"] = w;
            rawInput["height"] = h;
        }
        // Prompt always wins (overrides any prompt embedded in a preset).
        rawInput["prompt"] = prompt;
        // Sanity: we need at least a model. If none of the layers provided one, the T2I engine
        // will fail with an unhelpful error — surface a friendly one instead.
        if (!rawInput.ContainsKey("model") || string.IsNullOrWhiteSpace(rawInput["model"]?.ToString()))
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "No model selected. Either pick a preset that includes a model, or pass `params: { model: \"<model-name>\" }` for a raw generation."
            };
        }
        // Tag metadata so the resulting image's metadata sidecar records which presets were used.
        if (presetsApplied.Count > 0)
        {
            rawInput["extra_metadata"] = new JObject
            {
                ["presets_used"] = string.Join(", ", presetsApplied)
            };
        }
        try
        {
            JObject result = await T2IAPI.GenerateText2Image(session, 1, rawInput);
            if (result.TryGetValue("error", out JToken err))
            {
                return new JObject { ["success"] = false, ["error"] = err.ToString() };
            }
            if (result.TryGetValue("images", out JToken imagesToken) && imagesToken is JArray images && images.Count > 0)
            {
                JObject ret = new()
                {
                    ["success"] = true,
                    ["imageUrl"] = images[0].ToString(),
                    ["prompt"] = prompt,
                    ["width"] = rawInput["width"]?.ToObject<int>() ?? 0,
                    ["height"] = rawInput["height"]?.ToObject<int>() ?? 0
                };
                if (presetsApplied.Count > 0) ret["presets"] = new JArray(presetsApplied);
                return ret;
            }
            return new JObject { ["success"] = false, ["error"] = "No image was generated." };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] GenerateImageTool failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Parses the <c>preset</c> arg in either string or array form into a list of names.
    /// Empty strings are dropped. Returns empty list if the arg is missing or null.</summary>
    private static List<string> ParsePresetNames(JToken token)
    {
        List<string> result = [];
        if (token is null || token.Type == JTokenType.Null)
        {
            return result;
        }
        if (token is JArray arr)
        {
            foreach (JToken t in arr)
            {
                string name = t?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    result.Add(name);
                }
            }
        }
        else
        {
            string name = token.ToString()?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    /// <summary>Builds a one-line summary of a preset surfacing the key params the LLM needs to
    /// pick intelligently — the model, step count, sampler, default dimensions, and LoRAs.
    /// Without these, the LLM is just title-matching and can't tell "fast" from "high quality"
    /// or "anime" from "photoreal" unless the user wrote a description that says so.</summary>
    public static string FormatPresetSummary(T2IPreset preset)
    {
        if (preset is null)
        {
            return "";
        }
        StringBuilder sb = new();
        sb.Append(preset.Title);
        if (!string.IsNullOrWhiteSpace(preset.Description))
        {
            sb.Append(" — ").Append(preset.Description);
        }
        // Pull a small set of the most LLM-useful params out of the ParamMap. Keys that don't
        // exist are silently skipped — different presets capture different subsets of params.
        Dictionary<string, string> map = preset.ParamMap;
        if (map is not null && map.Count > 0)
        {
            List<string> facts = [];
            if (map.TryGetValue("model", out string model) && !string.IsNullOrWhiteSpace(model))
            {
                // Model paths can be long ("OfficialStableDiffusion/sd_xl_base_1.0"); trim to leaf.
                facts.Add($"model: {ShortenModelName(model)}");
            }
            if (map.TryGetValue("steps", out string steps) && !string.IsNullOrWhiteSpace(steps))
            {
                facts.Add($"{steps} steps");
            }
            if (map.TryGetValue("sampler", out string sampler) && !string.IsNullOrWhiteSpace(sampler))
            {
                facts.Add($"sampler: {sampler}");
            }
            if (map.TryGetValue("width", out string w) && map.TryGetValue("height", out string h)
                && !string.IsNullOrWhiteSpace(w) && !string.IsNullOrWhiteSpace(h))
            {
                facts.Add($"{w}×{h}");
            }
            if (map.TryGetValue("loras", out string loras) && !string.IsNullOrWhiteSpace(loras))
            {
                // LoRA values can be long. Show count + first one.
                string[] loraList = loras.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (loraList.Length == 1)
                {
                    facts.Add($"lora: {ShortenModelName(loraList[0])}");
                }
                else if (loraList.Length > 1)
                {
                    facts.Add($"{loraList.Length} loras");
                }
            }
            if (facts.Count > 0)
            {
                sb.Append(" (").Append(string.Join(", ", facts)).Append(')');
            }
        }
        return sb.ToString();
    }

    /// <summary>Returns the trailing component of a slash-separated model path. Keeps the LLM
    /// preset list scannable instead of full-of-folder-prefixes.</summary>
    private static string ShortenModelName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return "";
        }
        int idx = Math.Max(fullName.LastIndexOf('/'), fullName.LastIndexOf('\\'));
        return idx >= 0 ? fullName[(idx + 1)..] : fullName;
    }
}
