using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.ImageTools;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Hartsy.Extensions.LLMAssistant.T2I;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: generate an image via SwarmUI's native T2I engine. Supports stacking
/// one or more saved presets, raw <c>params</c> overrides applied on top, and an <c>aspect</c>
/// shorthand for dimensions when none are otherwise set. All param validation is delegated to
/// T2IAPI.</summary>
public class GenerateImageTool : ToolHandler
{
    public override string HandlerId => ToolConstants.GenerateImage;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Write;

    public const string ConfigDefaultPreset = "defaultPreset";

    /// <summary>A batch runs its images one after another on the backend, so a call gets far longer than the default minute.</summary>
    public override TimeSpan TimeoutFor(JObject toolDef) => TimeSpan.FromMinutes(10);

    public override JObject EnrichForUser(JObject toolDef, Session session, string assistantId = null)
    {
        if (toolDef is null || session?.User is null)
        {
            return toolDef;
        }
        JObject enriched = (JObject)toolDef.DeepClone();
        ImageToolSettings imageSettings = ImageToolSettings.ForUser(session.User);
        // The limits this user's calls run under, so the model asks for what will be made: the cap on `images`, and the default model (named only when one is set).
        if (enriched["parameters"]?["properties"]?["images"] is JObject imagesProp)
        {
            imagesProp["description"] = $"How many images to make from this prompt (default 1, at most {imageSettings.MaxImages}; larger requests are cut to {imageSettings.MaxImages}). Use it for several variations in one call instead of calling repeatedly.";
            imagesProp["maximum"] = imageSettings.MaxImages;
        }
        if (imageSettings.DefaultModel.Length > 0)
        {
            enriched["description"] = $"{enriched["description"]} The user's default image model is `{imageSettings.DefaultModel}`: it is used when no preset, `params.model` or `model` names one.";
        }
        List<T2IPreset> presets = UserPresetCache.GetPresets(session.User) ?? [];
        if (presets.Count == 0)
        {
            return enriched;
        }
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
        // Resolve presets: explicit args, else fall back to the assistant/user default.
        List<string> presetNames = ParsePresetNames(args["preset"]);
        if (presetNames.Count == 0)
        {
            string defaultPreset = ToolConfigService.GetConfig(HandlerId, session.User, ctx.AssistantId)[ConfigDefaultPreset]?.ToString();
            if (!string.IsNullOrWhiteSpace(defaultPreset))
            {
                presetNames.Add(defaultPreset);
            }
        }
        // Reject the call cleanly if any named preset is missing — partial application would be confusing.
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
        // Pre-merge presets ourselves so `params` can override (T2IAPI applies presets after raw
        // params natively, which is the wrong order for us — we want params to be the final word).
        JObject rawInput = [];
        List<string> presetsApplied = [];
        foreach (string name in presetNames)
        {
            T2IPreset preset = UserPresetCache.GetPreset(session.User, name);
            foreach (KeyValuePair<string, string> kv in preset.ParamMap)
            {
                // By what core takes the key to mean: a later layer replaces an earlier spelling of the same parameter ("Model" over "model") instead of sitting beside it.
                ImageParamKeys.Set(rawInput, kv.Key, kv.Value);
            }
            presetsApplied.Add(preset.Title);
        }
        string presetModel = ImageParamKeys.Get(rawInput, ImageParamKeys.Model)?.ToString()?.Trim() ?? "";
        string paramsModel = "";
        if (args["params"] is JObject paramsArg)
        {
            foreach (JProperty prop in paramsArg.Properties())
            {
                ImageParamKeys.Set(rawInput, prop.Name, prop.Value);
            }
            paramsModel = ImageParamKeys.Get(paramsArg, ImageParamKeys.Model)?.ToString()?.Trim() ?? "";
        }
        // init_image enables image-edit / img2img workflows; needs a preset/model that supports image input.
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
        rawInput["prompt"] = prompt;
        // How many images: the call's `images`, held to the server's limit.
        ImageToolSettings imageSettings = ImageToolSettings.ForUser(session.User);
        int requestedImages = 1;
        JToken imagesArg = args["images"];
        if (imagesArg is not null && imagesArg.Type != JTokenType.Null && !int.TryParse(imagesArg.ToString(), out requestedImages))
        {
            return new JObject { ["success"] = false, ["error"] = "images must be a whole number (1 or more)." };
        }
        int imageCount = imageSettings.ClampImages(requestedImages, out bool imagesCapped);
        // The cap holds for every spelling a preset or `params` may use (Images, "batch size", ...): they are removed, and images x batch size is kept within the limit.
        int batchSize = ImageParamKeys.EnforceImageCap(rawInput, imageCount, imageSettings.MaxImages);
        int totalImages = imageCount * batchSize;
        // With several images, one that fails must not throw away the ones that were made.
        if (totalImages > 1)
        {
            ImageParamKeys.Set(rawInput, "continueaftererrors", true);
        }
        // Choose the model: the `model` argument, then `params.model`, then the preset's, then the user's default image model. Anything but a preset's is checked against what is installed, so a wrong or
        // invented name gets the closest installed names back instead of a failure inside the engine, and no model at all gets a ready list to pick from.
        (JObject modelError, string modelNote) = await ChooseModelAsync(session, rawInput, args["model"]?.ToString()?.Trim() ?? "", paramsModel, presetModel, imageSettings);
        if (modelError is not null)
        {
            return modelError;
        }
        // `rewrite: off` stops the automatic prompt rewrite for this call; anything else follows the user's setting (and a generation's own parameter).
        if (string.Equals(args["rewrite"]?.ToString(), PromptAutoEnhance.ModeOff, StringComparison.OrdinalIgnoreCase) && PromptTagHandler.ParamAutoEnhance is not null)
        {
            rawInput[PromptTagHandler.ParamAutoEnhance.Type.ID] = PromptAutoEnhance.ModeOff;
        }
        // Tag metadata so the resulting image's metadata sidecar records which presets were used, and carry an id the prompt rewrite reports its outcome under.
        string reportId = Guid.NewGuid().ToString("N");
        JObject extraMetadata = new() { [PromptAutoEnhance.MetaReportId] = reportId };
        if (presetsApplied.Count > 0)
        {
            extraMetadata["presets_used"] = string.Join(", ", presetsApplied);
        }
        rawInput["extra_metadata"] = extraMetadata;
        try
        {
            BatchOutcome outcome = await ImageBatch.RunAsync(session, imageCount, rawInput, ctx.Ct);
            RewriteResult rewrite = RewriteReports.Take(reportId);
            if (outcome.Images.Count == 0)
            {
                if (outcome.Cancelled)
                {
                    return new JObject { ["success"] = false, ["error"] = "The image generation was stopped before an image was made.", ["cancelled"] = true };
                }
                // With one image the error core gave is returned as it is; with several, core keeps going after an error and only logs it, so the reason is in the SwarmUI log.
                return new JObject { ["success"] = false, ["error"] = outcome.Error ?? (totalImages > 1 ? "No image was generated (see the SwarmUI log for why)." : "No image was generated.") };
            }
            List<string> images = outcome.Images;
            JObject ret = new()
            {
                ["success"] = true,
                ["imageUrl"] = images[0],
                ["prompt"] = rewrite is { StyleId: not null } ? rewrite.Text : prompt,
                ["width"] = rawInput["width"]?.ToObject<int>() ?? 0,
                ["height"] = rawInput["height"]?.ToObject<int>() ?? 0,
                ["model"] = ImageParamKeys.Get(rawInput, ImageParamKeys.Model)?.ToString() ?? ""
            };
            if (images.Count > 1)
            {
                // The chat shows every image of a batch; imageUrl stays the first for anything that reads one.
                ret["imageUrls"] = new JArray(images.Take(ImageToolSettings.HardMaxImages));
                ret["images"] = images.Count;
            }
            if (images.Count < totalImages)
            {
                ret["note"] = outcome.Cancelled
                    ? $"The generation was stopped: {totalImages} images were asked for and {images.Count} were made."
                    : $"{totalImages} images were asked for and {images.Count} came back (the rest failed or were discarded).";
                if (outcome.Error is not null)
                {
                    ret["partialError"] = outcome.Error;
                }
            }
            if (imagesCapped)
            {
                ret["imagesCapped"] = $"{requestedImages} images were asked for; this server allows {imageSettings.MaxImages} per call, so {imageCount} were made. Call again for more.";
            }
            if (batchSize > 1)
            {
                ret["batchSize"] = batchSize;
            }
            if (modelNote is not null)
            {
                ret["modelNote"] = modelNote;
            }
            if (presetsApplied.Count > 0) ret["presets"] = new JArray(presetsApplied);
            if (rewrite is { StyleId: not null })
            {
                // The prompt above is what was generated from; the model also gets what it asked for, which style adapted it and anything that went wrong.
                ret["originalPrompt"] = prompt;
                ret["promptStyle"] = rewrite.StyleId;
                ret["rewriteStatus"] = rewrite.Rewritten ? "rewritten" : rewrite.FellBack ? "fallback" : "unchanged";
                if (rewrite.Warnings.Count > 0)
                {
                    ret["rewriteWarnings"] = new JArray(rewrite.Warnings);
                }
            }
            return ret;
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] GenerateImageTool failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Settles which model the generation uses and puts it in <paramref name="rawInput"/>. Returns the error to hand back (null when a model was chosen) and, when the name asked for
    /// was not the installed name (a different case, no extension), a note saying which one was used.</summary>
    private static async Task<(JObject Error, string Note)> ChooseModelAsync(Session session, JObject rawInput, string argModel, string paramsModel, string presetModel, ImageToolSettings settings)
    {
        (string requested, string source) = ImageParamKeys.PickModel(argModel, paramsModel, presetModel, settings.DefaultModel);
        // A preset's model is the user's own saved choice and goes to the engine as saved.
        if (source == "preset")
        {
            ImageParamKeys.Set(rawInput, ImageParamKeys.Model, requested);
            return (null, null);
        }
        bool canList = ImageCatalogSource.Denied(session) is null;
        List<CatalogEntry> everything = [];
        string listingError = null;
        if (canList)
        {
            try
            {
                // A name is checked against every model core lists (a video family's too: someone may mean it); the list shown in an error is the image models.
                everything = await ImageCatalogSource.ModelsAsync(session, includeVideo: true);
            }
            catch (CatalogUnavailableException ex)
            {
                listingError = ex.Message;
            }
        }
        List<string> installed = ImageCatalog.Names(everything);
        List<string> imageModels = ImageCatalog.Names(everything.Where(e => !e.Video));
        if (requested.Length == 0)
        {
            string hint = listingError is not null ? $"{listingError}." : ImageCatalog.ModelHint(imageModels);
            return (ImageCatalogService.Failed($"No image model selected. {hint} Pass one as `model`, or use a preset (list_presets). The user can also set a default image model in Settings."), null);
        }
        if (!canList || listingError is not null)
        {
            // Without the right to list models, or with the listing down, there is nothing to check against: the engine judges the name.
            ImageParamKeys.Set(rawInput, ImageParamKeys.Model, requested);
            return (null, null);
        }
        ModelResolution resolution = ModelNameResolver.Resolve(requested, installed);
        if (!resolution.Found)
        {
            string where = source == "default" ? " (this is the user's default image model: it may have been removed, so set another in Settings or pass `model`)" : "";
            return (ImageCatalogService.Failed(ImageCatalogService.NotFound(requested, resolution, imageModels) + where), null);
        }
        ImageParamKeys.Set(rawInput, ImageParamKeys.Model, resolution.Name);
        return (null, resolution.Name == requested ? null : $"'{requested}' was matched to the installed model '{resolution.Name}'.");
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
