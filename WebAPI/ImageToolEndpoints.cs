using Hartsy.Extensions.LLMAssistant.Services.ImageTools;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>The routes behind the image tools: the same listings the <c>list_image_models</c>, <c>list_loras</c>, <c>list_wildcards</c> and <c>list_presets</c> tools return (through the one
/// <see cref="ImageCatalogService"/>, so they cannot drift), and the image defaults (the default image model and the most images one call may make) with their Settings control. The listings need
/// <c>llm_chat</c> and, like core's own model list, see only what the calling user may; the defaults need <c>llm_settings</c>.</summary>
public static class ImageToolEndpoints
{
    /// <summary>Lists installed image models. Request: <c>{ query?, modelClass?, limit? }</c> (<c>modelClass</c> is a substring of the model type, e.g. <c>sdxl</c>; <c>limit</c> defaults to 25, at most 100).
    /// Returns <c>{success, installed, matched, shown, models: [{name, title?, architecture?, class?, resolution?, promptNotes?, triggerPhrase?, description?, tags?, loaded?}], more?, note}</c>.</summary>
    public static async Task<JObject> LLMAssistantListImageModels(Session session, string query = "", string modelClass = "", int limit = 0)
    {
        return await ImageCatalogService.ListModelsAsync(session, new JObject { ["query"] = query, ["class"] = modelClass, ["limit"] = limit > 0 ? limit : null });
    }

    /// <summary>Lists installed LoRAs. Request: <c>{ query?, baseModel?, forModel?, limit? }</c>; <c>forModel</c> is an installed model's name (only LoRAs of its family are returned).
    /// Returns <c>{success, installed, matched, shown, loras: [{name, title?, baseModel?, triggerWords?, defaultWeight?, tags?, description?}], forBase?, more?, usage, note}</c>.</summary>
    public static async Task<JObject> LLMAssistantListLoras(Session session, string query = "", string baseModel = "", string forModel = "", int limit = 0)
    {
        return await ImageCatalogService.ListLorasAsync(session, new JObject { ["query"] = query, ["baseModel"] = baseModel, ["forModel"] = forModel, ["limit"] = limit > 0 ? limit : null });
    }

    /// <summary>Lists wildcard names. Request: <c>{ query?, includeSamples?, limit? }</c>. Names only unless <c>includeSamples</c> is true (default false), which adds a few lines of the first wildcards.
    /// Returns <c>{success, total, matched, shown, wildcards: [{name, use, samples?}], more?}</c>.</summary>
    public static Task<JObject> LLMAssistantListWildcards(Session session, string query = "", bool includeSamples = false, int limit = 0)
    {
        return Task.FromResult(ImageCatalogService.ListWildcards(session, new JObject { ["query"] = query, ["includeSamples"] = includeSamples, ["limit"] = limit > 0 ? limit : null }));
    }

    /// <summary>Lists the caller's saved image presets. Request: <c>{ query?, limit? }</c>. Returns <c>{success, total, matched, shown, presets: [{name, description?, summary?}], more?, note}</c>.</summary>
    public static Task<JObject> LLMAssistantListPresets(Session session, string query = "", int limit = 0)
    {
        return Task.FromResult(ImageCatalogService.ListPresets(session, new JObject { ["query"] = query, ["limit"] = limit > 0 ? limit : null }));
    }

    /// <summary>The image defaults the caller's <c>generate_image</c> calls run under. Returns <c>{success, defaultModel, personalDefaultModel, sharedDefaultModel, maxImages, defaultMaxImages, hardMaxImages,
    /// canWriteShared}</c>: <c>defaultModel</c> is the effective one (the caller's own, else the shared one), <c>maxImages</c> the server limit on images per call.</summary>
    public static Task<JObject> LLMAssistantGetImageDefaults(Session session) => Task.FromResult(Describe(session.User));

    /// <summary>Changes the image defaults. Request: <c>{ defaultModel?, maxImages?, scope? }</c>. <c>defaultModel</c> is an installed model's name (a near match is accepted, a name that matches nothing is
    /// refused with suggestions; an empty string clears it); it is saved for the caller, or for every user when <c>scope</c> is <c>shared</c>. <c>maxImages</c> (1 to 16) is the server's limit on images per
    /// call. Both shared writes need <c>llm_shared_write</c>. Returns the same as <c>LLMAssistantGetImageDefaults</c>.</summary>
    public static async Task<JObject> LLMAssistantSetImageDefaults(Session session, JObject rawInput)
    {
        User user = session.User;
        bool shared = string.Equals(rawInput?["scope"]?.ToString(), Services.SettingsService.ScopeShared, StringComparison.OrdinalIgnoreCase);
        bool canWriteShared = user?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false;
        string model = rawInput?["defaultModel"]?.Type == JTokenType.String ? ((string)rawInput["defaultModel"]).Trim() : null;
        int? maxImages = null;
        if (rawInput?["maxImages"] is { Type: not JTokenType.Null } maxToken)
        {
            if (!int.TryParse(maxToken.ToString(), out int max) || max < 1 || max > ImageToolSettings.HardMaxImages)
            {
                return ImageCatalogService.Failed($"maxImages must be a whole number from 1 to {ImageToolSettings.HardMaxImages}.");
            }
            maxImages = max;
        }
        if ((shared || maxImages is not null) && !canWriteShared)
        {
            return ImageCatalogService.Failed("The shared default model and the limit on images per call need the llm_shared_write permission.");
        }
        if (model is null && maxImages is null)
        {
            return ImageCatalogService.Failed("Pass defaultModel and/or maxImages.");
        }
        if (!string.IsNullOrEmpty(model) && ImageCatalogSource.Denied(session) is null)
        {
            List<string> installed = ImageCatalog.Names(await ImageCatalogSource.ModelsAsync(session));
            ModelResolution resolution = ModelNameResolver.Resolve(model, installed);
            if (!resolution.Found)
            {
                return ImageCatalogService.Failed(ImageCatalogService.NotFound(model, resolution, installed));
            }
            model = resolution.Name;
        }
        ImageToolSettings.Save(user, shared, model, maxImages);
        return Describe(user);
    }

    /// <summary>The current defaults, in the shape both routes return.</summary>
    private static JObject Describe(User user)
    {
        ImageToolSettings effective = ImageToolSettings.ForUser(user);
        ImageToolSettings sharedOnly = ImageToolSettings.ForUser(null);
        return new JObject
        {
            ["success"] = true,
            ["defaultModel"] = effective.DefaultModel,
            ["personalDefaultModel"] = ImageToolSettings.PersonalModel(user),
            ["sharedDefaultModel"] = sharedOnly.DefaultModel,
            ["maxImages"] = effective.MaxImages,
            ["defaultMaxImages"] = ImageToolSettings.DefaultMaxImages,
            ["hardMaxImages"] = ImageToolSettings.HardMaxImages,
            ["canWriteShared"] = user?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false,
        };
    }
}
