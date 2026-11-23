using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;

/// <summary>Turns a chat blob into the text of an export file. Pure.</summary>
public static class ThreadExport
{
    /// <summary>The export formats: <c>json</c> (every message on every branch, importable again) and <c>markdown</c> (the conversation on screen, for reading).</summary>
    public static string NormalizeFormat(string format)
    {
        string f = (format ?? "json").Trim().ToLowerInvariant();
        return f switch
        {
            "json" => "json",
            "markdown" or "md" => "markdown",
            _ => null
        };
    }

    /// <summary>The export JSON: what <see cref="ThreadImport"/> reads. Every message of every branch, plus which branch was showing.</summary>
    public static JObject ToJson(JObject thread, DateTime nowUtc)
    {
        return new JObject
        {
            ["format"] = ThreadImport.FormatName,
            ["version"] = ThreadImport.FormatVersion,
            ["id"] = thread["id"],
            ["title"] = thread["title"]?.ToString() ?? "thread",
            ["assistantId"] = thread["assistantId"],
            ["messages"] = thread["messages"]?.DeepClone() ?? new JArray(),
            ["activeLeafId"] = thread["activeLeafId"],
            ["exportedAt"] = nowUtc.ToString("o")
        };
    }

    /// <summary>The Markdown export: the title, then the conversation on the branch showing, each message under its speaker. Only user and assistant text.</summary>
    public static string ToMarkdown(JObject thread)
    {
        string title = thread["title"]?.ToString();
        StringBuilder sb = new();
        sb.AppendLine($"# {(string.IsNullOrWhiteSpace(title) ? "Chat" : title.Replace('\n', ' ').Replace('\r', ' '))}");
        sb.AppendLine();
        foreach (JObject message in ThreadStorageService.GetActivePath(thread))
        {
            string role = message["role"]?.ToString();
            if (role != Roles.User && role != Roles.Assistant)
            {
                continue;
            }
            // Messages the extension makes itself (a session's report, an inbox note) carry a kind and are not part of the conversation.
            if (message["kind"] is { Type: not JTokenType.Null } kind && !string.IsNullOrEmpty(kind.ToString()))
            {
                continue;
            }
            string content = message["content"]?.Type == JTokenType.String ? message["content"].ToString() : "";
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }
            sb.AppendLine(role == Roles.User ? "## You" : "## Assistant");
            sb.AppendLine();
            sb.AppendLine(EscapeBody(content.TrimEnd()));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Message text made safe to put under a speaker heading: outside code fences, a line that would be a heading or a rule (<c>#</c>, <c>===</c>, <c>---</c>) gets a backslash so it shows as
    /// text and cannot pass for the next speaker or turn the line above it into a heading, and a code fence the text leaves open is closed so it cannot swallow what follows.</summary>
    public static string EscapeBody(string text)
    {
        List<string> lines = [];
        char fence = '\0';
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.TrimStart(' ');
            bool indented = line.Length - trimmed.Length <= 3;
            if (indented && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                if (fence == '\0')
                {
                    fence = trimmed[0];
                }
                else if (trimmed[0] == fence)
                {
                    fence = '\0';
                }
                lines.Add(line);
                continue;
            }
            string rest = trimmed.TrimEnd();
            bool rule = rest.Length > 0 && (rest.All(c => c == '=') || (rest.Contains('-') && rest.All(c => c == '-' || c == ' ')));
            bool structural = fence == '\0' && indented && (trimmed.StartsWith('#') || rule);
            lines.Add(structural ? line[..(line.Length - trimmed.Length)] + "\\" + trimmed : line);
        }
        if (fence != '\0')
        {
            lines.Add(new string(fence, 3));
        }
        return string.Join('\n', lines);
    }

    /// <summary>A file name for the chat: letters, digits, <c>_</c> and <c>-</c> from the title (spaces become <c>_</c>), at most 80 characters, <c>chat</c> when nothing is left.</summary>
    public static string SafeFileStem(string title)
    {
        string kept = new((title ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ').ToArray());
        string stem = kept.Trim().Replace(' ', '_');
        if (stem.Length > 80)
        {
            stem = stem[..80];
        }
        return stem.Length == 0 ? "chat" : stem;
    }

    /// <summary>The whole export: <c>(content, filename)</c>, or null content when <paramref name="format"/> is unknown.</summary>
    public static (string Content, string Filename) Build(JObject thread, string format, DateTime nowUtc)
    {
        string normalized = NormalizeFormat(format);
        string stem = SafeFileStem(thread["title"]?.ToString());
        return normalized switch
        {
            "json" => (ToJson(thread, nowUtc).ToString(Formatting.Indented), stem + ".json"),
            "markdown" => (ToMarkdown(thread), stem + ".md"),
            _ => (null, null)
        };
    }
}
