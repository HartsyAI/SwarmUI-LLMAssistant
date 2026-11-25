using System.IO;
using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;

/// <summary>What a formatter made of the rewriting model's answer: the clean text (with the tag placeholders still in it), anything worth telling the user, or the reason the answer cannot be used.</summary>
/// <param name="Text">The normalized prompt, or null when <paramref name="Error"/> is set.</param>
/// <param name="Warnings">Things that were fixed or dropped on the way (a box out of range, a color written in lowercase).</param>
/// <param name="Error">Why the answer cannot be used, written so the model can fix it on a retry; null when it is usable.</param>
public sealed record FormatOutcome(string Text, List<string> Warnings, string Error)
{
    /// <summary>A usable answer.</summary>
    public static FormatOutcome Ok(string text, List<string> warnings = null) => new(text, warnings ?? [], null);

    /// <summary>An answer that cannot be used.</summary>
    public static FormatOutcome Fail(string error) => new(null, [], error);
}

/// <summary>Turns the rewriting model's raw answer into the form one model family wants, checking it in code rather than trusting the model: this is what makes a JSON-only model safe to feed.</summary>
public interface IPromptFormatter
{
    /// <summary>The id a style names in its <c>formatter</c> key.</summary>
    string Id { get; }

    /// <summary>Whether the answer is a JSON object (so a restored tag must be escaped for a JSON string, and the writer is told to answer with JSON only). Decided by the formatter, not the style's <c>format</c> key,
    /// so the two cannot disagree.</summary>
    bool ProducesJson { get; }

    /// <summary>Normalizes <paramref name="raw"/> (the answer with tag placeholders in it) for <paramref name="style"/>.</summary>
    FormatOutcome Normalize(string raw, PromptStyle style);

    /// <summary>The user's own wording put into this form, used when the model failed twice: never bare text for a model that cannot read it. Null when the original is already fine as it is.</summary>
    string Wrap(string original);

    /// <summary>Like <see cref="Normalize(string, PromptStyle)"/> for a formatter that needs the image size (a layout in pixels). A size of 0 means unknown. Most formatters do not care.</summary>
    FormatOutcome Normalize(string raw, PromptStyle style, int width, int height) => Normalize(raw, style);

    /// <summary>Whether a tag placeholder in <paramref name="text"/> (the text <see cref="Normalize(string, PromptStyle)"/> made) lands inside a JSON string, so the tag must be escaped for one. Formatters
    /// whose answer is sometimes JSON and sometimes plain text decide per answer; the default is <see cref="ProducesJson"/>.</summary>
    bool EscapesTags(string text) => ProducesJson;

    /// <summary>Whether <paramref name="tag"/> (written with its angle brackets) is part of the format itself, not a Swarm tag, in <paramref name="text"/>: Flux.3 names each layout element in its caption
    /// as <c>&lt;person_1&gt;</c>. Such a tag is let through the check that the model invented no Swarm tag. The default is no.</summary>
    bool IsOwnTag(string tag, string text) => false;
}

