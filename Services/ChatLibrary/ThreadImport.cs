using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;

/// <summary>What reading a file for import found: either an error, or the chat that would be made and a summary of it for the preview.</summary>
public sealed class ThreadImportResult
{
    public bool Ok { get; init; }
    public string Error { get; init; }
    /// <summary>The new chat, ready to save: new ids throughout, plain text messages only. Null when <see cref="Ok"/> is false.</summary>
    public JObject Thread { get; init; }
    /// <summary>Things left out or changed that the user should know about.</summary>
    public List<string> Warnings { get; init; } = [];
    /// <summary>How many messages in the file were left out (other roles, empty ones).</summary>
    public int Skipped { get; init; }
    /// <summary>True when the chat has more than one branch.</summary>
    public bool HasBranches { get; init; }
    /// <summary>The first few messages, cut short, for the preview.</summary>
    public List<(string Role, string Text)> Sample { get; init; } = [];

    public static ThreadImportResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>Reads a chat from the JSON that <c>LLMAssistantExportThread</c> makes, strictly, and makes a new chat from it. Pure: no user, no storage.
/// <para>Nothing in the file is trusted beyond the conversation text. Ids are all made new (parent links are remapped), and only <c>role</c> (user or assistant), <c>content</c>
/// and <c>timestamp</c> of each message are kept. Attachments (media URLs are fetched by the server), tool calls and results, session, schedule, mode, workspace and approval fields,
/// compaction summaries and parameters are all dropped, so a file cannot reach into files or settings. Duplicate ids, parents that are not in the file, and loops are errors,
/// not repaired.</para></summary>
public static class ThreadImport
{
    /// <summary>Largest file read, in characters.</summary>
    public const int MaxFileChars = 4_000_000;
    public const int MaxMessages = 2000;
    public const int MaxMessageChars = 100_000;
    public const int MaxTitleChars = 200;
    /// <summary>Deepest nesting of JSON read. A conversation is a few levels deep, but a message's tool calls and results (which an export carries, and an import ignores) can nest much deeper.</summary>
    public const int MaxJsonDepth = 64;
    private const int MaxIdChars = 200;
    private const int SampleMessages = 3;
    private const int SampleChars = 200;
    /// <summary>Written into exports so a file says what it is; an import accepts files without it (older exports).</summary>
    public const string FormatName = "llmassistant-thread";
    public const int FormatVersion = 1;

