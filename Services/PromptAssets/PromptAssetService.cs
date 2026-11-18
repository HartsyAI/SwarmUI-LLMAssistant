using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptAssets;

/// <summary>What one chat can see: every asset from the folders that apply to it, merged by name.</summary>
public sealed class PromptAssetSet
{
    /// <summary>The assets, skills then commands, each in name order.</summary>
    public List<PromptAsset> Assets = [];

    /// <summary>The chat's workspace when it has one the assets were looked for in (empty otherwise), what it is called, whether the user trusts it, and how many assets
    /// its folders hold that were left out because they are not trusted.</summary>
    public string WorkspaceId = "", WorkspaceLabel = "";
    public bool WorkspaceTrusted;
    public int IgnoredWorkspaceAssets;

    /// <summary>The asset of this kind and name, or null.</summary>
    public PromptAsset Get(string kind, string name) => Assets.FirstOrDefault(a => a.Kind == kind && a.Name == name);

    /// <summary>The skills the model may load: those not marked for the user alone.</summary>
    public IEnumerable<PromptAsset> ModelSkills => Assets.Where(a => a.Kind == PromptAsset.KindSkill && !a.UserOnly);
}

/// <summary>Skills and commands: where they come from, who sees which, and how a chat uses them.
/// <para><b>Folders.</b> Bundled ones ship with the extension; shared ones are the admin's, in the server's data folder; personal ones are each user's own, also in the
/// data folder (never in the user's output folder, which the file tools write to, so a model cannot plant one); and a code-mode chat's workspace can hold its own in
/// <c>.llma/</c> or <c>.claude/</c>. A name that exists in more than one of the first three scopes takes the last (personal over shared over bundled).</para>
/// <para><b>Workspaces.</b> A file in a workspace is text anyone who can write there controls, and the model can write there, so a workspace's assets are used only for a
/// code-mode chat of a user holding <c>llm_code_mode</c>, only after that user has trusted that workspace (off for every workspace until they do), and they never replace an
/// asset of another scope.</para>
/// <para><b>In a chat.</b> A skill is advertised to the model as one line (name and description) in the system prompt, and the model loads the rest with the <c>skill</c> tool.
/// A command is a template the user runs by typing its name; its text is put into the message they send. There is one resolver, <see cref="For"/>, behind the index, the tool,
/// the picker and the expansion, so what the model is told about is exactly what the tool will give it.</para></summary>
public static class PromptAssetService
{
    /// <summary>The folders (inside a workspace) searched for <c>skills/</c> and <c>commands/</c>, in order; the first to have a name wins.</summary>
    public static readonly string[] WorkspaceDirs = [".llma", ".claude"];

    /// <summary>The settings key (in the user's own layer) of the workspaces the user trusts, a list of workspace ids.</summary>
    public const string TrustedKey = "trustedWorkspaces";

    /// <summary>The most workspaces a user can trust.</summary>
    public const int MaxTrusted = 100;

    /// <summary>The most skills listed in the system prompt, and the longest description of each there.</summary>
    public const int MaxIndexedSkills = 40, MaxIndexedDescriptionChars = 200;

    /// <summary>The longest description saved.</summary>
    public const int MaxDescriptionChars = 300;

    /// <summary>Held while an asset file is written or removed.</summary>
    private static readonly object FileLock = new();

    /// <summary>The folder of bundled assets (set when the extension starts); null when there is none.</summary>
    public static string BundledRoot;

    /// <summary>Where the shared assets live. Replaceable so tests can point it at a temp folder.</summary>
    public static Func<string> SharedRoot = () => Path.Combine(Program.DataDir, "LLMAssistant", "prompt_assets", "shared");

    /// <summary>Where a user's personal assets live. Replaceable so tests can point it at a temp folder.</summary>
    public static Func<User, string> PersonalRoot = user =>
    {
        // A user id that cleans down to nothing would put every such user in the same folder, so such a user has no personal folder.
        string key = Utilities.StrictFilenameClean(user.UserID ?? "").Trim('.', ' ');
        return key.Length == 0 ? null : Path.Combine(Program.DataDir, "LLMAssistant", "prompt_assets", "users", key);
    };

    // ===== Resolving =====