/// <summary>The formatters, found by a style's <c>formatter</c> key and otherwise by its <c>format</c> (text, tags, json).</summary>
public static class PromptFormatters
{
    /// <summary>Every built-in formatter by id.</summary>
    private static readonly Dictionary<string, IPromptFormatter> ById = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = new TextFormatter(),
        ["tags"] = new TagsFormatter(),
        ["json"] = new JsonFormatter(),
        ["ideogram-4"] = new Ideogram4Formatter(),
        ["flux-3"] = new Flux3Formatter(),
        ["fibo-2"] = new Fibo2Formatter(),
        ["ming-image"] = new MingImageFormatter(),
        ["moss-image"] = new MossImageFormatter(),
    };

    /// <summary>The formatter for <paramref name="style"/>: the one it names, else the one for its format. A named formatter that is not built in falls back to the format's, so a shared style cannot break a rewrite.</summary>
    public static IPromptFormatter For(PromptStyle style)
    {
        if (!string.IsNullOrWhiteSpace(style.Formatter) && ById.TryGetValue(style.Formatter.Trim(), out IPromptFormatter named))
        {
            return named;
        }
        return ById.TryGetValue(style.Format ?? PromptStyle.FormatText, out IPromptFormatter byFormat) ? byFormat : ById["text"];
    }

    /// <summary>Whether <paramref name="id"/> names a built-in formatter.</summary>
    public static bool Exists(string id) => !string.IsNullOrWhiteSpace(id) && ById.ContainsKey(id.Trim());

    /// <summary>Removes a reasoning block, a code fence and a lead-in line the model put around its answer, and trims it. What is left is the answer itself.</summary>
    public static string StripWrapping(string raw)
    {
        ThinkSpanFilter think = new();
        string text = (think.Feed(raw) + think.Flush()).Trim();
        Match fence = Regex.Match(text, @"^```[A-Za-z0-9_-]*\s*\n(.*?)\n?```\s*$", RegexOptions.Singleline);
        if (fence.Success)
        {
            text = fence.Groups[1].Value.Trim();
        }
        return text;
    }
}

/// <summary>Plain-language prompts: strips wrapping, a lead-in line ("Here is the prompt:") and surrounding quotes, and refuses an empty or reasoning-only answer.</summary>
public sealed class TextFormatter : IPromptFormatter
{
    /// <inheritdoc/>
    public string Id => "text";

    /// <inheritdoc/>
    public bool ProducesJson => false;

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style)
    {
        string text = PromptFormatters.StripWrapping(raw);
        string[] lines = text.Split('\n');
        if (lines.Length > 1 && Regex.IsMatch(lines[0].Trim(), @"^(here( is|'s)|sure|certainly|okay|ok)\b.*:\s*$", RegexOptions.IgnoreCase))
        {
            text = string.Join('\n', lines.Skip(1)).Trim();
        }
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && !text[1..^1].Contains('"'))
        {
            text = text[1..^1].Trim();
        }
        return text.Length == 0 ? FormatOutcome.Fail("it was empty (a reply that is only reasoning counts as empty)") : FormatOutcome.Ok(text);
    }

    /// <inheritdoc/>
    public string Wrap(string original) => null;
}

/// <summary>Tag-style prompts (comma separated, optional weights): the text rules plus a check that brackets balance and a warning for syntax the model does not use.</summary>
public sealed class TagsFormatter : IPromptFormatter
{
    /// <inheritdoc/>
    public string Id => "tags";

    /// <inheritdoc/>
    public bool ProducesJson => false;

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style)
    {
        FormatOutcome basic = new TextFormatter().Normalize(raw, style);
        if (basic.Error is not null)
        {
            return basic;
        }
        string text = Regex.Replace(basic.Text, @",?\s*\n\s*", ", ").Trim().TrimEnd(',').Trim();
        if (Count(text, '(') != Count(text, ')'))
        {
            return FormatOutcome.Fail("its round brackets are not balanced, so a weight like (word:1.2) is broken");
        }
        if (Count(text, '[') != Count(text, ']'))
        {
            return FormatOutcome.Fail("its square brackets are not balanced");
        }
        List<string> warnings = [];
        bool weights = style.Supports.Contains("weights", StringComparer.OrdinalIgnoreCase);
        if (!weights && Regex.IsMatch(text, @"\([^()]*:\s*-?\d+(\.\d+)?\)"))
        {
            warnings.Add("The prompt has weights like (word:1.2), which this model's style does not list as supported.");
        }
        if (!style.Supports.Contains("break", StringComparer.OrdinalIgnoreCase) && Regex.IsMatch(text, @"\bBREAK\b"))
        {
            warnings.Add("The prompt has BREAK, which this model's style does not list as supported.");
        }
        return text.Length == 0 ? FormatOutcome.Fail("it was empty") : FormatOutcome.Ok(text, warnings);
    }

    /// <inheritdoc/>
    public string Wrap(string original) => null;

    /// <summary>How many times <paramref name="c"/> appears in <paramref name="text"/>.</summary>
    private static int Count(string text, char c) => text.Count(x => x == c);
}

