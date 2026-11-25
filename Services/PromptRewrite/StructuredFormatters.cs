using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;

/// <summary>Shared checks for the formatters of structured families: reading JSON values, ordering keys and keeping what the format does not know. Each family's schema comes from its guide in
/// <c>docs/prompting/</c>, which was read through summaries, so these check shape and order and refuse only what is clearly broken.</summary>
internal static class StructuredJson
{
    /// <summary>A non-empty string value of <paramref name="key"/>, trimmed, or null when it is missing, null or blank.</summary>
    public static string Text(JObject obj, string key) => obj[key] is JValue { Type: JTokenType.String } v && !string.IsNullOrWhiteSpace((string)v) ? ((string)v).Trim() : null;

    /// <summary>Whether <paramref name="key"/> is present and not null.</summary>
    public static bool Has(JObject obj, string key) => obj[key] is not null && obj[key].Type != JTokenType.Null;

    /// <summary>Whether a value is empty for a format that drops empty values: a blank string, an empty list or an empty object.</summary>
    public static bool IsEmpty(JToken token) => token is null || token.Type == JTokenType.Null
        || (token is JValue { Type: JTokenType.String } s && string.IsNullOrWhiteSpace((string)s)) || (token is JArray { Count: 0 }) || (token is JObject { Count: 0 });

    /// <summary>Copies the keys of <paramref name="input"/> named in <paramref name="order"/>, in that order, then every other key with a warning (<paramref name="where"/> names the object), dropping empty
    /// values. A scalar is kept as it is; a string is trimmed.</summary>
    public static JObject Ordered(JObject input, string[] order, string where, List<string> warnings, bool keepUnknown)
    {
        JObject result = [];
        foreach (string key in order)
        {
            if (input[key] is JToken value && !IsEmpty(value))
            {
                result[key] = value is JValue { Type: JTokenType.String } s ? ((string)s).Trim() : value.DeepClone();
            }
        }
        foreach (JProperty extra in input.Properties().Where(p => !order.Contains(p.Name)))
        {
            if (keepUnknown && !IsEmpty(extra.Value))
            {
                result[extra.Name] = extra.Value.DeepClone();
                warnings.Add($"Kept the key \"{extra.Name}\" in {where}, which the format does not list.");
            }
            else
            {
                warnings.Add($"Dropped the key \"{extra.Name}\" in {where}, which the format does not list.");
            }
        }
        return result;
    }

    /// <summary>The answer without a reasoning block, a code fence or a lead-in line ("Here is the prompt:"), whichever order they come in.</summary>
    public static string Unwrap(string raw)
    {
        string text = PromptFormatters.StripWrapping(raw);
        int newline = text.IndexOf('\n');
        if (newline > 0 && Regex.IsMatch(text[..newline].Trim(), @"^(here( is|'s)|sure|certainly|okay|ok)\b.*:\s*$", RegexOptions.IgnoreCase))
        {
            text = PromptFormatters.StripWrapping(text[(newline + 1)..]);
        }
        return text;
    }

    /// <summary>The first JSON object in <paramref name="raw"/>, as <see cref="Ideogram4Formatter.ParseObject"/> reads it.</summary>
    public static string ParseObject(string raw, out JObject root) => Ideogram4Formatter.ParseObject(raw, out root);