    /// <summary>Everything <paramref name="user"/> can use in <paramref name="thread"/> (null for no chat: only the folders that do not depend on one).</summary>
    public static PromptAssetSet For(User user, JObject thread)
    {
        List<PromptAsset> bundled = string.IsNullOrEmpty(BundledRoot) ? [] : PromptAssetStore.Load(BundledRoot, PromptAsset.ScopeBundled);
        string sharedRoot = SharedRoot?.Invoke();
        List<PromptAsset> shared = string.IsNullOrEmpty(sharedRoot) ? [] : PromptAssetStore.Load(sharedRoot, PromptAsset.ScopeShared);
        string personalRoot = user is null ? null : PersonalRoot?.Invoke(user);
        List<PromptAsset> personal = string.IsNullOrEmpty(personalRoot) ? [] : PromptAssetStore.Load(personalRoot, PromptAsset.ScopePersonal);
        string workspaceId = "", workspaceLabel = "";
        bool trusted = false;
        List<PromptAsset> inWorkspace = [];
        if (user is not null && WorkspaceApplies(user, thread))
        {
            Workspace workspace = WorkspaceService.ResolveForThread(user, thread);
            if (workspace is not null)
            {
                workspaceId = workspace.Id;
                workspaceLabel = workspace.Label;
                trusted = IsTrusted(user, workspace.Id);
                inWorkspace = LoadWorkspace(workspace);
            }
        }
        return Merge(bundled, shared, personal, inWorkspace, trusted, workspaceId, workspaceLabel);
    }

    /// <summary>Combines the scopes' assets: bundled, then shared, then personal, each replacing a same-named one before it. The workspace's come last and are used only
    /// when <paramref name="workspaceTrusted"/>, and then only for a name nothing else has; otherwise they are counted as ignored.</summary>
    public static PromptAssetSet Merge(IEnumerable<PromptAsset> bundled, IEnumerable<PromptAsset> shared, IEnumerable<PromptAsset> personal, IEnumerable<PromptAsset> workspace,
        bool workspaceTrusted, string workspaceId = "", string workspaceLabel = "")
    {
        Dictionary<(string, string), PromptAsset> merged = [];
        foreach (PromptAsset asset in bundled.Concat(shared).Concat(personal))
        {
            merged[(asset.Kind, asset.Name)] = asset;
        }
        List<PromptAsset> inWorkspace = [.. workspace];
        if (workspaceTrusted)
        {
            foreach (PromptAsset asset in inWorkspace)
            {
                merged.TryAdd((asset.Kind, asset.Name), asset);
            }
        }
        return new PromptAssetSet
        {
            Assets = [.. merged.Values.OrderBy(a => a.Kind == PromptAsset.KindSkill ? 0 : 1).ThenBy(a => a.Name, StringComparer.Ordinal)],
            WorkspaceId = workspaceId,
            WorkspaceLabel = workspaceLabel,
            WorkspaceTrusted = workspaceTrusted,
            IgnoredWorkspaceAssets = workspaceTrusted ? 0 : inWorkspace.Count,
        };
    }

    /// <summary>Whether a chat's workspace is looked at for assets at all: it is in code mode and its user may use code mode (the same bar as the project instructions).</summary>
    private static bool WorkspaceApplies(User user, JObject thread)
        => thread is not null && CodeModeService.IsCodeThread(thread) && user.HasPermission(WebAPI.LLMAssistantAPI.PermCodeMode);

    /// <summary>The assets in a workspace's folders, the first folder winning a name.</summary>
    private static List<PromptAsset> LoadWorkspace(Workspace workspace)
    {
        List<PromptAsset> result = [];
        foreach (string dir in WorkspaceDirs)
        {
            if (PathSandbox.TryResolve(workspace.Root, dir, out string full, out _))
            {
                foreach (PromptAsset asset in PromptAssetStore.Load(full, PromptAsset.ScopeWorkspace))
                {
                    if (!result.Any(a => a.Kind == asset.Kind && a.Name == asset.Name))
                    {
                        result.Add(asset);
                    }
                }
            }
        }
        return result;
    }

    // ===== Workspace trust =====

    /// <summary>Whether <paramref name="user"/> has trusted the workspace <paramref name="workspaceId"/>.</summary>
    public static bool IsTrusted(User user, string workspaceId)
        => SettingsService.GetUserSettings(user)[TrustedKey] is JArray trusted && trusted.Any(t => t.ToString() == workspaceId);

    /// <summary>Trusts or stops trusting a workspace the user can use. Returns false with the reason in <paramref name="error"/> when it is not one of theirs.</summary>
    public static bool SetTrusted(User user, string workspaceId, bool trusted, out string error)
    {
        error = null;
        if (user is null || !user.HasPermission(WebAPI.LLMAssistantAPI.PermCodeMode))
        {
            error = "Trusting a workspace needs the LLM Code Mode permission.";
            return false;
        }
        if (!WorkspaceService.List(user).Any(w => w.Id == workspaceId))
        {
            error = "That is not one of your workspaces.";
            return false;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject layer = SettingsService.GetUserSettings(user);
            List<string> ids = [.. (layer[TrustedKey] as JArray ?? []).Select(t => t.ToString())];
            ids.Remove(workspaceId);
            if (trusted)
            {
                if (ids.Count >= MaxTrusted)
                {
                    error = $"Up to {MaxTrusted} workspaces can be trusted.";
                    return false;
                }
                ids.Add(workspaceId);
            }
            layer[TrustedKey] = new JArray(ids);
            SettingsService.ReplaceUserSettings(user, layer);
        }
        return true;
    }

