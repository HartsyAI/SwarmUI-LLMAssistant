using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>API endpoints for assistant CRUD and activation. All endpoints are user-scoped:
/// reads return the caller's merged view (shared ⊕ personal), writes target the caller's
/// personal layer unless they explicitly pass <c>scope: "shared"</c> AND hold
/// <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
public static class AssistantEndpoints
{
    /// <summary>Returns all assistants visible to the caller (shared + personal) and the caller's
    /// active assistant ID. Each entry is tagged with a <c>_scope</c> marker so the UI can show a
    /// "shared"/"personal" badge.</summary>
    public static async Task<JObject> LLMAssistantGetAssistants(Session session)
    {
        JObject settings = SettingsService.GetMergedSettings(session.User);
        return new JObject
        {
            ["success"] = true,
            ["assistants"] = AssistantService.GetAssistantList(settings, session.User),
            ["activeAssistantId"] = AssistantService.GetActiveAssistantId(settings, session.User),
            ["canWriteShared"] = session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false
        };
    }

    /// <summary>Returns a single assistant by ID from the caller's merged view.</summary>
    public static async Task<JObject> LLMAssistantGetAssistant(Session session, string assistantId)
    {
        JObject assistant = AssistantService.GetAssistant(assistantId, user: session.User);
        return new JObject
        {
            ["success"] = true,
            ["assistant"] = assistant
        };
    }

