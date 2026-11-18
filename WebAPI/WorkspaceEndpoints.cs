using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;

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

    /// <summary>The most entries one folder listing returns.</summary>
    public const int MaxListed = 1000;

    /// <summary>Lists one folder of a chat's workspace, for the files panel. Request: <c>{ threadId, path? }</c> (empty is the workspace's top). Returns
    /// <c>{success, path, entries[], truncated}</c>, each entry <c>{name, type, size}</c> with <c>type</c> <c>dir</c> or <c>file</c>, folders first. Links are left out
    /// (they could lead outside), and nothing names a host path. Needs <c>llm_code_mode</c>; not a model call, so it is not approved, hooked or audited.</summary>
    public static Task<JObject> LLMAssistantListWorkspaceFiles(Session session, string threadId, string path = "")
    {
        Workspace workspace = WorkspaceFor(session, threadId, out JObject failure);
        if (workspace is null)
        {
            return Task.FromResult(failure);
        }
        return Task.FromResult(ListFolder(workspace, path));
    }

    /// <summary>The listing of one folder in <paramref name="workspace"/>; separate from the route so it can be tested without a session.</summary>
    internal static JObject ListFolder(Workspace workspace, string path)
    {
        if (!PathSandbox.TryResolve(workspace.Root, path, out string full, out string error))
        {
            return new JObject { ["success"] = false, ["error"] = error };
        }
        if (!System.IO.Directory.Exists(full))
        {
            return new JObject { ["success"] = false, ["error"] = "That folder does not exist." };
        }
        List<JObject> entries = [];
        try
        {
            foreach (string directory in System.IO.Directory.EnumerateDirectories(full))
            {
                if (new System.IO.DirectoryInfo(directory).LinkTarget is null)
                {
                    entries.Add(new JObject { ["name"] = System.IO.Path.GetFileName(directory), ["type"] = "dir", ["size"] = 0 });
                }
            }
            foreach (string file in System.IO.Directory.EnumerateFiles(full))
            {
                System.IO.FileInfo info = new(file);
                if (info.LinkTarget is null && !info.Name.StartsWith(".llma-", StringComparison.Ordinal))
                {
                    entries.Add(new JObject { ["name"] = info.Name, ["type"] = "file", ["size"] = info.Length });
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return new JObject { ["success"] = false, ["error"] = "That folder cannot be read." };
        }
        List<JObject> sorted = [.. entries.OrderBy(e => e["type"]!.ToString() == "dir" ? 0 : 1).ThenBy(e => e["name"]!.ToString(), StringComparer.OrdinalIgnoreCase)];
        return new JObject
        {
            ["success"] = true,
            ["path"] = PathSandbox.Relative(workspace.Root, full),
            ["entries"] = new JArray(sorted.Take(MaxListed)),
            ["truncated"] = sorted.Count > MaxListed,
        };
    }

    /// <summary>Reads a text file of a chat's workspace for the viewer, a range of lines at a time (the same reader the model's <c>code_read</c> uses). Request:
    /// <c>{ threadId, path, offset?, limit? }</c>. Returns <c>{success, path, content, startLine, endLine, totalLines, truncated}</c>. Needs <c>llm_code_mode</c>.</summary>
    public static async Task<JObject> LLMAssistantReadWorkspaceFile(Session session, string threadId, string path, int offset = 1, int limit = 2000)
    {
        Workspace workspace = WorkspaceFor(session, threadId, out JObject failure);
        if (workspace is null)
        {
            return failure;
        }
        return await Tools.BuiltIn.CodeReadTool.Run(workspace, new JObject { ["path"] = path, ["offset"] = offset, ["limit"] = limit }, Program.GlobalProgramCancel);
    }

    /// <summary>The workspace of one of the caller's chats, or null with the failure to return. The caller needs <c>llm_code_mode</c> and must own the chat.</summary>
    private static Workspace WorkspaceFor(Session session, string threadId, out JObject failure)
    {
        failure = null;
        if (session.User is null || !session.User.HasPermission(LLMAssistantAPI.PermCodeMode))
        {
            failure = new JObject { ["success"] = false, ["error"] = "The workspace needs the LLM Code Mode permission." };
            return null;
        }
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            failure = new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
            return null;
        }
        Workspace workspace = WorkspaceService.ResolveForThread(session.User, thread);
        if (workspace is null)
        {
            failure = new JObject { ["success"] = false, ["error"] = "This chat's workspace is not available. Pick another one for the chat." };
        }
        return workspace;
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
