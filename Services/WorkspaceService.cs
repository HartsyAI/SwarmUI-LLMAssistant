using System.IO;
using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>A folder a chat's code tools work in.</summary>
/// <param name="Id">How it is named in a thread: <c>user:name</c> for one of the user's own, <c>root:id</c> for an admin-allowed folder.</param>
/// <param name="Label">What it is shown as.</param>
/// <param name="Root">The folder's full path.</param>
/// <param name="UserOwned">Made by the user under their own output folder, as opposed to an admin-allowed folder elsewhere on the host.</param>
public sealed record Workspace(string Id, string Label, string Root, bool UserOwned);

/// <summary>Where a chat's code tools may read and write. Every user has their own workspaces under <c>{output}/llm_assistant/workspaces/</c>, created
/// on demand and reachable by nobody else. An admin can also allow specific folders elsewhere on the host (a project checkout, for example)
/// by listing them in the shared <c>workspaceRoots</c> setting; nothing outside those is ever a workspace.</summary>
public static class WorkspaceService
{
    /// <summary>The settings key the shared layer keeps allowed folders under, as an object of id to <c>{ path, label }</c>.</summary>
    public const string RootsKey = "workspaceRoots";

    /// <summary>The workspace a chat uses when none was picked.</summary>
    public const string DefaultName = "default";

    /// <summary>Most workspaces one user may have, and most allowed folders.</summary>
    public const int MaxWorkspaces = 50, MaxRoots = 20;

    /// <summary>The prefix of a user's own workspace id, and of an allowed folder's.</summary>
    public const string UserPrefix = "user:", RootPrefix = "root:";

    /// <summary>Where a user's own workspaces live.</summary>
    public static string UserWorkspacesRoot(User user) => Path.GetFullPath(Path.Combine(user.OutputDirectory, FileWriteTool.SandboxSubdir, "workspaces"));

    /// <summary>Whether <paramref name="name"/> can name a workspace or an allowed folder: lowercase letters, digits, dashes and underscores.</summary>
    public static bool IsValidName(string name) => name is not null && Regex.IsMatch(name, "^[a-z0-9][a-z0-9_-]{0,39}$");

    /// <summary>Creates one of the user's own workspaces. Returns its id, or null with the reason in <paramref name="error"/>. An existing one is left as it is.</summary>
    public static string Create(User user, string name, out string error)
    {
        error = null;
        name = (name ?? "").Trim().ToLowerInvariant();
        if (!IsValidName(name))
        {
            error = "A workspace name is 1 to 40 lowercase letters, digits, dashes or underscores, starting with a letter or digit.";
            return null;
        }
        string path = Path.Combine(UserWorkspacesRoot(user), name);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(UserWorkspacesRoot(user));
            if (Directory.GetDirectories(UserWorkspacesRoot(user)).Length >= MaxWorkspaces)
            {
                error = $"Up to {MaxWorkspaces} workspaces can be made.";
                return null;
            }
            Directory.CreateDirectory(path);
        }
        return UserPrefix + name;
    }

    /// <summary>The workspaces a user may use: their own, then the folders an admin allowed.</summary>
    public static List<Workspace> List(User user)
    {
        List<Workspace> result = [];
        string own = UserWorkspacesRoot(user);
        if (Directory.Exists(own))
        {
            foreach (string directory in Directory.GetDirectories(own).OrderBy(d => d, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(directory);
                if (IsValidName(name) && new DirectoryInfo(directory).LinkTarget is null)
                {
                    result.Add(new Workspace(UserPrefix + name, name, directory, true));
                }
            }
        }
        foreach ((string id, string label, string path) in AllowedRoots())
        {
            if (Directory.Exists(path))
            {
                result.Add(new Workspace(RootPrefix + id, label, Path.GetFullPath(path), false));
            }
        }
        return result;
    }

    /// <summary>The workspace named by <paramref name="id"/> for this user, or null when there is none they may use. A null or empty id is the
    /// user's own <c>default</c> workspace, created if it is not there yet. The id is only ever looked up in what <see cref="List"/> offers (or
    /// made under the user's own folder), never turned into a path, so it cannot name anything else.</summary>
    public static Workspace Resolve(User user, string id)
    {
        if (user is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Create(user, DefaultName, out _);
            if (id is null)
            {
                return null;
            }
        }
        return List(user).FirstOrDefault(w => w.Id == id);
    }

    /// <summary>The folders an admin allowed, as (id, label, path). A stored entry that does not validate is skipped.</summary>
    public static List<(string Id, string Label, string Path)> AllowedRoots()
    {
        List<(string, string, string)> roots = [];
        if (SettingsService.GetSettings()[RootsKey] is JObject stored)
        {
            foreach (JProperty property in stored.Properties())
            {
                string path = (property.Value as JObject)?["path"]?.ToString();
                if (IsValidName(property.Name) && !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathRooted(path))
                {
                    string label = (property.Value as JObject)?["label"]?.ToString();
                    roots.Add((property.Name, string.IsNullOrWhiteSpace(label) ? property.Name : label.Trim(), path));
                }
            }
        }
        return roots;
    }

    /// <summary>Allows a folder as a workspace, or updates it. Needs <c>llm_workspace_roots</c>. Returns the id, or null with the reason in <paramref name="error"/>.</summary>
    public static string SaveRoot(string id, string path, string label, User user, out string error)
    {
        error = null;
        if (user is null || !user.HasPermission(WebAPI.LLMAssistantAPI.PermWorkspaceRoots))
        {
            error = "Allowing a folder as a workspace needs the LLM Workspace Roots permission.";
            return null;
        }
        id = (id ?? "").Trim().ToLowerInvariant();
        if (!IsValidName(id) || id.Length > 32)
        {
            error = "The id is 1 to 32 lowercase letters, digits, dashes or underscores.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) || !Directory.Exists(path))
        {
            error = "The path must be the full path of a folder that exists.";
            return null;
        }
        string full = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetPathRoot(full) == full)
        {
            error = "A drive or filesystem root cannot be a workspace.";
            return null;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject layer = SettingsService.GetSettings();
            JObject roots = layer[RootsKey] as JObject ?? [];
            if (!roots.ContainsKey(id) && roots.Count >= MaxRoots)
            {
                error = $"Up to {MaxRoots} folders can be allowed.";
                return null;
            }
            roots[id] = new JObject { ["path"] = full, ["label"] = (label ?? "").Trim() };
            layer[RootsKey] = roots;
            SettingsService.ReplaceSharedSettings(layer);
            AuditLogService.RecordSharedWrite("update", $"workspace-root:{id}", user, new JObject { ["path"] = full });
        }
        return id;
    }

    /// <summary>Stops allowing a folder. False when it was not allowed or the user may not change them.</summary>
    public static bool DeleteRoot(string id, User user)
    {
        if (user is null || !user.HasPermission(WebAPI.LLMAssistantAPI.PermWorkspaceRoots))
        {
            return false;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject layer = SettingsService.GetSettings();
            if (layer[RootsKey] is not JObject roots || !roots.Remove(id))
            {
                return false;
            }
            layer[RootsKey] = roots;
            SettingsService.ReplaceSharedSettings(layer);
            AuditLogService.RecordSharedWrite("delete", $"workspace-root:{id}", user);
        }
        return true;
    }
}