    /// <summary>Reads <paramref name="raw"/>. <paramref name="assistantOk"/> says whether an assistant id exists for this user; one that does not is replaced by <paramref name="fallbackAssistantId"/>.</summary>
    public static ThreadImportResult Parse(string raw, Func<string, bool> assistantOk, string fallbackAssistantId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ThreadImportResult.Fail("content is required: the exported JSON of a chat.");
        }
        if (raw.Length > MaxFileChars)
        {
            return ThreadImportResult.Fail($"The file is too large to import (over {MaxFileChars / 1_000_000} million characters).");
        }
        JObject root;
        try
        {
            using JsonTextReader reader = new(new StringReader(raw)) { MaxDepth = MaxJsonDepth, DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double };
            JToken token = JToken.Load(reader);
            if (reader.Read())
            {
                return ThreadImportResult.Fail("The file is not valid JSON: there is text after the chat.");
            }
            root = token as JObject;
        }
        catch (JsonReaderException ex) when (ex.Message.Contains("MaxDepth", StringComparison.Ordinal))
        {
            return ThreadImportResult.Fail($"The file is nested too deeply (more than {MaxJsonDepth} levels).");
        }
        catch (JsonException)
        {
            return ThreadImportResult.Fail("The file is not valid JSON.");
        }
        if (root is null)
        {
            return ThreadImportResult.Fail("The file is not an exported chat: expected a JSON object.");
        }
        if (root["format"] is { Type: not JTokenType.Null } format && format.ToString() != FormatName)
        {
            return ThreadImportResult.Fail("The file is not an exported chat from this extension.");
        }
        // A version too big for a long is newer too; reading it must not throw.
        if (root["version"] is JValue { Type: JTokenType.Integer or JTokenType.Float } version && version.Value switch
            {
                long number => number > FormatVersion,
                double number => number > FormatVersion,
                _ => true
            })
        {
            return ThreadImportResult.Fail("The file was exported by a newer version than this one can read.");
        }
        if (root["messages"] is not JArray messages)
        {
            return ThreadImportResult.Fail("The file is not an exported chat: it has no messages list.");
        }
        if (messages.Count > MaxMessages)
        {
            return ThreadImportResult.Fail($"Too many messages: {messages.Count} (the most that can be imported is {MaxMessages}).");
        }
        // Pass 1: read every entry as it is, with the structure checked.
        List<Entry> entries = [];
        Dictionary<string, int> byKey = [];
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JObject message)
            {
                return ThreadImportResult.Fail($"Message {i + 1} is not an object.");
            }
            string key;
            if (message["id"] is { Type: not JTokenType.Null } idToken)
            {
                if (idToken.Type != JTokenType.String || string.IsNullOrWhiteSpace(idToken.ToString()) || idToken.ToString().Length > MaxIdChars)
                {
                    return ThreadImportResult.Fail($"Message {i + 1} has an id that is not a short text.");
                }
                key = idToken.ToString();
            }
            else
            {
                // A message with no id gets a key of its own; a file that gives another message the same key is refused as a repeated id.
                key = "\u0001#" + i;
            }
            if (byKey.ContainsKey(key))
            {
                return ThreadImportResult.Fail($"Message {i + 1} repeats the id of an earlier message.");
            }
            byKey[key] = i;
            string role = message["role"]?.Type == JTokenType.String ? message["role"].ToString() : "";
            string content = "";
            if (message["content"] is { Type: not JTokenType.Null } contentToken)
            {
                if (contentToken.Type != JTokenType.String)
                {
                    return ThreadImportResult.Fail($"Message {i + 1} has content that is not text.");
                }
                content = contentToken.ToString();
            }
            if (content.Length > MaxMessageChars)
            {
                return ThreadImportResult.Fail($"Message {i + 1} is too long ({content.Length} characters; the most is {MaxMessageChars}).");
            }
            // parentId: present and null is a root, present and text is that message; absent means "the message before" (how older chats were stored).
            string parent = null;
            bool parentGiven = false;
            if (message.ContainsKey("parentId"))
            {
                JToken parentToken = message["parentId"];
                if (parentToken.Type == JTokenType.Null)
                {
                    parentGiven = true;
                }
                else if (parentToken.Type == JTokenType.String && !string.IsNullOrEmpty(parentToken.ToString()) && parentToken.ToString().Length <= MaxIdChars)
                {
                    parent = parentToken.ToString();
                    parentGiven = true;
                }
                else
                {
                    return ThreadImportResult.Fail($"Message {i + 1} has a parentId that is not a short text.");
                }
            }
            bool keep = (role == Roles.User || role == Roles.Assistant) && !string.IsNullOrWhiteSpace(content) && !HasKind(message);
            entries.Add(new Entry { Key = key, Parent = parent, ParentGiven = parentGiven, Role = role, Content = content, Timestamp = ReadTimestamp(message["timestamp"]), Keep = keep });
        }
        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].ParentGiven)
            {
                entries[i].Parent = i == 0 ? null : entries[i - 1].Key;
            }
            else if (entries[i].Parent is not null && !byKey.ContainsKey(entries[i].Parent))
            {
                return ThreadImportResult.Fail($"Message {i + 1} points to a parent message that is not in the file.");
            }
        }
        // Loops: following parents from any message must reach a root within as many steps as there are messages.
        for (int i = 0; i < entries.Count; i++)
        {
            string at = entries[i].Key;
            for (int steps = 0; at is not null; steps++)
            {
                if (steps > entries.Count)
                {
                    return ThreadImportResult.Fail("The messages form a loop: a message is its own ancestor.");
                }
                at = entries[byKey[at]].Parent;
            }
        }
        // Pass 2: new ids for what is kept; a kept message hangs off its nearest kept ancestor.
        Dictionary<string, string> newIds = [];
        foreach (Entry entry in entries.Where(e => e.Keep))
        {
            newIds[entry.Key] = Guid.NewGuid().ToString("N");
        }
        if (newIds.Count == 0)
        {
            return ThreadImportResult.Fail("The file has no user or assistant messages to import.");
        }
        JArray outMessages = [];
        foreach (Entry entry in entries.Where(e => e.Keep))
        {
            string parentKey = entry.Parent;
            while (parentKey is not null && !newIds.ContainsKey(parentKey))
            {
                parentKey = entries[byKey[parentKey]].Parent;
            }
            outMessages.Add(new JObject
            {
                ["id"] = newIds[entry.Key],
                ["parentId"] = parentKey is null ? JValue.CreateNull() : newIds[parentKey],
                ["role"] = entry.Role,
                ["content"] = entry.Content,
                ["timestamp"] = entry.Timestamp ?? nowUtc.ToString("o")
            });
        }
        // The branch showing: the file's activeLeafId (or its nearest kept ancestor); else the last message, as a chat with no leaf is read everywhere else.
        string leaf = null;
        if (root["activeLeafId"] is { Type: JTokenType.String } leafToken && byKey.ContainsKey(leafToken.ToString()))
        {
            string at = leafToken.ToString();
            while (at is not null && !newIds.ContainsKey(at))
            {
                at = entries[byKey[at]].Parent;
            }
            leaf = at is null ? null : newIds[at];
        }
        leaf ??= outMessages[^1]["id"].ToString();
        List<string> warnings = [];
        int skipped = entries.Count(e => !e.Keep);
        if (skipped > 0)
        {
            warnings.Add($"{skipped} message{(skipped == 1 ? "" : "s")} left out: only text from you and the assistant is imported (tool calls, system and empty messages are not).");
        }
        bool attachments = messages.OfType<JObject>().Any(m => m["media"] is JArray { Count: > 0 } || m["toolCalls"] is JArray { Count: > 0 } || ChatDocuments.HasDocuments(m));
        if (attachments)
        {
            warnings.Add("Attachments and tool calls are not imported, only the text.");
        }
        string assistantId = root["assistantId"]?.Type == JTokenType.String ? root["assistantId"].ToString().Trim() : "";
        if (assistantId.Length > 0 && (assistantId.Length > MaxIdChars || !assistantOk(assistantId)))
        {
            warnings.Add("The assistant this chat used is not available here; it will use your current assistant.");
            assistantId = "";
        }
        if (assistantId.Length == 0)
        {
            assistantId = fallbackAssistantId ?? "";
        }
        string title = CleanTitle(root["title"]?.Type == JTokenType.String ? root["title"].ToString() : "");
        bool hasBranches = outMessages.OfType<JObject>().GroupBy(m => m["parentId"].Type == JTokenType.Null ? null : m["parentId"].ToString()).Any(g => g.Count() > 1);
        JObject thread = new()
        {
            ["title"] = title,
            ["assistantId"] = assistantId,
            ["mode"] = ThreadModes.Chat,
            ["messages"] = outMessages,
            ["activeLeafId"] = leaf,
            ["importedAt"] = nowUtc.ToString("o")
        };
        return new ThreadImportResult
        {
            Ok = true,
            Thread = thread,
            Warnings = warnings,
            Skipped = skipped,
            HasBranches = hasBranches,
            Sample = outMessages.OfType<JObject>().Take(SampleMessages).Select(m => (m["role"].ToString(), Cut(m["content"].ToString(), SampleChars))).ToList()
        };
    }

    private sealed class Entry
    {
        public string Key;
        public string Parent;
        public bool ParentGiven;
        public string Role;
        public string Content;
        public string Timestamp;
        public bool Keep;
    }

    /// <summary>Messages the extension makes itself (a session's report, an inbox note) carry a <c>kind</c>; they are not conversation text.</summary>
    private static bool HasKind(JObject message)
    {
        return message["kind"] is { Type: not JTokenType.Null } kind && !string.IsNullOrEmpty(kind.ToString());
    }

    /// <summary>A timestamp as round-trip UTC text, or null when the value is missing or not a date.</summary>
    private static string ReadTimestamp(JToken token)
    {
        if (token?.Type != JTokenType.String)
        {
            return null;
        }
        if (DateTimeOffset.TryParse(token.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) && parsed.Year >= 2000 && parsed.Year <= 2200)
        {
            return parsed.UtcDateTime.ToString("o");
        }
        return null;
    }

    /// <summary>A title with control characters removed and whitespace flattened, cut to <see cref="MaxTitleChars"/>. Empty stays empty (saving then names the chat after its first message).</summary>
    public static string CleanTitle(string title)
    {
        StringBuilder sb = new();
        foreach (char c in title ?? "")
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        string flat = string.Join(' ', sb.ToString().Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > MaxTitleChars ? flat[..MaxTitleChars] : flat;
    }

    private static string Cut(string text, int max)
    {
        string flat = string.Join(' ', text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > max ? flat[..max] + "…" : flat;
    }
}
