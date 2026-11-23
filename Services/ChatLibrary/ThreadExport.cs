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
            string content = message["content"]?.Type == JTokenType.String ? message["content"].ToString() : "";
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }
            sb.AppendLine(role == Roles.User ? "## You" : "## Assistant");
            sb.AppendLine();
            sb.AppendLine(content.TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString();
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