    /// <summary>The whole of <paramref name="text"/> as a JSON array, or null when it is not exactly one.</summary>
    public static JArray TryParseArray(string text)
    {
        try
        {
            using JsonTextReader reader = new(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
            JArray array = JArray.Load(reader);
            return reader.Read() ? null : array;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Flux.3's prompt: a plain caption, or a caption followed by one space and a JSON array of layout rows (<c>id</c>, <c>bbox</c>, <c>desc</c>) when placement matters. The caption names each row at its
/// first mention as <c>&lt;id&gt;</c>, so this formatter lets those names through the check that the model wrote no Swarm tag, and only when they match a row. Boxes are <c>[y_min, x_min, y_max, x_max]</c>
/// on a 0 to 1000 grid. Swarm tags must stay in the caption: they are put back unescaped, and a tag inside a row's text could break the JSON around it.
/// <para>Written from <c>docs/prompting/flux-3.md</c>. Core's own <c>&lt;bbox&gt;</c> output does not match this form (see the guide), so a user's bbox tag is simply kept as a placeholder.</para></summary>
public sealed class Flux3Formatter : IPromptFormatter
{
    /// <summary>A row id: a class noun with an index (<c>person_1</c>) or a language code with text and an index (<c>En_Text_1</c>). The index at the end keeps an id from ever being a Swarm tag name.</summary>
    private static readonly Regex IdShape = new(@"^[A-Za-z][A-Za-z0-9]*(_[A-Za-z0-9]+)*_[0-9]+$", RegexOptions.Compiled);

    /// <summary>A cite of a row in a caption.</summary>
    private static readonly Regex Cite = new(@"<([A-Za-z][A-Za-z0-9_]*)>", RegexOptions.Compiled);

    /// <summary>Where a layout array can start.</summary>
    private static readonly Regex ArrayStart = new(@"\[\s*\{", RegexOptions.Compiled);

    /// <inheritdoc/>
    public string Id => "flux-3";

    /// <inheritdoc/>
    public bool ProducesJson => false;

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style)
    {
        string text = StructuredJson.Unwrap(raw);
        List<Match> starts = [.. ArrayStart.Matches(text).Cast<Match>()];
        if (starts.Count == 0)
        {
            FormatOutcome plain = new TextFormatter().Normalize(text, style);
            return plain.Error is null ? FormatOutcome.Ok(Regex.Replace(plain.Text, @"\s*\n\s*", " ")) : plain;
        }
        foreach (Match start in starts)
        {
            JArray rows = StructuredJson.TryParseArray(text[start.Index..]);
            if (rows is not null)
            {
                return WithLayout(text[..start.Index], rows);
            }
        }
        return FormatOutcome.Fail("the layout after the caption is not one valid JSON array of rows");
    }

    /// <summary>Checks the rows and the caption against each other.</summary>
    private static FormatOutcome WithLayout(string captionRaw, JArray rows)
    {
        List<string> warnings = [];
        string caption = Regex.Replace(captionRaw.Trim(), @"\s*\n\s*", " ").Trim();
        if (caption.Length == 0)
        {
            return FormatOutcome.Fail("it has a layout but no caption before it; write the caption first, then a space, then the array");
        }
        if (rows.Count == 0)
        {
            return FormatOutcome.Fail("the layout array has no rows");
        }
        JArray normalized = [];
        HashSet<string> ids = [];
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JObject row)
            {
                return FormatOutcome.Fail($"layout row {i} must be an object with id, bbox and desc");
            }
            string id = StructuredJson.Text(row, "id");
            if (id is null || !IdShape.IsMatch(id))
            {
                return FormatOutcome.Fail($"layout row {i} needs an \"id\" such as person_1 or En_Text_1: a word, then an underscore and a number");
            }
            if (!ids.Add(id))
            {
                return FormatOutcome.Fail($"the id \"{id}\" is used twice; each row needs its own number");
            }
            string boxError = Ideogram4Formatter.NormalizeBox(row["bbox"], i, warnings, out JArray box);
            if (boxError is not null)
            {
                return FormatOutcome.Fail(boxError);
            }
            if (box is null)
            {
                return FormatOutcome.Fail($"layout row {i} needs a bbox [top, left, bottom, right] of whole numbers from 0 to {Ideogram4Formatter.GridMax}, with top less than bottom and left less than right");
            }
            string desc = StructuredJson.Text(row, "desc");
            if (desc is null)
            {
                return FormatOutcome.Fail($"layout row {i} needs a non-empty \"desc\"");
            }
            if (desc.Contains(SwarmTagMask.Open, StringComparison.Ordinal))
            {
                return FormatOutcome.Fail("a tag placeholder is inside a layout row; keep every placeholder in the caption, outside the array");
            }
            foreach (JProperty extra in row.Properties().Where(p => p.Name is not ("id" or "bbox" or "desc")))
            {
                warnings.Add($"Dropped the key \"{extra.Name}\" of layout row {i}.");
            }
            normalized.Add(new JObject { ["id"] = id, ["bbox"] = box, ["desc"] = desc });
        }
        HashSet<string> cited = [.. Cite.Matches(caption).Cast<Match>().Select(m => m.Groups[1].Value)];
        string unknown = cited.FirstOrDefault(c => !ids.Contains(c));
        if (unknown is not null)
        {
            return FormatOutcome.Fail($"the caption cites <{unknown}>, which is not an id in the layout; cite only layout ids");
        }
        string uncited = ids.FirstOrDefault(id => !cited.Contains(id));
        if (uncited is not null)
        {
            return FormatOutcome.Fail($"the caption never names <{uncited}>; write <{uncited}> where that element is first mentioned");
        }
        return FormatOutcome.Ok($"{caption} {normalized.ToString(Formatting.None)}", warnings);
    }

    /// <inheritdoc/>
    public string Wrap(string original) => null;

    /// <inheritdoc/>
    public bool EscapesTags(string text) => false;

    /// <inheritdoc/>
    public bool IsOwnTag(string tag, string text)
    {
        Match cite = Cite.Match(tag);
        if (!cite.Success || cite.Value.Length != tag.Length || !IdShape.IsMatch(cite.Groups[1].Value))
        {
            return false;
        }
        Match start = ArrayStart.Matches(text).Cast<Match>().FirstOrDefault(s => StructuredJson.TryParseArray(text[s.Index..]) is not null);
        return start is not null && StructuredJson.TryParseArray(text[start.Index..]).OfType<JObject>().Any(r => StructuredJson.Text(r, "id") == cite.Groups[1].Value);
    }
}

/// <summary>Fibo 2's structured caption, in the order of Diffusers' own example: <c>short_description</c>, <c>objects</c>, <c>background_setting</c>, <c>lighting</c>, <c>aesthetics</c>,
/// <c>photographic_characteristics</c>, <c>style_medium</c>, <c>context</c>, <c>artistic_style</c>, and <c>text_render</c> last. Empty values are dropped (the model's pipeline drops them too) and keys the guide does
/// not list are kept after the known ones with a warning, since the guide's schema was read through summaries. Placement is words (<c>location</c>, <c>relative_size</c>): numeric boxes are not confirmed for this model.
/// <para>A failed rewrite is not wrapped: the guide says the plain short description is one of the two short forms the model was trained on and is passed through as it is.</para></summary>
public sealed class Fibo2Formatter : IPromptFormatter
{
    private static readonly string[] Top = ["short_description", "objects", "background_setting", "lighting", "aesthetics", "photographic_characteristics", "style_medium", "context", "artistic_style", "text_render"];
    private static readonly string[] Lighting = ["conditions", "direction", "shadows"];
    private static readonly string[] Aesthetics = ["composition", "color_scheme", "mood_atmosphere"];
    private static readonly string[] Photographic = ["depth_of_field", "focus", "camera_angle", "lens_focal_length"];
    private static readonly string[] ObjectKeys = ["description", "location", "relative_size", "shape_and_color", "texture", "appearance_details", "relationship", "orientation", "number_of_objects",
        "pose", "expression", "clothing", "action", "gender", "skin_tone_and_texture"];
    private static readonly string[] TextKeys = ["text", "location", "size", "color", "font", "appearance_details"];

