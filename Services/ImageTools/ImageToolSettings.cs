using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.ImageTools;

/// <summary>The settings of the image tools, from the <c>imageTools</c> object of the settings.
/// <list type="bullet">
/// <item><c>defaultModel</c>: the image model <c>generate_image</c> uses when nothing else (an argument, <c>params</c>, a preset) names one. A user's own value wins over the shared one an admin sets.</item>
/// <item><c>maxImages</c>: the most images one <c>generate_image</c> call may make. It is a server limit, so only the shared layer is read; a user cannot raise it for themselves.</item>
/// </list>Anything missing or out of range takes the default.</summary>
/// <param name="DefaultModel">The default image model name, or empty for none.</param>
/// <param name="MaxImages">The most images one call may make, from 1 to <see cref="HardMaxImages"/>.</param>
public sealed record ImageToolSettings(string DefaultModel, int MaxImages)
{
    /// <summary>The settings key.</summary>
    public const string Key = "imageTools";

    /// <summary>The default for <see cref="MaxImages"/>.</summary>
    public const int DefaultMaxImages = 4;

    /// <summary>The largest value <see cref="MaxImages"/> can be set to: one chat call should never queue a whole afternoon of generation.</summary>
    public const int HardMaxImages = 16;

    /// <summary>The defaults: no default model, four images.</summary>
    public static ImageToolSettings Default => new("", DefaultMaxImages);

    /// <summary>Reads the settings for <paramref name="user"/> from the shared layer and the user's own.</summary>
    public static ImageToolSettings ForUser(User user)
    {
        JObject shared = SettingsService.GetSettings()[Key] as JObject;
        JObject personal = user is null ? null : SettingsService.GetUserSettings(user)[Key] as JObject;
        ImageToolSettings settings = From(shared, personal);
        // A default model the user may not use is not theirs to see or be given: it is left out of tool descriptions and of what the routes return.
        return user is not null && settings.DefaultModel.Length > 0 && !user.IsAllowedModel(settings.DefaultModel) ? settings with { DefaultModel = "" } : settings;
    }

    /// <summary>The shared default model, or empty when there is none or <paramref name="user"/> may not use it.</summary>
    public static string SharedModelFor(User user)
    {
        string name = ReadModel(SettingsService.GetSettings()[Key] as JObject) ?? "";
        return name.Length > 0 && (user is null || user.IsAllowedModel(name)) ? name : "";
    }

    /// <summary>The default model the user saved in their own layer (empty when none).</summary>
    public static string PersonalModel(User user) => user is null ? "" : ReadModel(SettingsService.GetUserSettings(user)[Key] as JObject) ?? "";

    /// <summary>Combines the two layers' <c>imageTools</c> objects (either may be null): the model from the user's if it has one, else the shared one; the limit from the shared one only.</summary>
    public static ImageToolSettings From(JObject shared, JObject personal)
    {
        string model = ReadModel(personal) ?? ReadModel(shared) ?? "";
        int max = shared?["maxImages"]?.Type == JTokenType.Integer && long.TryParse(shared["maxImages"].ToString(), out long value)
            ? (int)Math.Clamp(value, 1, HardMaxImages)
            : DefaultMaxImages;
        return new ImageToolSettings(model, max);
    }

    /// <summary>The model name a layer holds: null when the layer has none (missing, not text or empty), so the next layer is read.</summary>
    private static string ReadModel(JObject layer)
    {
        string name = layer?["defaultModel"]?.Type == JTokenType.String ? ((string)layer["defaultModel"]).Trim() : "";
        return name.Length == 0 ? null : name;
    }

    /// <summary>Saves a change to the image tool settings. A model goes to the user's own layer, or the shared one when <paramref name="shared"/>; the limit is always shared (a server limit), so the caller must have
    /// checked the right to write it. Either may be null to leave it as it is; an empty <paramref name="defaultModel"/> clears the model of that layer.</summary>
    public static void Save(User user, bool shared, string defaultModel, int? maxImages)
    {
        JObject sharedPatch = [];
        JObject personalPatch = [];
        if (defaultModel is not null)
        {
            (shared ? sharedPatch : personalPatch)["defaultModel"] = defaultModel.Trim();
        }
        if (maxImages is not null)
        {
            sharedPatch["maxImages"] = Math.Clamp(maxImages.Value, 1, HardMaxImages);
        }
        if (sharedPatch.Count > 0)
        {
            lock (SettingsService.SettingsLock)
            {
                JObject current = SettingsService.GetSettings();
                current.Merge(new JObject { [Key] = sharedPatch });
                SettingsService.ReplaceSharedSettings(current);
            }
            AuditLogService.RecordSharedWrite("update", Key, user, new JObject { ["keys"] = string.Join(",", sharedPatch.Properties().Select(p => p.Name)) });
        }
        if (personalPatch.Count > 0)
        {
            SettingsService.PatchUserSettings(user, new JObject { [Key] = personalPatch });
        }
    }

    /// <summary>The number of images a call asked for, held to the limit: at least one, at most <see cref="MaxImages"/>. <paramref name="capped"/> says whether the request was cut.</summary>
    public int ClampImages(int requested, out bool capped)
    {
        int count = Math.Max(1, requested);
        capped = count > MaxImages;
        return Math.Min(count, MaxImages);
    }
}
