using Hartsy.Extensions.LLMAssistant.LLMs;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptStyles;

/// <summary>What core knows about an image or video model, as far as choosing a style needs it. Built by <see cref="CoreModelFacts"/> from core's model set; a plain value here so the choice can be tested
/// without a running server.</summary>
/// <param name="Name">The model's name as core lists it (what the user picks in the Generate tab).</param>
/// <param name="ClassId">Core's model class id (for example <c>ideogram-4</c>), or null when core could not tell what the model is.</param>
/// <param name="ClassName">The class's display name.</param>
/// <param name="CompatId">The id of the compatibility class the model class belongs to.</param>
/// <param name="IsVideo">Whether the model makes video.</param>
/// <param name="Prompt">What the model file says about how it is prompted (<see cref="ModelPromptFacts"/>), or null when its header says nothing.</param>
public sealed record ImageModelFacts(string Name, string ClassId, string ClassName, string CompatId, bool IsVideo, ModelPromptFacts Prompt = null);

/// <summary>A user's rule that pins a model to a style (or to none): <paramref name="Pattern"/> is a model name, or a pattern with <c>*</c> or <c>?</c>, and <paramref name="Style"/> is a style id or
/// <see cref="Off"/>. <paramref name="Axis"/> says which kind of model it names: <see cref="AxisImage"/> (the model a prompt is written for) or <see cref="AxisLlm"/> (the chat model).</summary>
public sealed record StyleOverride(string Axis, string Pattern, string Style)
{
    /// <summary>The two axes.</summary>
    public const string AxisImage = "image", AxisLlm = "llm";

    /// <summary>The style value that turns styling off for a model.</summary>
    public const string Off = "off";

    /// <summary>Whether <paramref name="axis"/> is one of the two.</summary>
    public static bool IsValidAxis(string axis) => axis is AxisImage or AxisLlm;

    /// <summary>Whether this rule's pattern is a pattern rather than one exact name.</summary>
    public bool IsGlob => Pattern.IndexOfAny(['*', '?']) >= 0;

