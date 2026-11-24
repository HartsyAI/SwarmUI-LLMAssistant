using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.LLMAssistant.Services.ImageTools;

/// <summary>A listing of models could not be read (as opposed to being empty).</summary>
public sealed class CatalogUnavailableException(string message) : Exception(message);

/// <summary>Reads what SwarmUI has for the calling user and hands it to <see cref="ImageCatalog"/>: the installed image models and LoRAs through core's own <c>ModelsAPI.ListModels</c> (so a user sees what the
/// Generate tab shows them: their model restrictions, and models other extensions offer), the wildcard files, and the user's presets. The tools and the API routes both read through here.</summary>
public static class ImageCatalogSource
{
    /// <summary>Why a user cannot see the catalog, or null when they can. Listing models needs core's own model-access permission.</summary>
    public static string Denied(Session session)
    {
        if (session?.User is null)
        {
            return "Listing image models needs a signed-in user.";
        }
        return session.User.HasPermission(Permissions.FundamentalModelAccess) ? null : "The user does not have permission to see models.";
    }

    /// <summary>How long a user's model list is reused: a name check, a LoRA match and a listing in the same few seconds read core once.</summary>
    private static readonly UserTtlCache<List<CatalogEntry>> ModelCache = new(TimeSpan.FromSeconds(12));

    /// <summary>Whether the cache has been hooked to core's model refresh (done on first use, so nothing touches core until a call needs it).</summary>
    private static bool _hooked;

    /// <summary>The installed text-to-image models the user may use.</summary>
    public static async Task<List<CatalogEntry>> ModelsAsync(Session session, bool includeVideo = false)
    {
        if (!_hooked)
        {
            _hooked = true;
            Program.ModelRefreshEvent += ModelCache.Clear;
        }
        string userId = session?.User?.UserID;
        if (!ModelCache.TryGet(userId, out List<CatalogEntry> marked))
        {
            List<CatalogEntry> all = await ReadAsync(session, "Stable-Diffusion");
            // Models of a video or audio family (core's own flags on the compatibility class), and upscalers and segmenters core lists with the checkpoints, are left out unless asked for: this is the list of text-to-image models.
            marked = [.. all.Select(e => e with { Video = ImageCatalog.IsNonImageFamily(e.CompatClass, e.Architecture, IsVideoFamily(e.CompatClass)) })];
            ModelCache.Set(userId, marked);
        }
        return includeVideo ? marked : [.. marked.Where(e => !e.Video)];
    }

    /// <summary>Whether core marks the compatibility class as one that makes video or audio.</summary>
    private static bool IsVideoFamily(string compatClass)
    {
        return !string.IsNullOrEmpty(compatClass) && T2IModelClassSorter.CompatClasses.TryGetValue(compatClass.ToLowerInvariant(), out T2IModelCompatClass family) && (family.IsText2Video || family.IsImage2Video || family.IsAudioModel);
    }

    /// <summary>The installed LoRAs the user may use.</summary>
    public static Task<List<CatalogEntry>> LorasAsync(Session session) => ReadAsync(session, "LoRA");

    /// <summary>The names of the wildcard files the user may use (core hides wildcards by the same model-access rule as models).</summary>
    public static List<string> WildcardNames(Session session)
    {
        try
        {
            return [.. WildcardsHelper.ListFiles.Where(n => session.User.IsAllowedModel(n))];
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Could not list wildcards: {ex.Message}");
            return [];
        }
    }

    /// <summary>The non-empty, non-comment lines of a wildcard (what a wildcard picks from), or empty when it is unknown.</summary>
    public static IReadOnlyList<string> WildcardLines(string name)
    {
        try
        {
            return WildcardsHelper.GetWildcard(name)?.Options ?? [];
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Could not read wildcard '{name}': {ex.Message}");
            return [];
        }
    }

    /// <summary>The user's saved presets, as the catalog lists them.</summary>
    public static List<PresetEntry> Presets(User user)
    {
        // A preset saved on the Generate tab does not tell the cache, so a listing (which is asked for rarely, unlike the tool definition) reads fresh.
        UserPresetCache.Invalidate(user);
        return [.. (UserPresetCache.GetPresets(user) ?? []).Select(p => new PresetEntry(p.Title, p.Description, Tools.BuiltIn.GenerateImageTool.FormatPresetSummary(p)))];
    }

    /// <summary>Lists one kind of model through core. A failure is logged and thrown as <see cref="CatalogUnavailableException"/>, so a listing that failed is never mistaken for one that is empty.</summary>
    private static async Task<List<CatalogEntry>> ReadAsync(Session session, string subtype)
    {
        try
        {
            JObject reply = await ModelsAPI.ListModels(session, "", 20, subtype);
            if (reply.ContainsKey("error"))
            {
                Logs.Warning($"[LLMAssistant] Listing {subtype} models failed: {reply["error"]}");
                throw new CatalogUnavailableException($"SwarmUI could not list the {subtype} models: {reply["error"]}");
            }
            return [.. (reply["files"] as JArray ?? []).OfType<JObject>().Select(CatalogEntry.FromNet).Where(e => e.Name.Length > 0)];
        }
        catch (CatalogUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Listing {subtype} models failed: {ex.Message}");
            throw new CatalogUnavailableException($"SwarmUI could not list the {subtype} models: {ex.Message}");
        }
    }
}