    // ===== In a chat =====

    /// <summary>The tools of a chat plus the <c>skill</c> tool, when tool calling is on for the chat, the user may use it, it is enabled in the tool list and there is at
    /// least one skill the model may load. The chat's assistant does not have to list it: skills are something the user set up, not a capability to hand out per assistant.</summary>
    public static List<JObject> WithSkillTool(List<JObject> enabled, JObject thread, JObject settings, User user, bool toolsOn)
    {
        if (!toolsOn || user is null || !user.HasPermission(WebAPI.LLMAssistantAPI.PermToolSkills) || enabled.Any(t => t["id"]?.ToString() == ToolConstants.Skill))
        {
            return enabled;
        }
        if ((settings["tools"] as JObject)?[ToolConstants.Skill] is not JObject tool || tool["enabled"]?.Value<bool>() == false)
        {
            return enabled;
        }
        return For(user, thread).ModelSkills.Any() ? [.. enabled, tool] : enabled;
    }

    /// <summary>Whether a chat may run <paramref name="toolId"/> although its assistant does not list it: the <c>skill</c> tool, in a chat that has tool calling on.</summary>
    public static bool GrantsTool(string toolId, User user, string threadId)
    {
        if (toolId != ToolConstants.Skill || user is null || string.IsNullOrEmpty(threadId))
        {
            return false;
        }
        JObject thread = ThreadStorageService.GetThread(user, threadId);
        bool pinnedOff = thread?["toolsEnabled"]?.Type == JTokenType.Boolean && !thread["toolsEnabled"].Value<bool>();
        return thread is not null && !pinnedOff;
    }

    /// <summary>The system prompt with the skill index after it, when the <c>skill</c> tool is one of the chat's tools. The index is the same text each turn until a skill
    /// changes (sorted by name, bounded in size), so a local model's prompt cache stays valid.</summary>
    public static string AppendIndex(string systemPrompt, IEnumerable<JObject> enabledTools, User user, JObject thread)
    {
        if (user is null || !user.HasPermission(WebAPI.LLMAssistantAPI.PermToolSkills) || !enabledTools.Any(t => t["handlerId"]?.ToString() == ToolConstants.Skill))
        {
            return systemPrompt;
        }
        string index = BuildIndex(For(user, thread).ModelSkills);
        return index is null ? systemPrompt : $"{systemPrompt}\n\n{index}";
    }

    /// <summary>The system-prompt section listing <paramref name="skills"/>, or null when there are none.</summary>
    public static string BuildIndex(IEnumerable<PromptAsset> skills)
    {
        List<PromptAsset> list = [.. skills.OrderBy(s => s.Name, StringComparer.Ordinal).Take(MaxIndexedSkills)];
        if (list.Count == 0)
        {
            return null;
        }
        StringBuilder sb = new();
        sb.Append("# Skills\n\nSkills are written instructions for particular kinds of task. When the user's request clearly matches one, call the `skill` tool with its name and follow what it returns. ")
            .Append("Do not load a skill that does not apply.\n");
        foreach (PromptAsset skill in list)
        {
            string description = Collapse(skill.Description);
            if (description.Length > MaxIndexedDescriptionChars)
            {
                description = description[..MaxIndexedDescriptionChars] + "…";
            }
            sb.Append("\n- ").Append(skill.Name).Append(": ").Append(description.Length == 0 ? "(no description)" : description);
        }
        return sb.ToString();
    }