    /// <summary>Whether this rule applies to the model called <paramref name="name"/>.</summary>
    public bool AppliesTo(string name) => IsGlob ? LLMModelMatcher.GlobMatches(Pattern, name) : string.Equals(Pattern, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One style that matched a model, for showing why a style won.</summary>
/// <param name="Id">The style id.</param>
/// <param name="Scope">The scope it came from.</param>
/// <param name="Matched">The condition that matched (<c>class:ideogram-4</c>), or <c>default</c> for a style with none.</param>
/// <param name="Rank">How specific that condition is; the highest wins.</param>
public sealed record StyleCandidate(string Id, string Scope, string Matched, int Rank);

/// <summary>The outcome of choosing a style for a model.</summary>
/// <param name="Style">The style to use, or null when none applies (or <paramref name="Off"/>).</param>
/// <param name="Off">Whether the model is set to no style on purpose.</param>
/// <param name="Via"><c>override</c> (a rule the user made), <c>match</c> (a style's own condition), <c>default</c> (a style for every model of its kind) or <c>none</c>.</param>
/// <param name="Reason">One line saying why, for a person.</param>
/// <param name="Candidates">Every style that matched, best first.</param>
public sealed record StyleResolution(PromptStyle Style, bool Off, string Via, string Reason, List<StyleCandidate> Candidates);

/// <summary>Chooses the style for a model, for both axes: the image or video model a prompt is written for, and the chat model doing the writing. A pure function of its inputs (the styles, the user's
/// rules and the model's facts), so the same model always gets the same style and the choice is shown to the user before anything depends on it.
/// <para>Order: the user's own rule for the model (an exact name before a pattern), then the style whose condition is most specific (image axis: <c>exact</c> name, <c>glob</c>, core <c>class</c>, the <c>format</c> the model
/// file declares, core <c>compat</c> class; chat axis: <see cref="LLMModelMatcher"/>'s own order, exact, family, provider, tag, glob), then a style with no conditions (the default). Equal conditions go to the more
/// personal scope (personal, shared, bundled), then to the smaller id.</para></summary>
public static class PromptStyleResolver
{
    /// <summary>How specific each image-axis condition is.</summary>
    private static int ImageRank(string kind) => kind switch { "exact" => 100, "glob" => 80, "class" => 60, "format" => 50, "compat" => 40, _ => 0 };

    /// <summary>The style for an image or video model.</summary>
    public static StyleResolution ResolveImage(IEnumerable<PromptStyle> styles, IEnumerable<StyleOverride> overrides, ImageModelFacts model)
    {
        List<PromptStyle> targets = [.. styles.Where(s => s.IsTargetStyle)];
        string note = "";
        StyleOverride rule = BestOverride(overrides, StyleOverride.AxisImage, model?.Name);
        if (rule is not null)
        {
            StyleResolution fromRule = FromOverride(rule, targets, ref note);
            if (fromRule is not null)
            {
                return fromRule;
            }
        }
        List<(PromptStyle Style, StyleMatch Match, int Rank)> hits = [];
        foreach (PromptStyle style in targets.Where(s => s.Matches.Count > 0))
        {
            foreach (StyleMatch match in style.Matches)
            {
                if (ImageMatches(match, model))
                {
                    hits.Add((style, match, ImageRank(match.Kind)));
                }
            }
        }
        List<StyleCandidate> candidates = [.. hits.GroupBy(h => h.Style.Id).Select(g => g.OrderByDescending(h => h.Rank).First())
            .OrderByDescending(h => h.Rank).ThenByDescending(h => PromptStyle.ScopeRank(h.Style.Scope)).ThenBy(h => h.Style.Id, StringComparer.Ordinal)
            .Select(h => new StyleCandidate(h.Style.Id, h.Style.Scope, $"{h.Match.Kind}:{h.Match.Pattern}", h.Rank))];
        if (candidates.Count > 0)
        {
            PromptStyle winner = targets.First(s => s.Id == candidates[0].Id);
            return new StyleResolution(winner, false, "match", $"{note}Its condition {candidates[0].Matched} matches this model.", candidates);
        }
        string preferred = model is not null && model.IsVideo ? PromptStyle.KindVideo : PromptStyle.KindImage;
        PromptStyle fallback = targets.Where(s => s.Matches.Count == 0)
            .OrderByDescending(s => s.Kind == preferred).ThenByDescending(s => PromptStyle.ScopeRank(s.Scope)).ThenBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault();
        return fallback is null
            ? new StyleResolution(null, false, "none", $"{note}No style matches this model and there is no default.", candidates)
            : new StyleResolution(fallback, false, "default", $"{note}No style is written for this model, so the default for {fallback.Kind} models is used.",
                [new StyleCandidate(fallback.Id, fallback.Scope, "default", 0)]);
    }

    /// <summary>The style for a chat model, for <paramref name="task"/> (null or empty for any task).</summary>
    public static StyleResolution ResolveLlm(IEnumerable<PromptStyle> styles, IEnumerable<StyleOverride> overrides, LLMModelInfo model, string task = null)
    {
        List<PromptStyle> chatStyles = [.. styles.Where(s => s.Kind == PromptStyle.KindLlm)];
        string note = "";
        StyleOverride rule = BestOverride(overrides, StyleOverride.AxisLlm, model?.Id);
        if (rule is not null)
        {
            StyleResolution fromRule = FromOverride(rule, chatStyles, ref note);
            if (fromRule is not null)
            {
                return fromRule;
            }
        }
        List<(PromptStyle Style, string Matched, int Rank)> hits = [];
        foreach (PromptStyle style in chatStyles.Where(s => string.IsNullOrEmpty(task) || s.Tasks.Count == 0 || s.Tasks.Contains(task)))
        {
            if (style.Matches.Count == 0)
            {
                hits.Add((style, "default", 0));
                continue;
            }
            foreach (StyleMatch match in style.Matches)
            {
                LLMModelMatcher matcher = new() { Kind = LLMModelMatcher.ParseKind(match.Kind), Pattern = match.Pattern };
                if (matcher.Matches(model))
                {
                    hits.Add((style, $"{match.Kind}:{match.Pattern}", matcher.Specificity));
                }
            }
        }
        List<(PromptStyle Style, string Matched, int Rank)> best = [.. hits.GroupBy(h => h.Style.Id).Select(g => g.OrderByDescending(h => h.Rank).First())
            .OrderByDescending(h => h.Rank).ThenByDescending(h => PromptStyle.ScopeRank(h.Style.Scope)).ThenBy(h => h.Style.Id, StringComparer.Ordinal)];
        List<StyleCandidate> candidates = [.. best.Select(h => new StyleCandidate(h.Style.Id, h.Style.Scope, h.Matched, h.Rank))];
        if (best.Count == 0)
        {
            return new StyleResolution(null, false, "none", $"{note}No chat-model style applies to this model.", candidates);
        }
        bool isDefault = best[0].Matched == "default";
        return new StyleResolution(best[0].Style, false, isDefault ? "default" : "match",
            isDefault ? $"{note}No style is written for this model, so the default is used." : $"{note}Its condition {best[0].Matched} matches this model.", candidates);
    }

    /// <summary>Whether an image-axis condition holds for the model. A model core could not classify matches only by name.</summary>
    private static bool ImageMatches(StyleMatch match, ImageModelFacts model)
    {
        if (model is null)
        {
            return false;
        }
        return match.Kind switch
        {
            "exact" => string.Equals(match.Pattern, model.Name, StringComparison.OrdinalIgnoreCase),
            "glob" => LLMModelMatcher.GlobMatches(match.Pattern, model.Name),
            "class" => model.ClassId is not null && string.Equals(match.Pattern, model.ClassId, StringComparison.OrdinalIgnoreCase),
            "compat" => model.CompatId is not null && string.Equals(match.Pattern, model.CompatId, StringComparison.OrdinalIgnoreCase),
            "format" => model.Prompt?.Format is not null && string.Equals(match.Pattern, model.Prompt.Format, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>The user's rule for <paramref name="name"/> on one axis: an exact name before a pattern, and the first one written when equal. Null when there is none.</summary>
    private static StyleOverride BestOverride(IEnumerable<StyleOverride> overrides, string axis, string name)
    {
        if (overrides is null || string.IsNullOrEmpty(name))
        {
            return null;
        }
        return overrides.Where(o => o.Axis == axis && o.AppliesTo(name)).OrderBy(o => o.IsGlob ? 1 : 0).FirstOrDefault();
    }

    /// <summary>The outcome a rule gives, or null (with <paramref name="note"/> saying why) when it names a style that is not there, so the normal choice goes on.</summary>
    private static StyleResolution FromOverride(StyleOverride rule, List<PromptStyle> styles, ref string note)
    {
        if (string.Equals(rule.Style, StyleOverride.Off, StringComparison.OrdinalIgnoreCase))
        {
            return new StyleResolution(null, true, "override", $"Your rule for {rule.Pattern} turns styles off for it.", []);
        }
        PromptStyle style = styles.Where(s => string.Equals(s.Id, rule.Style, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => PromptStyle.ScopeRank(s.Scope)).FirstOrDefault();
        if (style is null)
        {
            note = $"Your rule for {rule.Pattern} names a style ({rule.Style}) that does not exist, so it is ignored. ";
            return null;
        }
        return new StyleResolution(style, false, "override", $"Your rule for {rule.Pattern} picks it.", [new StyleCandidate(style.Id, style.Scope, $"rule:{rule.Pattern}", int.MaxValue)]);
    }
}
