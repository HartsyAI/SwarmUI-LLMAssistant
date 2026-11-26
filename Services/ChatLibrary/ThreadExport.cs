using System.Text;
using Newtonsoft.Json;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
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
        JObject export = new()
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
        if (ChatDocuments.IsMarked(thread))
        {
            export[ChatDocuments.ThreadFlag] = true;
        }
        return export;
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
    /// text and cannot pass for the next speaker or turn the line above it into a heading, and a code fence the text leaves open is closed so it cannot swallow what follows. Fences follow CommonMark:
    /// an opener is three or more backticks or tildes (up to three spaces in; a backtick fence's info text may not hold a backtick), and a closer is the same character, at least as many, with only
    /// spaces after it. Line breaks are <c>\n</c>, <c>\r\n</c> or a lone <c>\r</c>.</summary>
    public static string EscapeBody(string text)
    {
        List<string> lines = [];
        char fence = '\0';
        int fenceLength = 0;
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string trimmed = line.TrimStart(' ');
            bool indented = line.Length - trimmed.Length <= 3;
            if (indented && (trimmed.StartsWith('`') || trimmed.StartsWith('~')))
            {
                char mark = trimmed[0];
                int run = 0;
                while (run < trimmed.Length && trimmed[run] == mark)
                {
                    run++;
                }
                string after = trimmed[run..];
                if (fence == '\0' && run >= 3 && !(mark == '`' && after.Contains('`')))
                {
                    fence = mark;
                    fenceLength = run;
                    lines.Add(line);
                    continue;
                }
                if (fence != '\0' && mark == fence && run >= fenceLength && after.Trim(' ', '\t').Length == 0)
                {
                    fence = '\0';
                    fenceLength = 0;
                    lines.Add(line);
                    continue;
                }
            }
            string rest = trimmed.TrimEnd();
            bool rule = rest.Length > 0 && (rest.All(c => c == '=') || (rest.Contains('-') && rest.All(c => c == '-' || c == ' ')));
            bool structural = fence == '\0' && indented && (trimmed.StartsWith('#') || rule);
            lines.Add(structural ? line[..(line.Length - trimmed.Length)] + "\\" + trimmed : line);
        }
        if (fence != '\0')
        {
            lines.Add(new string(fence, Math.Max(3, fenceLength)));
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
