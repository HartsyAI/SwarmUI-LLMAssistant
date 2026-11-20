using System.Text;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptStyles;

/// <summary>One condition of a style's <c>matches:</c> line, written <c>kind:pattern</c>: for an image or video style <c>class:</c> (core's model class id), <c>compat:</c> (its compatibility class id),
/// <c>exact:</c> (the model's name) or <c>glob:</c> (a name pattern with <c>*</c> and <c>?</c>); for a chat-model style <c>exact:</c>, <c>glob:</c>, <c>family:</c>, <c>provider:</c> or <c>tag:</c>.</summary>
/// <param name="Kind">One of <see cref="PromptStyle.MatchKindsFor"/>.</param>
/// <param name="Pattern">What to compare with (case-insensitive).</param>
public sealed record StyleMatch(string Kind, string Pattern);

/// <summary>A prompt style: written instructions for one model, kept as a markdown file. An <b>image</b> or <b>video</b> style says how to write a prompt for the image or video model it matches (its format,
/// layout rules and examples); a <b>llm</b> style is a short addendum on how to instruct the chat model it matches. Which style applies to which model is decided by <see cref="PromptStyleResolver"/>.
/// <para>Nothing here is run: a style is text for the model that writes a prompt, so it is never listed to the model as a skill.</para></summary>
/// <param name="Id">The file name without <c>.md</c>; a name in a more specific scope replaces a style of the same id.</param>
/// <param name="Kind"><see cref="KindImage"/>, <see cref="KindVideo"/> or <see cref="KindLlm"/>.</param>
/// <param name="Name">The name shown to people.</param>
/// <param name="Description">One line saying what it is for.</param>
/// <param name="Matches">The models it is for. Empty makes it the default for its kind, used when nothing more specific matches.</param>
/// <param name="Format">What the finished prompt looks like: <see cref="FormatText"/>, <see cref="FormatTags"/> or <see cref="FormatJson"/>.</param>
/// <param name="Formatter">The name of a built-in normalizer that fixes the model's output into the exact shape the model needs (empty for none).</param>
/// <param name="Negative">Whether the model takes a negative prompt: <see cref="NegativeNone"/>, <see cref="NegativeSeparate"/> or <see cref="NegativeInline"/>.</param>
/// <param name="Supports">What the model honours, from <see cref="KnownSupports"/>; the writer is told to use only these.</param>
/// <param name="Tasks">For a chat-model style, the tasks it applies to (empty for all of them).</param>
/// <param name="Guide">The path of the researched prompting guide this style was written from, for people to open (empty for none).</param>
/// <param name="Body">The instructions, the text after the front matter.</param>
/// <param name="Scope">Where it came from: <see cref="PromptAsset.ScopeBundled"/>, <see cref="PromptAsset.ScopeShared"/> or <see cref="PromptAsset.ScopePersonal"/>.</param>
public sealed record PromptStyle(string Id, string Kind, string Name, string Description, List<StyleMatch> Matches, string Format, string Formatter, string Negative,
    List<string> Supports, List<string> Tasks, string Guide, string Body, string Scope)
{
    /// <summary>The kinds of style.</summary>
    public const string KindImage = "image", KindVideo = "video", KindLlm = "llm";

    /// <summary>The prompt formats.</summary>
    public const string FormatText = "text", FormatTags = "tags", FormatJson = "json";

    /// <summary>How a model takes a negative prompt.</summary>
    public const string NegativeNone = "none", NegativeSeparate = "separate", NegativeInline = "inline";

    /// <summary>The things a style can say its model honours.</summary>
    public static readonly string[] KnownSupports = ["weights", "break", "regions", "boxes", "text-rendering"];

    /// <summary>The tasks a chat-model style can be limited to.</summary>
    public static readonly string[] KnownTasks = ["chat", "tools", "vision", "caption", "rewrite"];

    /// <summary>The most conditions in one style, the longest pattern, the longest name and description, and the most overrides a user can keep.</summary>
    public const int MaxMatches = 20, MaxPatternChars = 200, MaxNameChars = 80, MaxDescriptionChars = 300, MaxOverrides = 200;

    /// <summary>Whether this style is for a prompt written for an image or video model (rather than for a chat model).</summary>
    public bool IsTargetStyle => Kind is KindImage or KindVideo;

    /// <summary>The match kinds a style of <paramref name="kind"/> may use.</summary>
    public static string[] MatchKindsFor(string kind) => kind == KindLlm ? ["exact", "glob", "family", "provider", "tag"] : ["exact", "glob", "class", "compat"];

    /// <summary>Whether <paramref name="kind"/> is one of the three kinds.</summary>
    public static bool IsValidKind(string kind) => kind is KindImage or KindVideo or KindLlm;

    /// <summary>Whether <paramref name="id"/> can name a style (the same rule as a skill's name).</summary>
    public static bool IsValidId(string id) => PromptAsset.IsValidName(id);

    /// <summary>How much a scope outranks another when two styles tie: personal over shared over bundled.</summary>
    public static int ScopeRank(string scope) => scope switch { PromptAsset.ScopePersonal => 3, PromptAsset.ScopeShared => 2, _ => 1 };
}