/// <summary>A style that wants JSON but names no formatter of its own: the answer must be one JSON object, and is written back compact. No keys are checked, only that it parses. The schema is unknown, so a failed
/// rewrite is never wrapped (the user's wording is left as it was, with a warning) rather than put under a key the model may not understand.</summary>
public sealed class JsonFormatter : IPromptFormatter
{
    /// <inheritdoc/>
    public string Id => "json";

    /// <inheritdoc/>
    public bool ProducesJson => true;

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style)
    {
        string error = Ideogram4Formatter.ParseObject(raw, out JObject root);
        return error is not null ? FormatOutcome.Fail(error) : FormatOutcome.Ok(root.ToString(Formatting.None));
    }

    /// <inheritdoc/>
    public string Wrap(string original) => null;
}

/// <summary>Ideogram 4's structured caption: checks the shape against the guide, puts the keys in the order the model was trained on, clamps boxes to the 0 to 1000 grid, writes colors as uppercase <c>#RRGGBB</c> and writes the
/// object compact. What cannot be repaired (no composition, no description for an element, both <c>photo</c> and <c>art_style</c>) is refused so the model can try again.</summary>
public sealed class Ideogram4Formatter : IPromptFormatter
{
    /// <summary>The most colors in the style palette and in one element's.</summary>
    public const int MaxStylePalette = 16, MaxElementPalette = 5;

    /// <summary>The top of the box grid.</summary>
    public const int GridMax = 1000;

    /// <summary>The mediums the guide lists; another value is kept and noted.</summary>
    private static readonly string[] Mediums = ["photograph", "illustration", "3d_render", "painting", "graphic_design"];

    /// <inheritdoc/>
    public string Id => "ideogram-4";

    /// <inheritdoc/>
    public bool ProducesJson => true;

