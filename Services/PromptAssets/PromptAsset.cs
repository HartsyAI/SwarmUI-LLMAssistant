using System.Text;
using System.Text.RegularExpressions;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptAssets;

/// <summary>One piece of reusable prompt text kept as a markdown file: a <b>skill</b> (instructions the model can load when a task matches, in the same
/// <c>skills/&lt;name&gt;/SKILL.md</c> layout other tools use) or a <b>command</b> (a prompt template the user runs by typing <c>/name</c>).</summary>
/// <param name="Kind"><see cref="PromptAsset.KindSkill"/> or <see cref="PromptAsset.KindCommand"/>.</param>
/// <param name="Name">The id: the folder name of a skill, or the file name of a command, without <c>.md</c>.</param>
/// <param name="Description">One line saying what it is for. A skill's is what the model reads to decide whether to load it.</param>
/// <param name="ArgumentHint">What a command expects after its name (<c>[file] [what to check]</c>), shown in the picker. Empty when it takes none.</param>
/// <param name="Body">The text after the front matter.</param>
/// <param name="Scope">Where it came from: <see cref="PromptAsset.ScopeBundled"/>, <see cref="PromptAsset.ScopeShared"/>, <see cref="PromptAsset.ScopePersonal"/> or <see cref="PromptAsset.ScopeWorkspace"/>.</param>
/// <param name="Directory">A skill's own folder (its other files can be read through the <c>skill</c> tool); null for a command.</param>
/// <param name="UserOnly">Set by <c>disable-model-invocation: true</c>: only the user may run it, so the model is not told about it and cannot load it.</param>
public sealed record PromptAsset(string Kind, string Name, string Description, string ArgumentHint, string Body, string Scope, string Directory, bool UserOnly)
{
    /// <summary>The kinds.</summary>
    public const string KindSkill = "skill", KindCommand = "command";

    /// <summary>The scopes, lowest to highest precedence for the first three; a workspace's assets come last and never replace another scope's.</summary>
    public const string ScopeBundled = "bundled", ScopeShared = "shared", ScopePersonal = "personal", ScopeWorkspace = "workspace";

    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 40;

    /// <summary>The largest file read, and so the most text one asset can put into a prompt.</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Whether <paramref name="name"/> can name an asset: lowercase letters, digits, dashes and underscores, starting with a letter or digit.</summary>
    public static bool IsValidName(string name) => name is not null && Regex.IsMatch(name, "^[a-z0-9][a-z0-9_-]{0,39}$");

    /// <summary>Whether <paramref name="kind"/> is a known kind.</summary>
    public static bool IsValidKind(string kind) => kind is KindSkill or KindCommand;
}

/// <summary>Reads and writes the <c>---</c> front matter of an asset file. It is a small tolerant subset of YAML: <c>key: value</c> lines (quotes around a
/// value are dropped), a value continued on following indented lines (or given as a <c>&gt;</c> or <c>|</c> block), and keys it does not know are kept and ignored.
/// A file without front matter is all body.</summary>
public static class PromptAssetFile
{
    /// <summary>Splits <paramref name="text"/> into its front matter (keys lowercased) and body.</summary>
    public static (Dictionary<string, string> Meta, string Body) Parse(string text)
    {
        Dictionary<string, string> meta = new(StringComparer.Ordinal);
        text = (text ?? "").TrimStart('﻿').Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (meta, text.Trim());
        }
        int close = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        while (close >= 0 && close + 4 < text.Length && text[close + 4] != '\n')
        {
            close = text.IndexOf("\n---", close + 1, StringComparison.Ordinal);
        }
        if (close < 0)
        {
            return (meta, text.Trim());
        }
        string[] lines = (close > 4 ? text[4..close] : "").Split('\n');
        string key = null;
        StringBuilder value = new();
        bool literal = false;
        void Flush()
        {
            if (key is not null)
            {
                meta[key] = Unquote(value.ToString().Trim());
            }
            key = null;
            value.Clear();
        }
        foreach (string line in lines)
        {
            bool continuation = line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && key is not null;
            if (continuation)
            {
                value.Append(literal ? "\n" : " ").Append(line.Trim());
                continue;
            }
            Match match = Regex.Match(line, @"^([A-Za-z][\w-]*)\s*:\s*(.*)$");
            if (!match.Success)
            {
                continue;
            }
            Flush();
            key = match.Groups[1].Value.ToLowerInvariant();
            string rest = match.Groups[2].Value.Trim();
            literal = rest.StartsWith('|');
            if (rest.StartsWith('|') || rest.StartsWith('>'))
            {
                rest = "";
            }
            value.Append(rest);
        }
        Flush();
        string body = text[(close + 4)..].Trim();
        return (meta, body);
    }

    /// <summary>The value with one pair of surrounding quotes removed.</summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    /// <summary>Whether a front matter value is true (<c>true</c>, <c>yes</c>, <c>on</c>).</summary>
    public static bool IsTrue(Dictionary<string, string> meta, string key)
        => meta.TryGetValue(key, out string value) && value.Trim().ToLowerInvariant() is "true" or "yes" or "on";

    /// <summary>The text of an asset file: front matter, then the body. Values are put on one line, so they cannot break out of the front matter.</summary>
    public static string Format(string description, string argumentHint, bool userOnly, string body)
    {
        StringBuilder sb = new();
        sb.Append("---\n");
        sb.Append("description: ").Append(OneLine(description)).Append('\n');
        if (!string.IsNullOrWhiteSpace(argumentHint))
        {
            sb.Append("argument-hint: ").Append(OneLine(argumentHint)).Append('\n');
        }
        if (userOnly)
        {
            sb.Append("disable-model-invocation: true\n");
        }
        sb.Append("---\n\n").Append((body ?? "").Replace("\r\n", "\n").Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>The value on one line, quoted when it would otherwise be read differently.</summary>
    internal static string OneLine(string value)
    {
        value = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        if (value.Length == 0)
        {
            return "\"\"";
        }
        if (value.StartsWith('"') || value.StartsWith('\'') || value.StartsWith('|') || value.StartsWith('>') || value.StartsWith("- "))
        {
            return "\"" + value.Replace("\"", "'") + "\"";
        }
        return value;
    }
}
