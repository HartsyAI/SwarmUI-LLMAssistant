using System.IO;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptStyles;

/// <summary>Every style one user can use, merged by id.</summary>
public sealed class PromptStyleSet
{
    /// <summary>The style in force for each id: the most personal scope's copy. In id order.</summary>
    public List<PromptStyle> Styles = [];

    /// <summary>Every copy found, one per scope per id, so a screen can tell a built-in style the user has customized from one they only use.</summary>
    public List<PromptStyle> AllCopies = [];

    /// <summary>The style in force for <paramref name="id"/>, or null.</summary>
    public PromptStyle Get(string id) => Styles.FirstOrDefault(s => s.Id == id);

    /// <summary>The scopes that hold a copy of <paramref name="id"/>.</summary>
    public List<string> ScopesOf(string id) => [.. AllCopies.Where(s => s.Id == id).Select(s => s.Scope).Distinct()];
}

/// <summary>Prompt styles: where they come from, who may change them, and the user's own rules that pin a model to a style.
/// <para><b>Folders.</b> The same three as skills and commands (see <see cref="PromptAssetService"/>): bundled styles ship with the extension, shared ones are the admin's, personal ones are each user's own.
/// A style of the same id in a more personal scope replaces the other, which is how a built-in style is customized (a personal copy) and reset (that copy deleted).</para>
/// <para><b>Rules.</b> A user's rules (a model name or pattern pinned to a style, or to none) are kept in their own settings layer.</para></summary>
public static class PromptStyleService
{
    /// <summary>The settings key (in the user's own layer) of the rules.</summary>
    public const string OverridesKey = "styleOverrides";

    /// <summary>Held while a style file is written or removed.</summary>
    private static readonly object FileLock = new();

    // ===== Reading =====

    /// <summary>Everything <paramref name="user"/> can use (null: only the bundled and shared styles).</summary>
    public static PromptStyleSet For(User user)
    {
        List<PromptStyle> bundled = string.IsNullOrEmpty(PromptAssetService.BundledRoot) ? [] : PromptStyleStore.Load(PromptAssetService.BundledRoot, PromptAsset.ScopeBundled);
        string sharedRoot = PromptAssetService.SharedRoot?.Invoke();
        List<PromptStyle> shared = string.IsNullOrEmpty(sharedRoot) ? [] : PromptStyleStore.Load(sharedRoot, PromptAsset.ScopeShared);
        string personalRoot = user is null ? null : PromptAssetService.PersonalRoot?.Invoke(user);
        List<PromptStyle> personal = string.IsNullOrEmpty(personalRoot) ? [] : PromptStyleStore.Load(personalRoot, PromptAsset.ScopePersonal);
        return Merge(bundled, shared, personal);
    }

    /// <summary>Combines the scopes' styles: bundled, then shared, then personal, each replacing a same-id style before it.</summary>
    public static PromptStyleSet Merge(IEnumerable<PromptStyle> bundled, IEnumerable<PromptStyle> shared, IEnumerable<PromptStyle> personal)
    {
        List<PromptStyle> all = [.. bundled, .. shared, .. personal];
        Dictionary<string, PromptStyle> merged = [];
        foreach (PromptStyle style in all)
        {
            merged[style.Id] = style;
        }
        return new PromptStyleSet { Styles = [.. merged.Values.OrderBy(s => s.Id, StringComparer.Ordinal)], AllCopies = all };
    }

    /// <summary>The style <paramref name="id"/> as kept in <paramref name="scope"/> (shared or personal; any other value gives the one in force), or null.</summary>
    public static PromptStyle Find(User user, string id, string scope)
    {
        id = (id ?? "").Trim().ToLowerInvariant();
        PromptStyleSet set = For(user);
        return scope is PromptAsset.ScopeShared or PromptAsset.ScopePersonal or PromptAsset.ScopeBundled
            ? set.AllCopies.FirstOrDefault(s => s.Id == id && s.Scope == scope)
            : set.Get(id);
    }

    // ===== Saving =====