    /// <inheritdoc/>
    public string Id => "fibo-2";

    /// <inheritdoc/>
    public bool ProducesJson => true;

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style)
    {
        string parseError = StructuredJson.ParseObject(raw, out JObject root);
        if (parseError is not null)
        {
            return FormatOutcome.Fail(parseError);
        }
        List<string> warnings = [];
        string shortDescription = StructuredJson.Text(root, "short_description");
        if (shortDescription is null)
        {
            return FormatOutcome.Fail("short_description is missing; it must be a string summarizing the image");
        }
        JObject result = [];
        foreach (string key in Top)
        {
            JToken value = root[key];
            if (StructuredJson.IsEmpty(value))
            {
                continue;
            }
            switch (key)
            {
                case "short_description":
                    result[key] = shortDescription;
                    break;
                case "objects":
                {
                    JArray objects = Items(value, key, ObjectKeys, "description", warnings, out string error);
                    if (error is not null)
                    {
                        return FormatOutcome.Fail(error);
                    }
                    if (objects.Count > 0)
                    {
                        result[key] = objects;
                    }
                    break;
                }
                case "text_render":
                {
                    JArray texts = Items(value, key, TextKeys, "text", warnings, out string error);
                    if (error is not null)
                    {
                        return FormatOutcome.Fail(error);
                    }
                    if (texts.Count > 0)
                    {
                        result[key] = texts;
                    }
                    break;
                }
                case "lighting" or "aesthetics" or "photographic_characteristics":
                {
                    if (value is not JObject nested)
                    {
                        return FormatOutcome.Fail($"{key} must be an object");
                    }
                    string[] order = key == "lighting" ? Lighting : key == "aesthetics" ? Aesthetics : Photographic;
                    JObject ordered = StructuredJson.Ordered(nested, order, key, warnings, keepUnknown: true);
                    if (ordered.Count > 0)
                    {
                        result[key] = ordered;
                    }
                    break;
                }
                default:
                    if (value is not JValue { Type: JTokenType.String })
                    {
                        return FormatOutcome.Fail($"{key} must be a string");
                    }
                    result[key] = ((string)value).Trim();
                    break;
            }
        }
        foreach (JProperty extra in root.Properties().Where(p => !Top.Contains(p.Name) && !StructuredJson.IsEmpty(p.Value)))
        {
            result[extra.Name] = extra.Value.DeepClone();
            warnings.Add($"Kept the top-level key \"{extra.Name}\", which the guide does not list.");
        }
        return FormatOutcome.Ok(result.ToString(Formatting.None), warnings);
    }

