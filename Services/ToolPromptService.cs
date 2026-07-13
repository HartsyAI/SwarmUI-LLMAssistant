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

    /// <summary>Builds the system-prompt snippet describing available tools to the LLM.</summary>
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
        sb.AppendLine();
        sb.AppendLine("You have access to the tools listed below. When you want to use a tool, output EXACTLY this format on its own line (and nothing else on that line):");
        sb.AppendLine();
        sb.AppendLine("<tool_call>{\"name\":\"TOOL_NAME\",\"arguments\":{\"PARAM\":\"VALUE\"}}</tool_call>");
        sb.AppendLine();
        sb.AppendLine("After the tool executes, you will see a <tool_result name=\"TOOL_NAME\">RESULT_JSON</tool_result> message. You may then continue your response or call another tool. Do not call tools unnecessarily. Only call a tool when it materially helps answer the user.");
        sb.AppendLine();
        sb.AppendLine("### Available Tools:");
        sb.AppendLine();
        foreach (JObject tool in tools)
        {
            string name = tool["id"]?.ToString() ?? tool["name"]?.ToString() ?? "unknown";
            string description = tool["description"]?.ToString() ?? "";
            sb.Append("- **").Append(name).Append("**: ").AppendLine(description);
            if (tool["parameters"] is JObject parameters)
            {
                string compact = parameters.ToString(Formatting.None);
                sb.Append("  Parameters schema: ").AppendLine(compact);
            }
        }
        sb.AppendLine();
        return sb.ToString();
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