    /// <summary>The first thing wrong with a style that is about to be saved, or null when it is fine.</summary>
    public static string Validate(PromptStyle style)
    {
        if (!PromptStyle.IsValidId(style.Id))
        {
            return $"An id is 1 to {PromptAsset.MaxNameLength} lowercase letters, digits, dashes or underscores, starting with a letter or digit.";
        }
        if (!PromptStyle.IsValidKind(style.Kind))
        {
            return "Choose image, video or chat model (llm).";
        }
        if (style.Name.Length > PromptStyle.MaxNameChars)
        {
            return $"The name can be up to {PromptStyle.MaxNameChars} characters.";
        }
        if (style.Description.Length > PromptStyle.MaxDescriptionChars)
        {
            return $"The description can be up to {PromptStyle.MaxDescriptionChars} characters.";
        }
        if (style.Matches.Count > PromptStyle.MaxMatches)
        {
            return $"A style can have up to {PromptStyle.MaxMatches} conditions.";
        }
        string[] allowed = PromptStyle.MatchKindsFor(style.Kind);
        foreach (StyleMatch match in style.Matches)
        {
            if (!allowed.Contains(match.Kind))
            {
                return $"A {style.Kind} style cannot use the condition '{match.Kind}'. It can use: {string.Join(", ", allowed)}.";
            }
            if (match.Pattern.Length == 0 || match.Pattern.Length > PromptStyle.MaxPatternChars || match.Pattern.Contains(','))
            {
                return $"A condition's pattern is 1 to {PromptStyle.MaxPatternChars} characters and cannot contain a comma.";
            }
        }
        if (style.Format is not (PromptStyle.FormatText or PromptStyle.FormatTags or PromptStyle.FormatJson))
        {
            return "The format is text, tags or json.";
        }
        if (style.Negative is not (PromptStyle.NegativeNone or PromptStyle.NegativeSeparate or PromptStyle.NegativeInline))
        {
            return "The negative prompt setting is none, separate or inline.";
        }
        if (style.Supports.Any(s => !PromptStyle.KnownSupports.Contains(s)))
        {
            return $"What a model honours can be: {string.Join(", ", PromptStyle.KnownSupports)}.";
        }
        if (style.Tasks.Any(t => !PromptStyle.KnownTasks.Contains(t)))
        {
            return $"A task can be: {string.Join(", ", PromptStyle.KnownTasks)}.";
        }
        if (string.IsNullOrWhiteSpace(style.Body))
        {
            return "The instructions are empty.";
        }
        if (Encoding.UTF8.GetByteCount(PromptStyleFile.Format(style)) > PromptAsset.MaxFileBytes)
        {
            return $"The style is too long (the limit is {PromptAsset.MaxFileBytes / 1024} KB).";
        }
        return null;
    }

    /// <summary>Saves a style as a personal one, or a shared one (which needs <c>llm_shared_write</c>). Returns false with the reason in <paramref name="error"/>.</summary>
    public static bool Save(User user, PromptStyle style, string scope, out string error)
    {
        error = null;
        scope = scope == PromptAsset.ScopeShared ? PromptAsset.ScopeShared : PromptAsset.ScopePersonal;
        if (user is null)
        {
            error = "There is no user.";
            return false;
        }
        error = Validate(style);
        if (error is not null)
        {
            return false;
        }
        if (scope == PromptAsset.ScopeShared && !user.HasPermission(WebAPI.LLMAssistantAPI.PermSharedWrite))
        {
            error = "Saving a shared style needs the LLM Shared Write permission.";
            return false;
        }
        string root = PromptAssetService.RootFor(user, scope);
        if (string.IsNullOrEmpty(root))
        {
            error = "There is nowhere to save it.";
            return false;
        }
        string file = FilePathFor(root, style.Id);
        lock (FileLock)
        {
            if (!File.Exists(file) && PromptStyleStore.Load(root, scope).Count >= PromptStyleStore.MaxPerRoot)
            {
                error = $"Up to {PromptStyleStore.MaxPerRoot} styles can be kept in one place.";
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp";
            File.WriteAllText(temp, PromptStyleFile.Format(style), new UTF8Encoding(false));
            File.Move(temp, file, true);
        }
        if (scope == PromptAsset.ScopeShared)
        {
            AuditLogService.RecordSharedWrite("update", $"style:{style.Id}", user, new JObject { ["kind"] = style.Kind });
        }
        return true;
    }

    /// <summary>Deletes a personal or shared style, which brings back the built-in one of the same id if there is one. False when there is none, or the user may not change that scope.</summary>
    public static bool Delete(User user, string id, string scope)
    {
        scope = scope == PromptAsset.ScopeShared ? PromptAsset.ScopeShared : PromptAsset.ScopePersonal;
        id = (id ?? "").Trim().ToLowerInvariant();
        if (user is null || !PromptStyle.IsValidId(id))
        {
            return false;
        }
        if (scope == PromptAsset.ScopeShared && !user.HasPermission(WebAPI.LLMAssistantAPI.PermSharedWrite))
        {
            return false;
        }
        string root = PromptAssetService.RootFor(user, scope);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }
        string file = FilePathFor(root, id);
        lock (FileLock)
        {
            if (!File.Exists(file))
            {
                return false;
            }
            File.Delete(file);
        }
        if (scope == PromptAsset.ScopeShared)
        {
            AuditLogService.RecordSharedWrite("delete", $"style:{id}", user);
        }
        return true;
    }

