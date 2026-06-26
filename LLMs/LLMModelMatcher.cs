using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>How a <see cref="LLMModelMatcher"/> compares against a model. The kind also
/// determines specificity: more specific kinds beat less specific ones when several match.</summary>
public enum ModelMatchKind
{
    /// <summary>Catch-all. Matches every model. Lowest specificity. Used for default variants.</summary>
    Default,
    /// <summary>Glob pattern against the model id (eg <c>*sonnet*</c>, <c>llama-3.*</c>).</summary>
    Glob,
    /// <summary>Tag-based — matches if the model declares the given tag in its metadata
    /// (eg <c>vision</c>, <c>tool-use</c>, <c>local</c>). Provider-specific.</summary>
    Tag,
    /// <summary>Provider/backend-type match (eg <c>anthropic</c>, <c>llamasharp</c>, <c>ollama</c>).</summary>
    Provider,
    /// <summary>Family/architecture match (eg <c>llama</c>, <c>claude</c>, <c>qwen</c>).</summary>
    Family,
    /// <summary>Exact id match against <see cref="LLMModelInfo.Id"/>.</summary>
    Exact
}

/// <summary>Generic, reusable matcher that decides whether a given LLM model satisfies a pattern.
/// Used by per-model instruction variants but designed to be reused by any future feature that
/// needs "if model is X, do Y" routing.
///
/// <para>Specificity ordering (highest wins): <c>Exact &gt; Family &gt; Provider &gt; Tag &gt; Glob &gt; Default</c>.</para>
///
/// <para>Graceful degradation: when only a model id string is available (not a full
/// <see cref="LLMModelInfo"/>), <c>Family</c>/<c>Provider</c>/<c>Tag</c> matchers silently don't
/// match, while <c>Exact</c>/<c>Glob</c>/<c>Default</c> still work — so callers don't need to
/// resolve full model facts just to do basic matching.</para></summary>
public class LLMModelMatcher
{
    /// <summary>The raw pattern string (semantics depend on <see cref="Kind"/>).</summary>
    public string Pattern { get; set; } = "";

    /// <summary>How <see cref="Pattern"/> is interpreted.</summary>
    public ModelMatchKind Kind { get; set; } = ModelMatchKind.Default;

    /// <summary>Higher = more specific. Used to pick a winner when multiple matchers match the
    /// same model. The numeric values themselves are an implementation detail; only the relative
    /// ordering matters.</summary>
    public int Specificity => Kind switch
    {
        ModelMatchKind.Exact => 100,
        ModelMatchKind.Family => 75,
        ModelMatchKind.Provider => 60,
        ModelMatchKind.Tag => 40,
        ModelMatchKind.Glob => 20,
        _ => 0
    };

    /// <summary>True if this matcher applies to the given model. Tolerates a null
    /// <see cref="LLMModelInfo"/> by only checking id-shaped kinds.</summary>
    public bool Matches(LLMModelInfo model)
    {
        if (Kind == ModelMatchKind.Default)
        {
            return true;
        }
        string id = model?.Id ?? "";
        switch (Kind)
        {
            case ModelMatchKind.Exact:
                return string.Equals(id, Pattern, StringComparison.OrdinalIgnoreCase);
            case ModelMatchKind.Glob:
                return GlobMatches(Pattern, id);
            case ModelMatchKind.Family:
                return model is not null && string.Equals(model.Family ?? "", Pattern, StringComparison.OrdinalIgnoreCase);
            case ModelMatchKind.Provider:
                return model is not null && string.Equals(model.Provider ?? "", Pattern, StringComparison.OrdinalIgnoreCase);
            case ModelMatchKind.Tag:
                if (model?.Metadata is null)
                {
                    return false;
                }
                // Tags can be expressed as a comma-separated "tags" metadata value, or as
                // individual `tag:<name>` keys with truthy values.
                if (model.Metadata.TryGetValue("tags", out string tagList))
                {
                    foreach (string tag in tagList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (string.Equals(tag, Pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                if (model.Metadata.TryGetValue("tag:" + Pattern, out string val))
                {
                    return !string.IsNullOrEmpty(val) && val != "false" && val != "0";
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>Convert a glob pattern (<c>*</c>, <c>?</c>) to a case-insensitive regex match.</summary>
    private static bool GlobMatches(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input ?? "", regex, RegexOptions.IgnoreCase);
    }

    /// <summary>Picks the value whose matcher matches the given model with the highest
    /// specificity. Ties broken by order (first-declared wins). Returns <paramref name="defaultValue"/>
    /// if no candidate matches.</summary>
    public static T PickBest<T>(IEnumerable<(LLMModelMatcher Matcher, T Value)> candidates, LLMModelInfo model, T defaultValue = default)
    {
        if (candidates is null)
        {
            return defaultValue;
        }
        T bestValue = defaultValue;
        int bestScore = int.MinValue;
        bool anyMatch = false;
        foreach ((LLMModelMatcher matcher, T value) in candidates)
        {
            if (matcher is null || !matcher.Matches(model))
            {
                continue;
            }
            int score = matcher.Specificity;
            if (!anyMatch || score > bestScore)
            {
                bestValue = value;
                bestScore = score;
                anyMatch = true;
            }
        }
        return anyMatch ? bestValue : defaultValue;
    }

    // ---- Serialization ----

    /// <summary>Builds a matcher from a JSON object of the form
    /// <c>{ "match": "claude-*", "matchKind": "glob" }</c>. Unknown <c>matchKind</c> values
    /// fall back to <see cref="ModelMatchKind.Glob"/>.</summary>
    public static LLMModelMatcher FromJson(JObject obj)
    {
        if (obj is null)
        {
            return new LLMModelMatcher();
        }
        string raw = obj["match"]?.ToString() ?? "";
        string kindStr = obj["matchKind"]?.ToString();
        ModelMatchKind kind = ParseKind(kindStr, defaultIfEmpty: string.IsNullOrEmpty(raw) ? ModelMatchKind.Default : ModelMatchKind.Glob);
        return new LLMModelMatcher { Pattern = raw, Kind = kind };
    }

    /// <summary>Inverse of <see cref="FromJson"/>.</summary>
    public JObject ToJson()
    {
        return new JObject
        {
            ["match"] = Pattern,
            ["matchKind"] = Kind.ToString().ToLowerInvariant()
        };
    }

    /// <summary>Permissive parse of a kind name. Accepts any-case and the common short forms.</summary>
    public static ModelMatchKind ParseKind(string s, ModelMatchKind defaultIfEmpty = ModelMatchKind.Default)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return defaultIfEmpty;
        }
        return s.Trim().ToLowerInvariant() switch
        {
            "exact" => ModelMatchKind.Exact,
            "family" => ModelMatchKind.Family,
            "provider" => ModelMatchKind.Provider,
            "tag" => ModelMatchKind.Tag,
            "glob" or "wildcard" => ModelMatchKind.Glob,
            "default" or "any" or "*" => ModelMatchKind.Default,
            _ => defaultIfEmpty
        };
    }
}
