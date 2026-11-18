using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Routes for skills and commands: what a chat can use (for the <c>/</c> picker and the settings list), running one, and saving, deleting and trusting. Nothing here
/// names a host path. Reading and running need <c>llm_chat</c>; changing needs <c>llm_settings</c>, and a shared one also <c>llm_shared_write</c>.</summary>
public static class PromptAssetEndpoints
{
    /// <summary>The most characters of arguments accepted when running a command.</summary>
    public const int MaxArgumentChars = 20_000;

    /// <summary>The skills and commands the caller can use in a chat. Request: <c>{ threadId? }</c> (without one, only the shared and personal ones). Returns <c>{success, assets[],
    /// canWriteShared, workspace, workspaces[]}</c>; each asset is <c>{kind, name, description, argumentHint, scope, userOnly}</c> (the text is fetched with
    /// <see cref="LLMAssistantGetPromptAsset"/>). <c>workspace</c> is <c>{id, label, trusted, ignored}</c> for a code-mode chat whose workspace was looked in (<c>ignored</c> is how
    /// many assets it holds that are not used because it is not trusted), and <c>workspaces</c> lists the caller's workspaces with <c>trusted</c>, for the settings screen.</summary>
    public static Task<JObject> LLMAssistantGetPromptAssets(Session session, string threadId = "")
    {
        JObject thread = ReadThread(session, threadId);
        PromptAssetSet set = PromptAssetService.For(session.User, thread);
        JArray assets = [];
        foreach (PromptAsset asset in set.Assets)
        {
            assets.Add(Describe(asset));
        }
        JArray workspaces = [];
        if (session.User?.HasPermission(LLMAssistantAPI.PermCodeMode) ?? false)
        {
            foreach (Workspace workspace in WorkspaceService.List(session.User))
            {
                workspaces.Add(new JObject { ["id"] = workspace.Id, ["label"] = workspace.Label, ["trusted"] = PromptAssetService.IsTrusted(session.User, workspace.Id) });
            }
        }
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["assets"] = assets,
            ["canWriteShared"] = session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false,
            ["workspace"] = set.WorkspaceId.Length == 0 ? null : new JObject
            {
                ["id"] = set.WorkspaceId, ["label"] = set.WorkspaceLabel, ["trusted"] = set.WorkspaceTrusted, ["ignored"] = set.IgnoredWorkspaceAssets,
            },
            ["workspaces"] = workspaces,
        });
    }

    /// <summary>One asset with its text, for the editor. Request: <c>{ kind, name, scope?, threadId? }</c>: a <c>scope</c> of <c>shared</c> or <c>personal</c> reads that
    /// folder's file even when another scope's of the same name takes precedence. Returns <c>{success, asset}</c> with <c>body</c> added.</summary>
    public static Task<JObject> LLMAssistantGetPromptAsset(Session session, string kind, string name, string scope = "", string threadId = "")
    {
        PromptAsset asset = Find(session, kind, name, scope, threadId);
        if (asset is null)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "There is no such skill or command." });
        }
        JObject json = Describe(asset);
        json["body"] = asset.Body;
        return Task.FromResult(new JObject { ["success"] = true, ["asset"] = json });
    }

    /// <summary>The message text for running a skill or command. Request: <c>{ kind, name, arguments?, threadId? }</c>. Returns <c>{success, text}</c>; the browser puts
    /// the text in the message it sends. See <see cref="PromptAssetService.Expand"/> for what is replaced.</summary>
    public static Task<JObject> LLMAssistantExpandPromptAsset(Session session, string kind, string name, string arguments = "", string threadId = "")
    {
        if ((arguments ?? "").Length > MaxArgumentChars)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = $"The arguments are too long (the limit is {MaxArgumentChars} characters)." });
        }
        PromptAsset asset = Find(session, kind, name, "", threadId);
        return Task.FromResult(asset is null
            ? new JObject { ["success"] = false, ["error"] = $"There is no {kind} named '{name}'." }
            : new JObject { ["success"] = true, ["text"] = PromptAssetService.Expand(asset, arguments) });
    }

    /// <summary>Creates or replaces a personal skill or command, or a shared one with <c>scope</c> <c>shared</c>. Request: <c>{ kind, name, body, description?, argumentHint?,
    /// userOnly?, scope? }</c>. A skill's file is <c>skills/&lt;name&gt;/SKILL.md</c>; any other files in its folder are left alone.</summary>
    public static Task<JObject> LLMAssistantSavePromptAsset(Session session, string kind, string name, string body, string description = "", string argumentHint = "", bool userOnly = false, string scope = "personal")
    {
        bool saved = PromptAssetService.Save(session.User, kind, name, description, argumentHint, body, userOnly, scope, out string error);
        return Task.FromResult(saved ? new JObject { ["success"] = true } : new JObject { ["success"] = false, ["error"] = error });
    }

    /// <summary>Deletes a personal skill or command, or a shared one with <c>scope</c> <c>shared</c>. Request: <c>{ kind, name, scope? }</c>.</summary>
    public static Task<JObject> LLMAssistantDeletePromptAsset(Session session, string kind, string name, string scope = "personal")
        => Task.FromResult(PromptAssetService.Delete(session.User, kind, name, scope)
            ? new JObject { ["success"] = true }
            : new JObject { ["success"] = false, ["error"] = "There is no such skill or command, or you may not change that one." });

    /// <summary>Trusts a workspace's own skills and commands (<c>.llma/</c> and <c>.claude/</c> in it), or stops. Request: <c>{ workspaceId, trusted }</c>. Off for every
    /// workspace until the user turns it on, because the model can write files in a workspace and what is in them goes into prompts.</summary>
    public static Task<JObject> LLMAssistantSetWorkspaceTrust(Session session, string workspaceId, bool trusted)
        => Task.FromResult(PromptAssetService.SetTrusted(session.User, workspaceId, trusted, out string error)
            ? new JObject { ["success"] = true }
            : new JObject { ["success"] = false, ["error"] = error });

    /// <summary>The caller's thread <paramref name="threadId"/>, or null when none was given or it is not theirs.</summary>
    private static JObject ReadThread(Session session, string threadId)
        => string.IsNullOrEmpty(threadId) || session.User is null ? null : ThreadStorageService.GetThread(session.User, threadId);

    /// <summary>The asset the caller means, or null. A <paramref name="scope"/> of <c>shared</c> or <c>personal</c> reads that one folder; otherwise the chat's merged set is used.</summary>
    private static PromptAsset Find(Session session, string kind, string name, string scope, string threadId)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        if (!PromptAsset.IsValidKind(kind) || !PromptAsset.IsValidName(name))
        {
            return null;
        }
        if (scope is PromptAsset.ScopeShared or PromptAsset.ScopePersonal)
        {
            string root = PromptAssetService.RootFor(session.User, scope);
            return PromptAssetStore.Load(root, scope).FirstOrDefault(a => a.Kind == kind && a.Name == name);
        }
        return PromptAssetService.For(session.User, ReadThread(session, threadId)).Get(kind, name);
    }

    /// <summary>An asset as the browser sees it, without its text.</summary>
    private static JObject Describe(PromptAsset asset) => new()
    {
        ["kind"] = asset.Kind,
        ["name"] = asset.Name,
        ["description"] = asset.Description,
        ["argumentHint"] = asset.ArgumentHint,
        ["scope"] = asset.Scope,
        ["userOnly"] = asset.UserOnly,
    };
}
