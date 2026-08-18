using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Best-effort syntactic repair for near-valid JSON, applied only as a cheap last resort when
/// <c>JObject.Parse</c> has already failed — mirrors the scope of OpenRouter's "response healing" plugin
/// (markdown-fence unwrapping, mixed-content extraction, trailing commas, unquoted keys, unbalanced
/// brackets from truncation). This is a recovery net, not a substitute for
/// <see cref="Backends.HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings.StructuredToolCalling"/>'s
/// grammar-masked generation — that prevents these errors outright for the span it covers; this patches
/// whatever slips through everywhere else (the legacy tag convention, an unopted-in OpenAI-compatible
/// endpoint, or any future free-form "give me JSON" request). Every repair is verified by actually
/// re-parsing the result — a repair that still doesn't parse is reported as unrepairable, never a
/// best-guess/partial result.</summary>
public static partial class JsonRepairService
{
    [GeneratedRegex(@"^\s*```(?:json)?\s*\n?([\s\S]*?)\n?\s*```\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FencedBlockRegex();

    /// <summary>Attempts to repair <paramref name="text"/> into parseable JSON. Returns true and sets
    /// <paramref name="result"/> only when the repaired text actually parses.</summary>
    public static bool TryRepair(string text, out JObject result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string candidate = UnwrapMarkdownFence(text);
        candidate = ExtractOutermostObject(candidate);
        candidate = NormalizeStructure(candidate);
        candidate = CloseUnbalanced(candidate);
        try
        {
            result = JObject.Parse(candidate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Unwraps a response fully wrapped in a ```/```json code fence.</summary>
    private static string UnwrapMarkdownFence(string text)
    {
        Match m = FencedBlockRegex().Match(text.Trim());
        return m.Success ? m.Groups[1].Value : text;
    }

    /// <summary>Strips prose before/after the JSON by slicing from the first <c>{</c> to the last
    /// <c>}</c> — handles "Sure, here you go: {...} let me know if that helps" without trying to
    /// understand the prose. A no-op when the text is already a bare object.</summary>
    private static string ExtractOutermostObject(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    /// <summary>Single string-aware pass that drops trailing commas before <c>}</c>/<c>]</c> and quotes
    /// bare identifier keys (<c>{name: "Eve"}</c> → <c>{"name": "Eve"}</c>). Tracks whether it's inside a
    /// quoted string so it never touches literal text that happens to look like a comma or an identifier —
    /// a blind regex over the raw text could corrupt an otherwise-valid string value elsewhere in the
    /// document just because something else in it was broken.</summary>
    private static string NormalizeStructure(string text)
    {
        System.Text.StringBuilder sb = new(text.Length);
        bool inString = false, escaped = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (escaped) { escaped = false; }
                else if (c == '\\') { escaped = true; }
                else if (c == '"') { inString = false; }
                i++;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == ',')
            {
                int j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                if (j < text.Length && (text[j] == '}' || text[j] == ']'))
                {
                    i++; // drop the trailing comma
                    continue;
                }
                sb.Append(c);
                i++;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                string ident = text[i..j];
                int k = j;
                while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
                bool isKey = k < text.Length && text[k] == ':';
                // true/false/null are valid bare JSON literals (values, not keys) — leave them alone.
                bool isLiteral = ident is "true" or "false" or "null";
                sb.Append(isKey && !isLiteral ? $"\"{ident}\"" : ident);
                i = j;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Appends closing quotes/brackets for anything still open at end-of-string — the common
    /// truncated-at-max-tokens shape. String-aware depth tracking (a <c>}</c> inside a string value must
    /// not count). If the cut point lands mid-key or mid-partial-token, the result still won't parse and
    /// <see cref="TryRepair"/> correctly reports failure rather than guessing — this only fixes the subset
    /// of truncations that land on a clean value/container boundary.</summary>
    private static string CloseUnbalanced(string text)
    {
        int braceDepth = 0, bracketDepth = 0;
        bool inString = false, escaped = false;
        foreach (char c in text)
        {
            if (inString)
            {
                if (escaped) { escaped = false; }
                else if (c == '\\') { escaped = true; }
                else if (c == '"') { inString = false; }
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': braceDepth++; break;
                case '}': braceDepth--; break;
                case '[': bracketDepth++; break;
                case ']': bracketDepth--; break;
            }
        }
        if (!inString && braceDepth <= 0 && bracketDepth <= 0)
        {
            return text;
        }
        System.Text.StringBuilder sb = new(text);
        if (inString)
        {
            sb.Append('"');
        }
        for (int i = 0; i < bracketDepth; i++) sb.Append(']');
        for (int i = 0; i < braceDepth; i++) sb.Append('}');
        return sb.ToString();
    }
}