    /// <summary>A list of objects with <paramref name="order"/> as their key order and <paramref name="required"/> as the one key each must have.</summary>
    private static JArray Items(JToken value, string name, string[] order, string required, List<string> warnings, out string error)
    {
        error = null;
        JArray result = [];
        if (value is not JArray list)
        {
            error = $"{name} must be a list of objects";
            return result;
        }
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not JObject item)
            {
                error = $"{name}[{i}] must be an object";
                return result;
            }
            if (StructuredJson.Text(item, required) is null)
            {
                error = $"{name}[{i}] needs a non-empty \"{required}\"";
                return result;
            }
            result.Add(StructuredJson.Ordered(item, order, $"{name}[{i}]", warnings, keepUnknown: true));
        }
        return result;
    }

    /// <inheritdoc/>
    public string Wrap(string original) => null;
}

/// <summary>Ming Image's layered caption: <c>canvas_settings</c> (<c>aspect_ratio</c>, <c>ambient_lighting</c>, <c>image_style</c>) then <c>layers</c>, background first, each with <c>description</c>,
/// <c>coordinates</c>, <c>hierarchy_and_relation</c> and <c>color_specs</c>. Coordinates are one string, <c>cx: 0.500, cy: 0.500, w: 1.000, h: 1.000</c>, written here with three decimals whatever the machine's
/// culture; a box that sticks out of the canvas is moved back in. Ming also takes raw text, so a failed rewrite is not wrapped.
/// <para>Written from <c>docs/prompting/ming-image.md</c>.</para></summary>
public sealed class MingImageFormatter : IPromptFormatter
{
    private static readonly Regex Coordinates = new(
        @"^\s*cx\s*:\s*(?<cx>-?\d+(\.\d+)?)\s*,\s*cy\s*:\s*(?<cy>-?\d+(\.\d+)?)\s*,\s*w\s*:\s*(?<w>-?\d+(\.\d+)?)\s*,\s*h\s*:\s*(?<h>-?\d+(\.\d+)?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The most colors kept for one layer.</summary>
    public const int MaxLayerColors = 16;

    /// <inheritdoc/>
    public string Id => "ming-image";

    /// <inheritdoc/>
    public bool ProducesJson => true;

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style)
    {
        string parseError = StructuredJson.ParseObject(raw, out JObject root);
        if (parseError is not null)
        {
            return FormatOutcome.Fail(parseError);
        }
        List<string> warnings = [];
        if (root["canvas_settings"] is not JObject canvas)
        {
            return FormatOutcome.Fail("canvas_settings is missing or is not an object with aspect_ratio, ambient_lighting and image_style");
        }
        JObject canvasOut = [];
        foreach (string key in new[] { "aspect_ratio", "ambient_lighting", "image_style" })
        {
            string value = StructuredJson.Text(canvas, key);
            if (value is null)
            {
                return FormatOutcome.Fail($"canvas_settings.{key} must be a non-empty string");
            }
            canvasOut[key] = value;
        }
        foreach (JProperty extra in canvas.Properties().Where(p => p.Name is not ("aspect_ratio" or "ambient_lighting" or "image_style")))
        {
            warnings.Add($"Dropped the unknown canvas_settings key \"{extra.Name}\".");
        }
        if (root["layers"] is not JArray { Count: > 0 } layers)
        {
            return FormatOutcome.Fail("layers must be a non-empty list, background first");
        }
        JArray layersOut = [];
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i] is not JObject layer)
            {
                return FormatOutcome.Fail($"layer {i} must be an object");
            }
            string description = StructuredJson.Text(layer, "description");
            if (description is null)
            {
                return FormatOutcome.Fail($"layer {i} needs a non-empty \"description\"");
            }
            string coordsError = NormalizeCoordinates(layer["coordinates"], i, warnings, out string coordinates);
            if (coordsError is not null)
            {
                return FormatOutcome.Fail(coordsError);
            }
            JObject outLayer = new()
            {
                ["description"] = description,
                ["coordinates"] = coordinates,
                ["hierarchy_and_relation"] = layer["hierarchy_and_relation"] is JValue { Type: JTokenType.String } rel ? ((string)rel).Trim() : "",
                ["color_specs"] = StructuredJson.Has(layer, "color_specs") ? Ideogram4Formatter.NormalizePalette(layer["color_specs"], MaxLayerColors, $"layer {i} color_specs", warnings) : new JArray(),
            };
            foreach (JProperty extra in layer.Properties().Where(p => p.Name is not ("description" or "coordinates" or "hierarchy_and_relation" or "color_specs")))
            {
                warnings.Add($"Dropped the unknown key \"{extra.Name}\" of layer {i}.");
            }
            layersOut.Add(outLayer);
        }
        foreach (JProperty extra in root.Properties().Where(p => p.Name is not ("canvas_settings" or "layers")))
        {
            warnings.Add($"Dropped the unknown top-level key \"{extra.Name}\".");
        }
        return FormatOutcome.Ok(new JObject { ["canvas_settings"] = canvasOut, ["layers"] = layersOut }.ToString(Formatting.None), warnings);
    }

    /// <summary>Reads a layer's coordinates (the string form, or an object with cx, cy, w and h) and writes them back in the string form, inside the canvas.</summary>
    private static string NormalizeCoordinates(JToken token, int index, List<string> warnings, out string text)
    {
        text = null;
        double cx, cy, w, h;
        if (token is JValue { Type: JTokenType.String } s)
        {
            Match m = Coordinates.Match((string)s);
            if (!m.Success)
            {
                return $"layer {index} coordinates must be one string like \"cx: 0.500, cy: 0.500, w: 1.000, h: 1.000\"";
            }
            cx = Parse(m.Groups["cx"]);
            cy = Parse(m.Groups["cy"]);
            w = Parse(m.Groups["w"]);
            h = Parse(m.Groups["h"]);
        }
        else if (token is JObject o && new[] { "cx", "cy", "w", "h" }.All(k => o[k] is JValue { Type: JTokenType.Integer or JTokenType.Float }))
        {
            (cx, cy, w, h) = ((double)o["cx"], (double)o["cy"], (double)o["w"], (double)o["h"]);
            warnings.Add($"The coordinates of layer {index} were an object; they were written as the string form the model reads.");
        }
        else
        {
            return $"layer {index} coordinates must be one string like \"cx: 0.500, cy: 0.500, w: 1.000, h: 1.000\"";
        }
        if (w <= 0 || h <= 0)
        {
            return $"layer {index} coordinates need w and h above 0";
        }
        if (new[] { cx, cy, w, h }.Any(v => v > 1.5))
        {
            return $"layer {index} coordinates look like pixels; use fractions of the canvas from 0 to 1";
        }
        double w2 = Math.Min(w, 1), h2 = Math.Min(h, 1);
        double cx2 = Math.Clamp(cx, w2 / 2, 1 - w2 / 2), cy2 = Math.Clamp(cy, h2 / 2, 1 - h2 / 2);
        if (Math.Abs(w2 - w) > 0.0005 || Math.Abs(h2 - h) > 0.0005 || Math.Abs(cx2 - cx) > 0.0005 || Math.Abs(cy2 - cy) > 0.0005)
        {
            warnings.Add($"The box of layer {index} stuck out of the canvas and was moved inside it.");
        }
        text = string.Create(CultureInfo.InvariantCulture, $"cx: {cx2:F3}, cy: {cy2:F3}, w: {w2:F3}, h: {h2:F3}");
        return null;
    }

    private static double Parse(Group group) => double.Parse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public string Wrap(string original) => null;
}