/// <summary>Reads and writes a style's file: the <c>---</c> front matter of <see cref="PromptAssetFile"/> with the keys of <see cref="PromptStyle"/>.</summary>
public static class PromptStyleFile
{
    /// <summary>Reads a style from the text of its file, or null when it cannot be one (an unknown <c>kind</c>). Anything else wrong is made safe rather than refused: a missing kind is
    /// <c>image</c>, an unknown format is <c>text</c>, and conditions or support names that do not apply are dropped.</summary>
    public static PromptStyle Parse(string id, string scope, string text)
    {
        (Dictionary<string, string> meta, string body) = PromptAssetFile.Parse(text);
        string kind = (meta.GetValueOrDefault("kind") ?? PromptStyle.KindImage).Trim().ToLowerInvariant();
        if (!PromptStyle.IsValidKind(kind))
        {
            return null;
        }
        string format = (meta.GetValueOrDefault("format") ?? "").Trim().ToLowerInvariant();
        if (format is not (PromptStyle.FormatText or PromptStyle.FormatTags or PromptStyle.FormatJson))
        {
            format = PromptStyle.FormatText;
        }
        string negative = (meta.GetValueOrDefault("negative") ?? "").Trim().ToLowerInvariant();
        if (negative is not (PromptStyle.NegativeNone or PromptStyle.NegativeSeparate or PromptStyle.NegativeInline))
        {
            negative = PromptStyle.NegativeNone;
        }
        string name = (meta.GetValueOrDefault("name") ?? "").Trim();
        return new PromptStyle(id, kind, name.Length == 0 ? id : name, (meta.GetValueOrDefault("description") ?? "").Trim(), ParseMatches(meta.GetValueOrDefault("matches"), kind),
            format, (meta.GetValueOrDefault("formatter") ?? "").Trim().ToLowerInvariant(), negative,
            [.. SplitList(meta.GetValueOrDefault("supports")).Where(s => PromptStyle.KnownSupports.Contains(s))],
            [.. SplitList(meta.GetValueOrDefault("tasks")).Where(s => PromptStyle.KnownTasks.Contains(s))],
            (meta.GetValueOrDefault("guide") ?? "").Trim(), body, scope);
    }

    /// <summary>The conditions in a <c>matches:</c> value (<c>class:ideogram-4, glob:*pony*</c>), keeping those whose kind applies to <paramref name="styleKind"/>. Each is split at its first
    /// colon, so a pattern may contain colons (<c>glob:llama3:*</c>) but not commas.</summary>
    public static List<StyleMatch> ParseMatches(string value, string styleKind)
    {
        List<StyleMatch> matches = [];
        string[] allowed = PromptStyle.MatchKindsFor(styleKind);
        foreach (string token in SplitList(value, lowercase: false))
        {
            int colon = token.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            string kind = token[..colon].Trim().ToLowerInvariant();
            string pattern = token[(colon + 1)..].Trim();
            if (allowed.Contains(kind) && pattern.Length > 0 && pattern.Length <= PromptStyle.MaxPatternChars && matches.Count < PromptStyle.MaxMatches)
            {
                matches.Add(new StyleMatch(kind, pattern));
            }
        }
        return matches;
    }

    /// <summary>The comma-separated items of a front matter value, trimmed, without empties.</summary>
    private static IEnumerable<string> SplitList(string value, bool lowercase = true)
        => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => lowercase ? s.ToLowerInvariant() : s);

    /// <summary>The text of a style's file: front matter, then the body. Values are put on one line, so they cannot break out of the front matter.</summary>
    public static string Format(PromptStyle style)
    {
        StringBuilder sb = new();
        sb.Append("---\n");
        sb.Append("name: ").Append(PromptAssetFile.OneLine(style.Name)).Append('\n');
        sb.Append("description: ").Append(PromptAssetFile.OneLine(style.Description)).Append('\n');
        sb.Append("kind: ").Append(style.Kind).Append('\n');
        if (style.Matches.Count > 0)
        {
            sb.Append("matches: ").Append(PromptAssetFile.OneLine(string.Join(", ", style.Matches.Select(m => $"{m.Kind}:{m.Pattern}")))).Append('\n');
        }
        sb.Append("format: ").Append(style.Format).Append('\n');
        if (style.Formatter.Length > 0)
        {
            sb.Append("formatter: ").Append(PromptAssetFile.OneLine(style.Formatter)).Append('\n');
        }
        sb.Append("negative: ").Append(style.Negative).Append('\n');
        if (style.Supports.Count > 0)
        {
            sb.Append("supports: ").Append(string.Join(", ", style.Supports)).Append('\n');
        }
        if (style.Tasks.Count > 0)
        {
            sb.Append("tasks: ").Append(string.Join(", ", style.Tasks)).Append('\n');
        }
        if (style.Guide.Length > 0)
        {
            sb.Append("guide: ").Append(PromptAssetFile.OneLine(style.Guide)).Append('\n');
        }
        sb.Append("---\n\n").Append((style.Body ?? "").Replace("\r\n", "\n").Trim()).Append('\n');
        return sb.ToString();
    }
}
