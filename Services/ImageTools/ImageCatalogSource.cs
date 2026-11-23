using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace Hartsy.Extensions.LLMAssistant.Services.ImageTools;

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

    /// <summary>The installed text-to-image models the user may use.</summary>
    public static Task<List<CatalogEntry>> ModelsAsync(Session session) => ReadAsync(session, "Stable-Diffusion");

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

    /// <summary>Lists one kind of model through core. A failure is logged and reads as an empty list, so a tool never fails on a listing problem.</summary>
    private static async Task<List<CatalogEntry>> ReadAsync(Session session, string subtype)
    {
        try
        {
            JObject reply = await ModelsAPI.ListModels(session, "", 20, subtype);
            if (reply.ContainsKey("error"))
            {
                Logs.Warning($"[LLMAssistant] Listing {subtype} models failed: {reply["error"]}");
                return [];
            }
            return [.. (reply["files"] as JArray ?? []).OfType<JObject>().Select(CatalogEntry.FromNet).Where(e => e.Name.Length > 0)];
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Listing {subtype} models failed: {ex.Message}");
            return [];
        }
    }
}