/// <summary>MOSS Image's prompt: a plain caption, or a layout. The writing model drafts the layout in absolute pixels (<c>width</c>, <c>height</c>, <c>bbox_coord_space</c> <c>abs</c>, <c>bbox_format</c> <c>xyxy</c>,
/// <c>summary</c>, <c>items</c>), and this formatter compiles it the way the official compiler does, so the model never has to centre coordinates by hand: boxes become <c>[y1, x1, y2, x2]</c> minus half the
/// canvas (whole-number division), with <c>bbox_coord_space</c> <c>abs_centralized</c> and <c>bbox_format</c> <c>yxyx</c>. Like the official compiler it refuses a box outside the canvas rather than clipping it,
/// and a canvas that is not a multiple of 16 or not the image's size. A plain caption is also a valid prompt, so a failed rewrite is not wrapped.
/// <para>Written from <c>docs/prompting/moss-image.md</c>; the compile math matches the guide's worked example.</para></summary>
public sealed class MossImageFormatter : IPromptFormatter
{
    private static readonly string[] Labels = ["object", "character", "text"];
    private static readonly string[] Canvases = ["single", "multi_panel", "collage"];

    /// <inheritdoc/>
    public string Id => "moss-image";

    /// <inheritdoc/>
    public bool ProducesJson => false;

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style) => Normalize(raw, style, 0, 0);

    /// <inheritdoc/>
    public FormatOutcome Normalize(string raw, PromptStyle style, int width, int height)
    {
        string text = StructuredJson.Unwrap(raw);
        if (!text.StartsWith('{'))
        {
            return new TextFormatter().Normalize(text, style);
        }
        string parseError = StructuredJson.ParseObject(raw, out JObject draft);
        if (parseError is not null)
        {
            return FormatOutcome.Fail(parseError);
        }
        List<string> warnings = [];
        int w = draft["width"]?.Type == JTokenType.Integer ? (int)draft["width"] : 0;
        int h = draft["height"]?.Type == JTokenType.Integer ? (int)draft["height"] : 0;
        if (w <= 0 || h <= 0 || w % 16 != 0 || h % 16 != 0)
        {
            return FormatOutcome.Fail("width and height must be positive whole numbers that are multiples of 16, equal to the image size");
        }
        if (width > 0 && height > 0 && (w != width || h != height))
        {
            return FormatOutcome.Fail($"width and height must be the image size, {width} and {height}");
        }
        if (StructuredJson.Has(draft, "bbox_coord_space") && !string.Equals((string)draft["bbox_coord_space"], "abs", StringComparison.Ordinal))
        {
            return FormatOutcome.Fail("bbox_coord_space must be \"abs\": write boxes in absolute pixels");
        }
        if (StructuredJson.Has(draft, "bbox_format") && !string.Equals((string)draft["bbox_format"], "xyxy", StringComparison.Ordinal))
        {
            return FormatOutcome.Fail("bbox_format must be \"xyxy\": write boxes as [x_min, y_min, x_max, y_max]");
        }
        if (draft["summary"] is not JObject summary || StructuredJson.Text(summary, "caption") is null)
        {
            return FormatOutcome.Fail("summary must be an object with a caption describing the whole image");
        }
        JObject summaryOut = new() { ["caption"] = StructuredJson.Text(summary, "caption") };
        if (summary["meta"] is JArray meta && meta.All(m => m.Type == JTokenType.String) && meta.Count > 0)
        {
            summaryOut["meta"] = meta.DeepClone();
        }
        if (StructuredJson.Text(summary, "canvas") is string canvas)
        {
            if (Canvases.Contains(canvas))
            {
                summaryOut["canvas"] = canvas;
            }
            else
            {
                warnings.Add($"Dropped the canvas \"{canvas}\"; it is one of {string.Join(", ", Canvases)}.");
            }
        }
        if (summary["palette"] is JArray palette && palette.All(p => p.Type == JTokenType.String) && palette.Count > 0)
        {
            summaryOut["palette"] = palette.DeepClone();
        }
        if (draft["items"] is not JArray { Count: > 0 } items)
        {
            return FormatOutcome.Fail("items must be a flat, non-empty list");
        }
        JArray itemsOut = [];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not JObject item)
            {
                return FormatOutcome.Fail($"item {i} must be an object");
            }
            string label = StructuredJson.Text(item, "label")?.ToLowerInvariant();
            if (label is null || !Labels.Contains(label))
            {
                return FormatOutcome.Fail($"item {i} needs \"label\" to be one of {string.Join(", ", Labels)}");
            }
            string caption = StructuredJson.Text(item, "caption");
            if (caption is null)
            {
                return FormatOutcome.Fail($"item {i} needs a non-empty \"caption\"");
            }
            string boxError = Compile(item["bbox"], i, w, h, out JArray box);
            if (boxError is not null)
            {
                return FormatOutcome.Fail(boxError);
            }
            JObject outItem = new() { ["label"] = label, ["caption"] = caption };
            if (label == "text")
            {
                if (item["text"] is not JValue { Type: JTokenType.String } textValue || string.IsNullOrEmpty((string)textValue))
                {
                    return FormatOutcome.Fail($"text item {i} needs \"text\", the exact characters to draw");
                }
                outItem["text"] = (string)textValue;
            }
            outItem["bbox"] = box;
            if (item["tags"] is JArray tags && tags.Count > 0 && tags.All(t => t.Type == JTokenType.String))
            {
                outItem["tags"] = tags.DeepClone();
            }
            if (item["palette"] is JArray itemPalette && itemPalette.Count > 0 && itemPalette.All(p => p.Type == JTokenType.String))
            {
                outItem["palette"] = itemPalette.DeepClone();
            }
            foreach (JProperty extra in item.Properties().Where(p => p.Name is not ("label" or "caption" or "text" or "bbox" or "tags" or "palette")))
            {
                warnings.Add($"Dropped the unknown key \"{extra.Name}\" of item {i} (the compiler rejects unknown fields).");
            }
            itemsOut.Add(outItem);
        }
        JObject compiled = new() { ["summary"] = summaryOut, ["items"] = itemsOut, ["bbox_coord_space"] = "abs_centralized", ["bbox_format"] = "yxyx" };
        return FormatOutcome.Ok(compiled.ToString(Formatting.None), warnings);
    }

    /// <summary>Turns an absolute <c>[x_min, y_min, x_max, y_max]</c> box into the centred <c>[y1, x1, y2, x2]</c> the model reads: minimum edges rounded down and maximum edges up, then half the canvas (whole-number
    /// division) taken off. A box outside the canvas, or with no area, is refused.</summary>
    private static string Compile(JToken token, int index, int width, int height, out JArray box)
    {
        box = null;
        if (token is not JArray { Count: 4 } array || array.Any(v => v.Type is not (JTokenType.Integer or JTokenType.Float)))
        {
            return $"item {index} needs a bbox of four numbers [x_min, y_min, x_max, y_max] in pixels";
        }
        double[] v = [.. array.Select(t => (double)t)];
        if (v.All(x => x >= 0 && x <= 1) && v.Any(x => x != Math.Floor(x)))
        {
            return $"the bbox of item {index} looks like 0 to 1 fractions; write absolute pixels as [x_min, y_min, x_max, y_max]";
        }
        int x1 = (int)Math.Floor(v[0]), y1 = (int)Math.Floor(v[1]), x2 = (int)Math.Ceiling(v[2]), y2 = (int)Math.Ceiling(v[3]);
        if (x1 < 0 || y1 < 0 || x2 > width || y2 > height)
        {
            return $"the bbox of item {index} is outside the {width} by {height} canvas; keep every box inside it (enlarge or recompose rather than clip)";
        }
        if (x1 >= x2 || y1 >= y2)
        {
            return $"the bbox of item {index} needs x_min less than x_max and y_min less than y_max";
        }
        box = new JArray(y1 - height / 2, x1 - width / 2, y2 - height / 2, x2 - width / 2);
        return null;
    }

    /// <inheritdoc/>
    public string Wrap(string original) => null;

    /// <inheritdoc/>
    public bool EscapesTags(string text) => text.TrimStart().StartsWith('{');
}
