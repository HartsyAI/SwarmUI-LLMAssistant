using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Builds tool system prompts and parses tool calls from LLM output (prompt-injection strategy).</summary>
public static partial class ToolPromptService
{
    [GeneratedRegex(@"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallRegex();

    /// <summary>A parsed tool call extracted from the LLM output.</summary>
    public class ParsedToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public JObject Arguments { get; set; }
        public string RawMatch { get; set; }
    }

    /// <summary>Builds the system-prompt snippet describing available tools to the LLM. Deliberately compact:
    /// the previous version emitted each tool's full prose description plus its raw JSON-schema dump verbatim
    /// (often 500+ characters per tool — <c>generate_image</c> alone ran well over a thousand), which reliably
    /// overwhelmed small local GGUF models (0.5B-7B, especially quantized) into either producing garbage or
    /// reciting the schema back instead of answering the user — confirmed live 2026-07-25, identical on both the
    /// pre- and post-Engine-cutover provider code, so this is a prompt-size problem, not a code bug. Every tool
    /// here collapses to one line: name, a compact <c>name: type</c> argument signature, and a one-sentence
    /// summary — enough for a model to shape a valid <see cref="ParseToolCalls"/>-compatible call without
    /// drowning in prose it doesn't need.</summary>
    public static string BuildToolSystemPrompt(List<JObject> tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return "";
        }
        StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Tools");
        sb.AppendLine("To call a tool, output ONLY this on its own line: <tool_call>{\"name\":\"TOOL_NAME\",\"arguments\":{...}}</tool_call>");
        sb.AppendLine("Wait for the <tool_result> before continuing. Only call a tool when it materially helps answer the user.");
        sb.AppendLine();
        foreach (JObject tool in tools)
        {
            string name = tool["id"]?.ToString() ?? tool["name"]?.ToString() ?? "unknown";
            string description = Summarize(tool["description"]?.ToString() ?? "", 100);
            string signature = BuildCompactSignature(tool["parameters"] as JObject);
            sb.Append("- ").Append(name).Append('(').Append(signature).Append(')');
            if (!string.IsNullOrEmpty(description))
            {
                sb.Append(" — ").Append(description);
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Compacts a tool description to its first sentence, or a hard length cap if that sentence is
    /// still long — the fixed budget is what keeps a dozen enabled tools from dwarfing the actual conversation.</summary>
    private static string Summarize(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        int period = text.IndexOf(". ", StringComparison.Ordinal);
        string cut = period > 0 && period < maxLen ? text[..(period + 1)] : text;
        return cut.Length > maxLen ? cut[..maxLen].TrimEnd() + "…" : cut.TrimEnd();
    }

    /// <summary>Renders a JSON-schema <c>parameters</c> object as a compact <c>name: type, name2?: type2</c>
    /// argument signature (optional params get a <c>?</c> suffix; short enums are inlined as <c>[a,b,c]</c>) —
    /// enough for the model to shape valid <c>arguments</c> JSON without the per-parameter prose descriptions
    /// that dominated the old prompt.</summary>
    private static string BuildCompactSignature(JObject parameters)
    {
        if (parameters?["properties"] is not JObject props || props.Count == 0)
        {
            return "";
        }
        HashSet<string> required = [.. (parameters["required"] as JArray)?.Select(t => t.ToString()) ?? []];
        List<string> parts = [];
        foreach (JProperty prop in props.Properties())
        {
            string type = (prop.Value as JObject)?["type"]?.ToString() ?? "any";
            string suffix = required.Contains(prop.Name) ? "" : "?";
            string enumSuffix = "";
            if ((prop.Value as JObject)?["enum"] is JArray enumVals && enumVals.Count is > 0 and <= 6)
            {
                string joined = string.Join(",", enumVals.Select(v => v.ToString()));
                if (joined.Length <= 60)
                {
                    enumSuffix = $"[{joined}]";
                }
            }
            parts.Add($"{prop.Name}{suffix}: {type}{enumSuffix}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Parses all complete &lt;tool_call&gt; blocks from the given text. Blocks whose JSON fails to
    /// parse or has no <c>name</c> field are dropped from the returned list but their raw matched text is
    /// collected into <paramref name="malformedRawMatches"/> so the caller can feed a retry hint back to the
    /// model instead of letting the call silently vanish.</summary>
    public static List<ParsedToolCall> ParseToolCalls(string text, out List<string> malformedRawMatches)
    {
        List<ParsedToolCall> result = [];
        malformedRawMatches = [];
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }
        MatchCollection matches = ToolCallRegex().Matches(text);
        foreach (Match match in matches)
        {
            string json = match.Groups[1].Value;
            JObject obj;
            try
            {
                obj = JObject.Parse(json);
            }
            catch
            {
                // Cheap last-resort recovery (markdown fences, trailing commas, unquoted keys, truncated
                // brackets) before giving up — see JsonRepairService's doc for how this relates to the
                // grammar-masked path (HartsyLocalLLMProvider.StructuredToolCalling), which prevents these
                // errors outright rather than patching them after the fact.
                if (!JsonRepairService.TryRepair(json, out obj))
                {
                    malformedRawMatches.Add(match.Value);
                    continue;
                }
            }
            string name = obj["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                malformedRawMatches.Add(match.Value);
                continue;
            }
            JObject args = obj["arguments"] as JObject ?? new JObject();
            result.Add(new ParsedToolCall
            {
                Id = $"call-{Guid.NewGuid():N}",
                Name = name,
                Arguments = args,
                RawMatch = match.Value
            });
        }
        return result;
    }

    /// <summary>Incrementally detects a literal sentinel (eg <c>&lt;/tool_call&gt;</c>) across a stream of
    /// appended chunks without re-scanning the whole accumulated buffer on every chunk — only the last
    /// <c>sentinel.Length - 1</c> characters need to be carried between calls, since a match can span at most
    /// one chunk boundary.</summary>
    public sealed class TailWindowSentinel(string sentinel)
    {
        private readonly int _carryLength = sentinel.Length - 1;
        private string _tail = "";

        /// <summary>Feeds the next appended chunk; returns true the moment the sentinel is found (once).</summary>
        public bool Feed(string chunk)
        {
            string window = _tail + chunk;
            bool found = window.Contains(sentinel, StringComparison.Ordinal);
            _tail = window.Length > _carryLength ? window[^_carryLength..] : window;
            return found;
        }
    }

    /// <summary>Formats a tool execution result for injection back into conversation history.
    /// Strings inside the result are sanitized so a tool returning literal text like
    /// <c>&lt;tool_call&gt;...&lt;/tool_call&gt;</c> (eg from a web search snippet) can't be re-parsed as a
    /// real tool call on the next agentic iteration. We replace <c>&lt;</c> with <c>&amp;lt;</c> in every
    /// string value before serializing.</summary>
    public static string FormatToolResult(string toolName, JObject result)
    {
        if (result is null)
        {
            return $"<tool_result name=\"{toolName}\">{{}}</tool_result>";
        }
        JObject sanitized = (JObject)result.DeepClone();
        SanitizeAngleBrackets(sanitized);
        string json = sanitized.ToString(Formatting.None);
        return $"<tool_result name=\"{toolName}\">{json}</tool_result>";
    }

    /// <summary>Recursively replaces <c>&lt;</c> with <c>&amp;lt;</c> in every string value of the
    /// given token. Mutates in place. Numeric/bool/null values are left alone.</summary>
    private static void SanitizeAngleBrackets(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (JProperty prop in obj.Properties())
            {
                SanitizeAngleBrackets(prop.Value);
            }
        }
        else if (token is JArray arr)
        {
            foreach (JToken item in arr)
            {
                SanitizeAngleBrackets(item);
            }
        }
        else if (token is JValue val && val.Type == JTokenType.String)
        {
            string s = val.Value<string>();
            if (s is not null && s.Contains('<'))
            {
                val.Value = s.Replace("<", "&lt;");
            }
        }
    }
}
