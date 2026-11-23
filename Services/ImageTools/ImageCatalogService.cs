using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.ImageTools;

/// <summary>The four catalog listings (image models, LoRAs, wildcards, presets) as a call with arguments and a session: what the <c>list_*</c> tools run and what the API routes of the same names return,
/// so the two cannot drift. Everything is for the calling user only. All of them return <c>success</c> and, when something is wrong, an <c>error</c> the model can act on.</summary>
public static class ImageCatalogService
{
    /// <summary>Lists installed image models. Arguments: <c>query</c> (words in the name), <c>class</c> (a model class, id or family, by substring), <c>limit</c>.</summary>
    public static async Task<JObject> ListModelsAsync(Session session, JObject args)
    {
        if (ImageCatalogSource.Denied(session) is string denied)
        {
            return Failed(denied);
        }
        List<CatalogEntry> models = await ImageCatalogSource.ModelsAsync(session);
        return ImageCatalog.ModelsResult(models, Text(args, "query"), Text(args, "class"), ImageCatalog.ClampLimit(args?["limit"]), session.User.IsAllowedModel);
    }

    /// <summary>Lists installed LoRAs. Arguments: <c>query</c> (words in the name, title or tags), <c>baseModel</c> (a model class, id or family, by substring), <c>forModel</c> (an installed model's name: only
    /// the LoRAs that fit its family), <c>limit</c>.</summary>
    public static async Task<JObject> ListLorasAsync(Session session, JObject args)
    {
        if (ImageCatalogSource.Denied(session) is string denied)
        {
            return Failed(denied);
        }
        string baseClass = Text(args, "baseModel");
        string forModel = Text(args, "forModel");
        if (forModel.Length > 0)
        {
            List<CatalogEntry> models = await ImageCatalogSource.ModelsAsync(session);
            ModelResolution resolution = ModelNameResolver.Resolve(forModel, ImageCatalog.Names(models));
            if (!resolution.Found)
            {
                return Failed(NotFound(forModel, resolution, ImageCatalog.Names(models)));
            }
            CatalogEntry model = models.First(m => m.Name == resolution.Name);
            baseClass = model.CompatClass.Length > 0 ? model.CompatClass : model.Architecture;
            if (baseClass.Length == 0)
            {
                return Failed($"SwarmUI could not tell what kind of model '{model.Name}' is, so LoRAs cannot be matched to it. List them without `forModel`.");
            }
        }
        List<CatalogEntry> loras = await ImageCatalogSource.LorasAsync(session);
        JObject result = ImageCatalog.LorasResult(loras, Text(args, "query"), baseClass, ImageCatalog.ClampLimit(args?["limit"]), session.User.IsAllowedModel);
        if (forModel.Length > 0)
        {
            result["forBase"] = baseClass;
        }
        result["usage"] = "Use a LoRA with generate_image's params: { loras: \"<name>\", loraweights: \"<weight>\" }, and add its triggerWords to the prompt.";
        return result;
    }

    /// <summary>Lists wildcard names. Arguments: <c>query</c>, <c>limit</c>, <c>includeSamples</c> (default false: the contents of the user's files stay out of the result unless asked for).</summary>
    public static JObject ListWildcards(Session session, JObject args)
    {
        if (session?.User is null)
        {
            return Failed("Listing wildcards needs a signed-in user.");
        }
        bool samples = args?["includeSamples"]?.Type == JTokenType.Boolean && (bool)args["includeSamples"];
        return ImageCatalog.WildcardsResult(ImageCatalogSource.WildcardNames(session), Text(args, "query"), ImageCatalog.ClampLimit(args?["limit"]), samples, ImageCatalogSource.WildcardLines, session.User.IsAllowedModel);
    }

    /// <summary>Lists the user's presets. Arguments: <c>query</c> (words in the name or description), <c>limit</c>.</summary>
    public static JObject ListPresets(Session session, JObject args)
    {
        if (session?.User is null)
        {
            return Failed("Listing presets needs a signed-in user.");
        }
        return ImageCatalog.PresetsResult(ImageCatalogSource.Presets(session.User), Text(args, "query"), ImageCatalog.ClampLimit(args?["limit"]));
    }

    /// <summary>The error for a model name that is not installed: what was asked for, the closest installed names ("did you mean"), and where to look.</summary>
    public static string NotFound(string requested, ModelResolution resolution, IReadOnlyList<string> installed)
    {
        if (installed.Count == 0)
        {
            return $"Image model '{requested}' was not found. {ImageCatalog.ModelHint(installed)}";
        }
        if (resolution.Ambiguous)
        {
            return $"Image model '{requested}' fits several installed models: {string.Join(", ", resolution.Suggestions)}. Use the exact name of one.";
        }
        return resolution.Suggestions.Count > 0
            ? $"Image model '{requested}' is not installed. Did you mean: {string.Join(", ", resolution.Suggestions)}? Use the exact name. {ImageCatalog.ModelHint(installed)}"
            : $"Image model '{requested}' is not installed. {ImageCatalog.ModelHint(installed)}";
    }

    /// <summary>A failed result.</summary>
    public static JObject Failed(string error) => new() { ["success"] = false, ["error"] = error };

    /// <summary>A text argument, trimmed; empty when missing or not text.</summary>
    private static string Text(JObject args, string key) => args?[key]?.Type == JTokenType.String ? ((string)args[key]).Trim() : "";
}