    /// <summary>The file a style is kept in under <paramref name="root"/>.</summary>
    private static string FilePathFor(string root, string id) => Path.Combine(root, PromptStyleStore.FolderName, id + ".md");

    // ===== The user's rules =====

    /// <summary>The user's rules, in the order they wrote them.</summary>
    public static List<StyleOverride> GetOverrides(User user)
    {
        List<StyleOverride> rules = [];
        if (SettingsService.GetUserSettings(user)[OverridesKey] is JArray array)
        {
            foreach (JToken token in array)
            {
                string axis = token["axis"]?.ToString() ?? "";
                string pattern = (token["match"]?.ToString() ?? "").Trim();
                string style = (token["style"]?.ToString() ?? "").Trim();
                if (StyleOverride.IsValidAxis(axis) && pattern.Length > 0 && style.Length > 0)
                {
                    rules.Add(new StyleOverride(axis, pattern, style));
                }
            }
        }
        return rules;
    }

    /// <summary>Replaces the user's rules. Returns false with the reason in <paramref name="error"/> when one is not valid; nothing is changed then.</summary>
    public static bool SetOverrides(User user, IEnumerable<StyleOverride> rules, out string error)
    {
        error = null;
        if (user is null)
        {
            error = "There is no user.";
            return false;
        }
        List<StyleOverride> clean = [];
        foreach (StyleOverride rule in rules)
        {
            string pattern = (rule.Pattern ?? "").Trim();
            string style = (rule.Style ?? "").Trim().ToLowerInvariant();
            if (!StyleOverride.IsValidAxis(rule.Axis))
            {
                error = "A rule is for an image model or a chat model.";
                return false;
            }
            if (pattern.Length == 0 || pattern.Length > PromptStyle.MaxPatternChars)
            {
                error = $"A rule's model name or pattern is 1 to {PromptStyle.MaxPatternChars} characters.";
                return false;
            }
            if (style != StyleOverride.Off && !PromptStyle.IsValidId(style))
            {
                error = $"A rule needs a style id, or '{StyleOverride.Off}'.";
                return false;
            }
            if (!clean.Any(c => c.Axis == rule.Axis && string.Equals(c.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
            {
                clean.Add(new StyleOverride(rule.Axis, pattern, style));
            }
        }
        if (clean.Count > PromptStyle.MaxOverrides)
        {
            error = $"Up to {PromptStyle.MaxOverrides} rules can be kept.";
            return false;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject layer = SettingsService.GetUserSettings(user);
            layer[OverridesKey] = new JArray(clean.Select(c => new JObject { ["axis"] = c.Axis, ["match"] = c.Pattern, ["style"] = c.Style }));
            SettingsService.ReplaceUserSettings(user, layer);
        }
        return true;
    }
}