    /// <summary>Creates or updates an assistant. Accepts an optional <c>scope</c> field
    /// ("personal" or "shared"). Defaults to personal. Shared writes require
    /// <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
    public static async Task<JObject> LLMAssistantSaveAssistant(Session session, JObject rawInput)
    {
        JObject assistantData = rawInput["assistant"] as JObject;
        if (assistantData is null)
        {
            return new JObject { ["success"] = false, ["error"] = "No assistant data provided." };
        }
        string scope = rawInput["scope"]?.ToString();
        string id = AssistantService.SaveAssistant(assistantData, session.User, scope);
        if (id is null)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "Not permitted to save to the requested scope (shared writes require llm_shared_write)."
            };
        }
        return new JObject { ["success"] = true, ["id"] = id, ["scope"] = scope ?? SettingsService.ScopePersonal };
    }

    /// <summary>Deletes an assistant. Auto-detects personal vs shared if <c>scope</c> is not
    /// provided. Shared deletes require <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
    public static async Task<JObject> LLMAssistantDeleteAssistant(Session session, string assistantId, string scope = null)
    {
        if (assistantId == AssistantConstants.DefaultId)
        {
            return new JObject { ["success"] = false, ["error"] = "Cannot delete the default assistant." };
        }
        bool deleted = AssistantService.DeleteAssistant(assistantId, session.User, scope);
        // Only include "error" on failure (a present "error" key — even null — is logged as an error).
        if (!deleted)
        {
            return new JObject { ["success"] = false, ["error"] = "Assistant not found or not permitted." };
        }
        return new JObject { ["success"] = true };
    }

    /// <summary>Sets the active assistant (personal preference, always stored in user layer).</summary>
    public static async Task<JObject> LLMAssistantSetActiveAssistant(Session session, string assistantId)
    {
        AssistantService.SetActiveAssistant(assistantId, session.User);
        return new JObject { ["success"] = true };
    }

    /// <summary>Subdirectory under the user's OutputDirectory where assistant avatars live.
    /// Sandboxed via the per-user output route (see <see cref="WebServer.ViewOutput"/>) so
    /// served avatars are auth-checked just like any other Output asset.</summary>
    public const string AvatarSubdir = "llm_assistant/avatars";

    /// <summary>Allowed extensions for avatar uploads, derived from the data-URI MIME type.</summary>
    private static readonly Dictionary<string, string> AvatarMimeToExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"]  = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"]  = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"]  = ".gif"
    };

    /// <summary>Hard cap on avatar file size, decoded. 2 MB is plenty — assistant avatars
    /// are tiny by nature and we don't need to host actual photographs here.</summary>
    private const int MaxAvatarBytes = 2 * 1024 * 1024;

    /// <summary>Uploads an assistant avatar from a data URI to the user's per-user output
    /// directory and returns the URL the UI should store in <c>assistant.avatar</c>. Served via the
    /// <c>/Output/{*Path}</c> route, which already enforces per-user auth (see <see cref="WebServer.ViewOutput"/>).</summary>
    public static async Task<JObject> LLMAssistantUploadAssistantAvatar(Session session, JObject rawInput)
    {
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Authentication required." };
        }
        string assistantId = rawInput["assistantId"]?.ToString();
        string imageData = rawInput["imageData"]?.ToString();
        if (string.IsNullOrWhiteSpace(assistantId))
        {
            return new JObject { ["success"] = false, ["error"] = "assistantId is required." };
        }
        if (string.IsNullOrWhiteSpace(imageData))
        {
            return new JObject { ["success"] = false, ["error"] = "imageData (data URI) is required." };
        }
        // Parse data URI: data:image/png;base64,<payload>
        if (!imageData.StartsWith("data:", StringComparison.Ordinal))
        {
            return new JObject { ["success"] = false, ["error"] = "imageData must be a data URI." };
        }
        int commaIdx = imageData.IndexOf(',');
        if (commaIdx <= 0)
        {
            return new JObject { ["success"] = false, ["error"] = "Malformed data URI." };
        }
        string header = imageData[5..commaIdx];
        string payload = imageData[(commaIdx + 1)..];
        string[] headerParts = header.Split(';');
        string mime = headerParts.Length > 0 ? headerParts[0] : "";
        if (!AvatarMimeToExt.TryGetValue(mime, out string ext))
        {
            return new JObject { ["success"] = false, ["error"] = $"Unsupported image type: {mime}. Allowed: png, jpg, webp, gif." };
        }
        if (!header.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return new JObject { ["success"] = false, ["error"] = "Only base64-encoded data URIs are supported." };
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload); }
        catch { return new JObject { ["success"] = false, ["error"] = "Invalid base64 payload." }; }
        if (bytes.Length > MaxAvatarBytes)
        {
            return new JObject { ["success"] = false, ["error"] = $"Image too large ({bytes.Length} bytes; max {MaxAvatarBytes})." };
        }
        // Sanitize the assistant id into a safe filename component (UUIDs are already safe but
        // legacy ids may contain odd chars). Strip everything that isn't alnum/dash/underscore.
        string safeId = new string([.. assistantId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')]);
        if (string.IsNullOrEmpty(safeId))
        {
            return new JObject { ["success"] = false, ["error"] = "assistantId contains no valid filename characters." };
        }
        try
        {
            string avatarsDir = Path.Combine(session.User.OutputDirectory, AvatarSubdir);
            Directory.CreateDirectory(avatarsDir);
            // Remove any existing avatar files for this id (could be a different extension).
            foreach (string existing in Directory.EnumerateFiles(avatarsDir, $"{safeId}.*"))
            {
                try { File.Delete(existing); } catch { /* tolerated; new write will overwrite */ }
            }
            string fullPath = Path.Combine(avatarsDir, safeId + ext);
            await File.WriteAllBytesAsync(fullPath, bytes);
            // Return the URL relative to the OutputPath root so the existing /Output/{*Path}
            // route serves it (per-user auth is already enforced there).
            string outputRoot = Path.GetFullPath(Program.ServerSettings.Paths.OutputPath);
            string relPath = Path.GetRelativePath(outputRoot, Path.GetFullPath(fullPath)).Replace('\\', '/');
            return new JObject
            {
                ["success"] = true,
                ["url"] = $"Output/{relPath}",
                ["bytesWritten"] = bytes.Length
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Avatar upload failed for {session.User.UserID}/{assistantId}: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Returns the bundled starter assistant templates. Read once from disk on first
    /// call and cached for the process lifetime — these are baseline reference data, not user data.
    /// The frontend uses them to populate the "Clone from template" dropdown in the assistant
    /// editor, giving users a working baseline (with per-model variants demoed) instead of a
    /// blank form.</summary>
    public static async Task<JObject> LLMAssistantGetStarterTemplates(Session session)
    {
        return new JObject
        {
            ["success"] = true,
            ["templates"] = StarterAssistantsCache.GetTemplates()
        };
    }

    /// <summary>Returns the resolved active assistant object for the caller.</summary>
    public static async Task<JObject> LLMAssistantGetActiveAssistant(Session session)
    {
        JObject settings = SettingsService.GetMergedSettings(session.User);
        JObject assistant = AssistantService.GetActiveAssistant(settings, session.User);
        return new JObject
        {
            ["success"] = true,
            ["activeAssistantId"] = AssistantService.GetActiveAssistantId(settings, session.User),
            ["assistant"] = assistant
        };
    }
}