    /// <summary>The text on one line, runs of white space made one space.</summary>
    private static string Collapse(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();

    // ===== Running one =====

    /// <summary>The message text for the user running <paramref name="asset"/> with <paramref name="arguments"/> typed after its name. A command is its template: every
    /// <c>$ARGUMENTS</c> is replaced by what was typed (in one pass, so an argument that contains the token is not expanded again), and when the template has no token the
    /// arguments are added at the end so they are not lost. A skill is wrapped with its text, so a skill the model is not allowed to load (or a chat that has no
    /// <c>skill</c> tool) still works when the user asks for it. Nothing in a template is run or read from disk: there is no <c>!command</c> or <c>@file</c> syntax.</summary>
    public static string Expand(PromptAsset asset, string arguments)
    {
        arguments = (arguments ?? "").Trim();
        if (asset.Kind == PromptAsset.KindCommand)
        {
            if (asset.Body.Contains("$ARGUMENTS", StringComparison.Ordinal))
            {
                return asset.Body.Replace("$ARGUMENTS", arguments, StringComparison.Ordinal);
            }
            return arguments.Length == 0 ? asset.Body : $"{asset.Body}\n\nARGUMENTS: {arguments}";
        }
        string text = $"Use the \"{asset.Name}\" skill for the request below.\n\n--- Skill: {asset.Name} ---\n{asset.Body}\n--- End of skill ---";
        return arguments.Length == 0 ? text : $"{text}\n\n{arguments}";
    }

    // ===== Saving =====

    /// <summary>The folder a scope's assets are saved in (<c>shared</c> or <c>personal</c>), or null for a scope that cannot be saved to.</summary>
    public static string RootFor(User user, string scope)
        => scope == PromptAsset.ScopeShared ? SharedRoot?.Invoke() : scope == PromptAsset.ScopePersonal ? PersonalRoot?.Invoke(user) : null;

    /// <summary>The file an asset is kept in under <paramref name="root"/>.</summary>
    private static string FilePathFor(string root, string kind, string name)
        => kind == PromptAsset.KindSkill ? Path.Combine(root, "skills", name, PromptAssetStore.SkillFile) : Path.Combine(root, "commands", name + ".md");

    /// <summary>Saves a skill or command as a personal asset, or as a shared one (which needs <c>llm_shared_write</c>). Returns false with the reason in <paramref name="error"/>.</summary>
    public static bool Save(User user, string kind, string name, string description, string argumentHint, string body, bool userOnly, string scope, out string error)
    {
        error = null;
        name = (name ?? "").Trim().ToLowerInvariant();
        scope = scope == PromptAsset.ScopeShared ? PromptAsset.ScopeShared : PromptAsset.ScopePersonal;
        if (user is null || !PromptAsset.IsValidKind(kind))
        {
            error = "Choose skill or command.";
            return false;
        }
        if (!PromptAsset.IsValidName(name))
        {
            error = $"A name is 1 to {PromptAsset.MaxNameLength} lowercase letters, digits, dashes or underscores, starting with a letter or digit.";
            return false;
        }
        if (scope == PromptAsset.ScopeShared && !user.HasPermission(WebAPI.LLMAssistantAPI.PermSharedWrite))
        {
            error = "Saving a shared skill or command needs the LLM Shared Write permission.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            error = "The text is empty.";
            return false;
        }
        if ((description ?? "").Length > MaxDescriptionChars)
        {
            error = $"The description can be up to {MaxDescriptionChars} characters.";
            return false;
        }
        string text = PromptAssetFile.Format(description, argumentHint, userOnly, body);
        if (Encoding.UTF8.GetByteCount(text) > PromptAsset.MaxFileBytes)
        {
            error = $"The text is too long (the limit is {PromptAsset.MaxFileBytes / 1024} KB).";
            return false;
        }
        string root = RootFor(user, scope);
        if (string.IsNullOrEmpty(root))
        {
            error = "There is nowhere to save it.";
            return false;
        }
        string file = FilePathFor(root, kind, name);
        lock (FileLock)
        {
            if (!File.Exists(file) && PromptAssetStore.Load(root, scope).Count >= PromptAssetStore.MaxPerRoot)
            {
                error = $"Up to {PromptAssetStore.MaxPerRoot} skills and commands can be kept in one place.";
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, file, true);
        }
        if (scope == PromptAsset.ScopeShared)
        {
            AuditLogService.RecordSharedWrite("update", $"{kind}:{name}", user, new JObject { ["kind"] = kind });
        }
        return true;
    }

    /// <summary>Deletes a personal or shared skill or command. False when there is none, or the user may not change that scope.</summary>
    public static bool Delete(User user, string kind, string name, string scope)
    {
        scope = scope == PromptAsset.ScopeShared ? PromptAsset.ScopeShared : PromptAsset.ScopePersonal;
        if (user is null || !PromptAsset.IsValidKind(kind) || !PromptAsset.IsValidName(name ?? ""))
        {
            return false;
        }
        if (scope == PromptAsset.ScopeShared && !user.HasPermission(WebAPI.LLMAssistantAPI.PermSharedWrite))
        {
            return false;
        }
        string root = RootFor(user, scope);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }
        string file = FilePathFor(root, kind, name);
        lock (FileLock)
        {
            if (!File.Exists(file))
            {
                return false;
            }
            File.Delete(file);
            string folder = Path.GetDirectoryName(file);
            if (kind == PromptAsset.KindSkill && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        if (scope == PromptAsset.ScopeShared)
        {
            AuditLogService.RecordSharedWrite("delete", $"{kind}:{name}", user);
        }
        return true;
    }
}
