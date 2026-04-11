using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Asset endpoints. Assets (markdown/html/code/image/etc produced by the assistant or
/// tools) are persisted as part of the owning thread's JSON blob under the <c>assets</c> key.
/// The client still performs the actual extraction — these endpoints exist so headless API
/// clients can list, fetch, and delete assets per thread without re-running extraction.</summary>
public static class AssetEndpoints
{
    /// <summary>Returns all assets for the given thread. Includes full content for each asset.</summary>
    public static async Task<JObject> LLMAssistantGetAssets(Session session, string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Thread '{threadId}' not found." };
        }
        JArray assets = thread["assets"] as JArray ?? [];
        return new JObject
        {
            ["success"] = true,
            ["threadId"] = threadId,
            ["assets"] = assets
        };
    }

    /// <summary>Returns a single asset from a thread by asset ID.</summary>
    public static async Task<JObject> LLMAssistantGetAsset(Session session, string threadId, string assetId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(assetId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId and assetId are required." };
        }
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Thread '{threadId}' not found." };
        }
        JArray assets = thread["assets"] as JArray ?? [];
        JObject asset = assets.OfType<JObject>().FirstOrDefault(a => a["id"]?.ToString() == assetId);
        if (asset is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Asset '{assetId}' not found in thread." };
        }
        return new JObject
        {
            ["success"] = true,
            ["asset"] = asset
        };
    }

    /// <summary>Deletes a single asset from a thread. Does not touch message content — the UI should
    /// also remove any inline asset card referencing this ID when it receives success.</summary>
    public static async Task<JObject> LLMAssistantDeleteAsset(Session session, string threadId, string assetId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(assetId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId and assetId are required." };
        }
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Thread '{threadId}' not found." };
        }
        JArray assets = thread["assets"] as JArray;
        if (assets is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Asset '{assetId}' not found in thread." };
        }
        JObject target = assets.OfType<JObject>().FirstOrDefault(a => a["id"]?.ToString() == assetId);
        if (target is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Asset '{assetId}' not found in thread." };
        }
        assets.Remove(target);
        thread["assets"] = assets;
        ThreadStorageService.SaveThread(session.User, thread);
        return new JObject { ["success"] = true };
    }
}
