using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Routes for workspaces: the folders a chat's code tools work in. Using one needs <c>llm_code_mode</c>; allowing a folder outside the user's own
/// space needs <c>llm_workspace_roots</c>.</summary>
public static class WorkspaceEndpoints
{
    /// <summary>The workspaces the caller may use. Request: none. Returns <c>{success, workspaces[], canUseCode, canManageRoots}</c>, each workspace
    /// <c>{id, label, userOwned}</c>. An allowed folder's path is not sent (see <see cref="LLMAssistantGetWorkspaceRoots"/>).</summary>
    public static Task<JObject> LLMAssistantGetWorkspaces(Session session)
    {
        bool canUse = session.User?.HasPermission(LLMAssistantAPI.PermCodeMode) ?? false;
        JArray list = [];
        if (canUse && session.User is not null)
        {
            WorkspaceService.Create(session.User, WorkspaceService.DefaultName, out _);
            foreach (Workspace workspace in WorkspaceService.List(session.User))
            {
                list.Add(new JObject { ["id"] = workspace.Id, ["label"] = workspace.Label, ["userOwned"] = workspace.UserOwned });
            }
        }
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["workspaces"] = list,
            ["canUseCode"] = canUse,
            ["canManageRoots"] = session.User?.HasPermission(LLMAssistantAPI.PermWorkspaceRoots) ?? false,
        });
    }

    /// <summary>Makes one of the caller's own workspaces. Request: <c>{ name }</c>. Returns <c>{success, id}</c>.</summary>
    public static Task<JObject> LLMAssistantCreateWorkspace(Session session, string name)
    {
        if (session.User is null || !session.User.HasPermission(LLMAssistantAPI.PermCodeMode))
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "Workspaces need the LLM Code Mode permission." });
        }
        string id = WorkspaceService.Create(session.User, name, out string error);
        return Task.FromResult(id is null ? new JObject { ["success"] = false, ["error"] = error } : new JObject { ["success"] = true, ["id"] = id });
    }

    /// <summary>Picks the workspace a chat's code tools work in. Request: <c>{ threadId, workspaceId }</c>; an empty <c>workspaceId</c> goes back to the
    /// caller's default. Only a workspace the caller may use is accepted.</summary>
    public static Task<JObject> LLMAssistantSetThreadWorkspace(Session session, string threadId, string workspaceId = "")
    {
        if (!string.IsNullOrEmpty(workspaceId) && (session.User is null || !session.User.HasPermission(LLMAssistantAPI.PermCodeMode) || WorkspaceService.Resolve(session.User, workspaceId) is null))
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "That workspace is not available to you." });
        }
        JObject thread = ThreadStorageService.SetWorkspace(session.User, threadId, workspaceId);
        return Task.FromResult(thread is null
            ? new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." }
            : new JObject { ["success"] = true, ["workspace"] = thread["workspace"] });
    }

    /// <summary>The folders an admin has allowed, with their paths. Request: none. Needs <c>llm_workspace_roots</c>. Returns <c>{success, roots[]}</c>,
    /// each <c>{id, label, path, exists}</c>.</summary>
    public static Task<JObject> LLMAssistantGetWorkspaceRoots(Session session)
    {
        if (session.User is null || !session.User.HasPermission(LLMAssistantAPI.PermWorkspaceRoots))
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "Listing allowed folders needs the LLM Workspace Roots permission." });
        }
        JArray roots = new(WorkspaceService.AllowedRoots().Select(r => new JObject { ["id"] = r.Id, ["label"] = r.Label, ["path"] = r.Path, ["exists"] = System.IO.Directory.Exists(r.Path) }));
        return Task.FromResult(new JObject { ["success"] = true, ["roots"] = roots });
    }

    /// <summary>Allows a folder on the host as a workspace for every user who may use code mode. Request: <c>{ id, path, label? }</c>. Needs <c>llm_workspace_roots</c>.</summary>
    public static Task<JObject> LLMAssistantSaveWorkspaceRoot(Session session, string id, string path, string label = "")
    {
        string saved = WorkspaceService.SaveRoot(id, path, label, session.User, out string error);
        return Task.FromResult(saved is null ? new JObject { ["success"] = false, ["error"] = error } : new JObject { ["success"] = true, ["id"] = saved });
    }

    /// <summary>Stops allowing a folder. Request: <c>{ id }</c>. Needs <c>llm_workspace_roots</c>. A chat that used it falls back to its owner's default workspace
    /// only if it is set again; until then the code tools report that the workspace is not available.</summary>
    public static Task<JObject> LLMAssistantDeleteWorkspaceRoot(Session session, string id)
        => Task.FromResult(WorkspaceService.DeleteRoot(id, session.User)
            ? new JObject { ["success"] = true }
            : new JObject { ["success"] = false, ["error"] = "Folder not found, or you may not manage allowed folders." });
}