    /// <summary>Reads the first JSON object in <paramref name="raw"/> (after the wrapping is stripped). Returns the reason it cannot, or null.</summary>
    public static string ParseObject(string raw, out JObject root)
    {
        root = null;
        string text = PromptFormatters.StripWrapping(raw);
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return "it was not a JSON object";
        }
        try
        {
            using JsonTextReader reader = new(new StringReader(text[start..(end + 1)])) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(reader);
            // Reading on makes Newtonsoft refuse a second value or any text after the object, rather than silently ignoring it.
            reader.Read();
            return null;
        }
        catch (JsonException ex)
        {
            return $"it was not valid JSON ({ex.Message})";
        }
    }

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style)
    {
        string parseError = ParseObject(raw, out JObject root);
        if (parseError is not null)
        {
            return FormatOutcome.Fail(parseError);
        }
        List<string> warnings = [];
        JObject result = [];
        if (root["high_level_description"] is JValue { Type: JTokenType.String } description && !string.IsNullOrWhiteSpace((string)description))
        {
            result["high_level_description"] = ((string)description).Trim();
        }
        else
        {
            warnings.Add("The caption has no high_level_description, which the guide recommends.");
        }
        if (root["style_description"] is not null && root["style_description"].Type != JTokenType.Null)
        {
            if (root["style_description"] is not JObject styleIn)
            {
                return FormatOutcome.Fail("style_description must be an object");
            }
            string styleError = NormalizeStyle(styleIn, warnings, out JObject styleOut);
            if (styleError is not null)
            {
                return FormatOutcome.Fail(styleError);
            }
            result["style_description"] = styleOut;
        }
        if (root["compositional_deconstruction"] is not JObject compIn)
        {
            return FormatOutcome.Fail("compositional_deconstruction is missing or is not an object");
        }
        string compError = NormalizeComposition(compIn, warnings, out JObject compOut);
        if (compError is not null)
        {
            return FormatOutcome.Fail(compError);
        }
        result["compositional_deconstruction"] = compOut;
        foreach (JProperty extra in root.Properties().Where(p => p.Name is not ("high_level_description" or "style_description" or "compositional_deconstruction")))
        {
            warnings.Add($"Dropped the unknown top-level key \"{extra.Name}\".");
        }
        return FormatOutcome.Ok(result.ToString(Formatting.None), warnings);
    }

    /// <inheritdoc/>
    public string Wrap(string original) => new JObject
    {
        ["high_level_description"] = original,
        ["compositional_deconstruction"] = new JObject
        {
            ["background"] = original,
            ["elements"] = new JArray(new JObject { ["type"] = "obj", ["desc"] = original }),
        },
    }.ToString(Formatting.None);

    /// <summary>Checks and orders <c>style_description</c>: exactly one of photo and art_style, aesthetics, lighting and medium present, the palette last.</summary>
    private static string NormalizeStyle(JObject input, List<string> warnings, out JObject output)
    {
        output = null;
        bool photo = input["photo"] is not null && input["photo"].Type != JTokenType.Null;
        bool art = input["art_style"] is not null && input["art_style"].Type != JTokenType.Null;
        if (photo && art)
        {
            return "style_description has both photo and art_style; it must have exactly one (photo for photographs, art_style for everything else)";
        }
        if (!photo && !art)
        {
            return "style_description needs either photo (for photographs) or art_style (for everything else)";
        }
        string[] order = photo ? ["aesthetics", "lighting", "photo", "medium"] : ["aesthetics", "lighting", "medium", "art_style"];
        JObject result = [];
        foreach (string key in order)
        {
            if (input[key] is not JValue { Type: JTokenType.String } value || string.IsNullOrWhiteSpace((string)value))
            {
                return $"style_description.{key} must be a non-empty string";
            }
            result[key] = ((string)value).Trim();
        }
        if (!Mediums.Contains(((string)result["medium"]).ToLowerInvariant()))
        {
            warnings.Add($"The medium \"{(string)result["medium"]}\" is not one the guide lists ({string.Join(", ", Mediums)}).");
        }
        if (input["color_palette"] is not null && input["color_palette"].Type != JTokenType.Null)
        {
            JArray palette = NormalizePalette(input["color_palette"], MaxStylePalette, "style_description.color_palette", warnings);
            if (palette.Count > 0)
            {
                result["color_palette"] = palette;
            }
        }
        foreach (JProperty extra in input.Properties().Where(p => !order.Contains(p.Name) && p.Name != "color_palette"))
        {
            warnings.Add($"Dropped the unknown style_description key \"{extra.Name}\".");
        }
        output = result;
        return null;
    }

    /// <summary>Checks <c>compositional_deconstruction</c>: the background, then each element in its key order.</summary>
    private static string NormalizeComposition(JObject input, List<string> warnings, out JObject output)
    {
        output = null;
        if (input["background"] is not JValue { Type: JTokenType.String } background)
        {
            return "compositional_deconstruction.background must be a string";
        }
        if (input["elements"] is not JArray elements)
        {
            return "compositional_deconstruction.elements must be a list";
        }
        JArray normalized = [];
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i] is not JObject element)
            {
                return $"element {i} must be an object";
            }
            string type = element["type"]?.Type == JTokenType.String ? ((string)element["type"]).Trim().ToLowerInvariant() : null;
            if (type is not ("obj" or "text"))
            {
                return $"element {i} needs \"type\" to be \"obj\" or \"text\"";
            }
            JObject outElement = new() { ["type"] = type };
            string boxError = NormalizeBox(element["bbox"], i, warnings, out JArray box);
            if (boxError is not null)
            {
                return boxError;
            }
            if (box is not null)
            {
                outElement["bbox"] = box;
            }
            if (type == "text")
            {
                if (element["text"] is not JValue { Type: JTokenType.String } text || string.IsNullOrEmpty((string)text))
                {
                    return $"text element {i} needs \"text\", the exact words to draw";
                }
                outElement["text"] = (string)text;
            }
            if (element["desc"] is not JValue { Type: JTokenType.String } desc || string.IsNullOrWhiteSpace((string)desc))
            {
                return $"element {i} needs a non-empty \"desc\"";
            }
            outElement["desc"] = ((string)desc).Trim();
            if (element["color_palette"] is not null && element["color_palette"].Type != JTokenType.Null)
            {
                JArray palette = NormalizePalette(element["color_palette"], MaxElementPalette, $"element {i} color_palette", warnings);
                if (palette.Count > 0)
                {
                    outElement["color_palette"] = palette;
                }
            }
            normalized.Add(outElement);
        }
        if (normalized.Count == 0)
        {
            warnings.Add("The composition has no elements; whether the model accepts an empty list is not confirmed.");
        }
        output = new JObject { ["background"] = ((string)background).Trim(), ["elements"] = normalized };
        return null;
    }

    /// <summary>A box as four whole numbers on the 0 to 1000 grid in the order [y_min, x_min, y_max, x_max]. Numbers are rounded (halves away from zero) and clamped; a box that cannot be one is dropped with a warning, and a box
    /// written as 0 to 1 fractions (Swarm's own <c>&lt;bbox&gt;</c> convention) is refused, since rounding it would give a tiny box in the corner instead of the one meant.</summary>
    internal static string NormalizeBox(JToken token, int index, List<string> warnings, out JArray box)
    {
        box = null;
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }
        if (token is not JArray { Count: 4 } array || array.Any(v => v.Type is not (JTokenType.Integer or JTokenType.Float)))
        {
            warnings.Add($"Dropped the bbox of element {index}: it must be four numbers [y_min, x_min, y_max, x_max].");
            return null;
        }
        double[] raw = [.. array.Select(t => (double)t)];
        if (raw.All(x => x >= 0 && x <= 1) && raw.Any(x => x != Math.Floor(x)))
        {
            return $"the bbox of element {index} looks like 0 to 1 fractions; use whole numbers from 0 to {GridMax} as [y_min, x_min, y_max, x_max]";
        }
        int[] v = [.. raw.Select(x => (int)Math.Clamp(Math.Round(x, MidpointRounding.AwayFromZero), 0, GridMax))];
        if (v[0] >= v[2] || v[1] >= v[3])
        {
            warnings.Add($"Dropped the bbox of element {index}: y_min must be less than y_max and x_min less than x_max.");
            return null;
        }
        if (raw.Select((x, i) => Math.Round(x, MidpointRounding.AwayFromZero) != v[i]).Any(changed => changed))
        {
            warnings.Add($"The bbox of element {index} was rounded or clamped to the 0 to {GridMax} grid.");
        }
        box = new JArray(v);
        return null;
    }

    /// <summary>A palette of uppercase <c>#RRGGBB</c> colors, at most <paramref name="max"/>; short <c>#RGB</c> is expanded, anything else dropped with a warning.</summary>
    internal static JArray NormalizePalette(JToken token, int max, string where, List<string> warnings)
    {
        JArray result = [];
        if (token is not JArray colors)
        {
            warnings.Add($"Dropped {where}: it must be a list of colors.");
            return result;
        }
        foreach (JToken color in colors)
        {
            string value = color.Type == JTokenType.String ? ((string)color).Trim() : null;
            if (value is not null && Regex.IsMatch(value, @"^#[0-9A-Fa-f]{3}$"))
            {
                value = "#" + string.Concat(value[1..].Select(c => new string(c, 2)));
            }
            if (value is null || !Regex.IsMatch(value, @"^#[0-9A-Fa-f]{6}$"))
            {
                warnings.Add($"Dropped a color in {where} that is not #RRGGBB.");
                continue;
            }
            if (result.Count >= max)
            {
                warnings.Add($"{where} was cut to {max} colors.");
                break;
            }
            result.Add(value.ToUpperInvariant());
        }
        return result;
    }
}
