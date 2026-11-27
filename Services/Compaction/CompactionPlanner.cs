using System.Security.Cryptography;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Compaction;

/// <summary>A saved summary of the start of a conversation. It is a side record on the thread, not a node in the message tree, so
/// branching, editing and deleting are unaffected: a summary applies only while the messages it covers are still exactly the start of
/// the active path (<see cref="CoverHash"/>), and quietly stops applying when they are not.</summary>
/// <param name="Id">Identifies the record.</param>
/// <param name="UpToMessageId">The last message the summary covers.</param>
/// <param name="CoverHash">A fingerprint of every message from the start of the conversation through <paramref name="UpToMessageId"/>.</param>
/// <param name="Summary">The summary text.</param>
/// <param name="MessageCount">How many messages the summary stands in for.</param>
/// <param name="TokensBefore">The conversation's size when it was summarized, or 0 when unknown.</param>
/// <param name="Model">The model that wrote it.</param>
/// <param name="CreatedAt">When it was made (ISO 8601).</param>
/// <param name="TipMessageId">The newest message the chat had when the summary was made. A reply at or before it reported its size from before the
/// summary, so that figure no longer describes what is sent.</param>
public sealed record CompactionRecord(string Id, string UpToMessageId, string CoverHash, string Summary, int MessageCount, long TokensBefore, string Model, string CreatedAt,
    string TipMessageId = "")
{
    /// <summary>The JSON a thread stores in its <c>compactions</c> list.</summary>
    public JObject ToJson() => new()
    {
        ["id"] = Id,
        ["upToMessageId"] = UpToMessageId,
        ["coverHash"] = CoverHash,
        ["summary"] = Summary,
        ["messageCount"] = MessageCount,
        ["tokensBefore"] = TokensBefore,
        ["model"] = Model,
        ["createdAt"] = CreatedAt,
        ["tipMessageId"] = TipMessageId,
    };

    /// <summary>Reads <see cref="ToJson"/>'s form, or null when the entry is not a usable record.</summary>
    public static CompactionRecord FromJson(JToken json)
    {
        if (json is not JObject obj || string.IsNullOrEmpty(obj.Value<string>("upToMessageId")) || string.IsNullOrWhiteSpace(obj.Value<string>("summary")))
        {
            return null;
        }
        return new CompactionRecord(obj.Value<string>("id") ?? "", obj.Value<string>("upToMessageId"), obj.Value<string>("coverHash") ?? "",
            obj.Value<string>("summary"), obj.Value<int?>("messageCount") ?? 0, obj.Value<long?>("tokensBefore") ?? 0, obj.Value<string>("model") ?? "",
            obj.Value<string>("createdAt") ?? "", obj.Value<string>("tipMessageId") ?? "");
    }
}

/// <summary>What one compaction would do: summarize <see cref="ToSummarize"/> (after <see cref="Previous"/>'s summary, if any) and cover the
/// path through <see cref="AnchorIndex"/>.</summary>
/// <param name="AnchorIndex">Index in the active path of the last message the new summary covers.</param>
/// <param name="Previous">The summary that applies now, whose text the new one builds on, or null.</param>
/// <param name="ToSummarize">The messages after the previous summary, through the anchor.</param>
public sealed record CompactionPlan(int AnchorIndex, CompactionRecord Previous, List<JObject> ToSummarize);

/// <summary>The decisions of compaction as pure functions of a thread's active path: which saved summary applies, what to summarize next,
/// how big the conversation is, and what the model should be sent instead of the covered messages. Nothing here talks to a model or a
/// disk, so every rule is a unit test.</summary>
public static class CompactionPlanner
{
    /// <summary>How many of the newest messages a compaction leaves as they are, so the model keeps the live thread of the conversation.</summary>
    public const int KeepRecentMessages = 4;

    /// <summary>The share of the window at which a conversation is compacted on its own.</summary>
    public const double DefaultThreshold = 0.8;

    /// <summary>The label a summary message opens with; the model reads it as a note about the earlier conversation.</summary>
    public const string SummaryHeading = "[Summary of the earlier conversation, which was shortened to save space]";

    /// <summary>Whether the conversation is compacted on its own (<c>parameters.autoCompact</c>, on unless set to false).</summary>
    public static bool AutoEnabled(JObject settings) => (settings?["parameters"] as JObject)?.Value<bool?>("autoCompact") ?? true;

    /// <summary>The share of the window that triggers compaction (<c>parameters.compactThreshold</c>), or <see cref="DefaultThreshold"/> when unset or out of range.</summary>
    public static double Threshold(JObject settings)
        => (settings?["parameters"] as JObject)?.Value<double?>("compactThreshold") is > 0 and <= 1 and double value ? value : DefaultThreshold;

    /// <summary>A fingerprint of <paramref name="messages"/> (their ids and texts, in order). Two paths share one only when their covered
    /// messages are the same and unedited.</summary>
    public static string Fingerprint(IEnumerable<JObject> messages)
    {
        StringBuilder sb = new();
        foreach (JObject message in messages)
        {
            sb.Append(message["id"]?.ToString()).Append('\n').Append(message["role"]?.ToString()).Append('\n').Append(message["content"]?.ToString()).Append('\u0001');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24];
    }

    /// <summary>Every record stored on <paramref name="thread"/>, oldest first.</summary>
    public static List<CompactionRecord> Records(JObject thread)
        => thread?["compactions"] is JArray list ? [.. list.Select(CompactionRecord.FromJson).Where(r => r is not null)] : [];

    /// <summary>The summary that applies to <paramref name="path"/>: the newest record whose last covered message is on the path and whose
    /// covered messages still match. An older record can apply when a newer one does not (a branch made before the newer one). Null for none.</summary>
    public static CompactionRecord FindActive(JObject thread, List<JObject> path)
    {
        List<CompactionRecord> records = Records(thread);
        for (int i = records.Count - 1; i >= 0; i--)
        {
            int index = IndexOf(path, records[i].UpToMessageId);
            if (index >= 0 && Fingerprint(path.Take(index + 1)) == records[i].CoverHash)
            {
                return records[i];
            }
        }
        return null;
    }

    /// <summary>The position of the message with <paramref name="messageId"/> in <paramref name="path"/>, or -1.</summary>
    public static int IndexOf(List<JObject> path, string messageId)
        => string.IsNullOrEmpty(messageId) ? -1 : path.FindIndex(m => m["id"]?.ToString() == messageId);

    /// <summary>What to compact next, or null when there is nothing worth compacting: the path is too short to leave
    /// <paramref name="keepRecent"/> messages alone, or the summary that applies already covers all that could be. The cut falls just before
    /// a user message where one is near, so the kept messages open on a question rather than in the middle of an answer.</summary>
    public static CompactionPlan Plan(List<JObject> path, CompactionRecord active, int keepRecent = KeepRecentMessages)
    {
        int previousIndex = active is null ? -1 : IndexOf(path, active.UpToMessageId);
        int anchor = path.Count - 1 - Math.Max(1, keepRecent);
        while (anchor > previousIndex && anchor + 1 < path.Count && path[anchor + 1]["role"]?.ToString() != "user")
        {
            anchor--;
        }
        if (anchor <= previousIndex || anchor < 0)
        {
            // No user turn to cut before inside the room there is: fall back to the plain cut.
            anchor = path.Count - 1 - Math.Max(1, keepRecent);
        }
        if (anchor <= previousIndex || anchor < 0)
        {
            return null;
        }
        return new CompactionPlan(anchor, previousIndex >= 0 ? active : null, path.Skip(previousIndex + 1).Take(anchor - previousIndex).ToList());
    }

    /// <summary>The conversation <paramref name="path"/> with the messages <paramref name="active"/> covers replaced by the summary, flagged
    /// <c>compaction</c>. When the next message is the user's, the summary opens that message, because some chat templates (several local models')
    /// refuse two user messages in a row; otherwise it is a user message of its own, which the reply that follows keeps alternating. The path is
    /// returned unchanged when no summary applies.</summary>
    public static List<JObject> Apply(List<JObject> path, CompactionRecord active)
    {
        int index = active is null ? -1 : IndexOf(path, active.UpToMessageId);
        if (index < 0)
        {
            return path;
        }
        List<JObject> kept = [.. path.Skip(index + 1)];
        if (kept.Count > 0 && kept[0]["role"]?.ToString() == "user")
        {
            JObject merged = (JObject)kept[0].DeepClone();
            merged["content"] = SummaryText(active.Summary) + "\n\n" + (kept[0]["content"]?.ToString() ?? "");
            merged["compaction"] = true;
            kept[0] = merged;
            return kept;
        }
        List<JObject> applied = [new JObject { ["id"] = active.Id, ["role"] = "user", ["content"] = SummaryText(active.Summary), ["compaction"] = true }];
        applied.AddRange(kept);
        return applied;
    }

    /// <summary>The text the model reads in place of the covered messages.</summary>
    public static string SummaryText(string summary) => $"{SummaryHeading}\n\n{summary}";

    /// <summary>How many tokens the conversation holds now, and whether that is a count the model reported (<c>true</c>) or an estimate.
    /// The newest reply after the summary that carries <c>meta.usage</c> is the best figure: what it was sent plus what it wrote, with the
    /// messages after it estimated. Without one, everything that would be sent is estimated, <paramref name="systemPromptTokens"/> (what goes ahead of the conversation: the instructions and the tools' definitions) included. Attached documents' text is counted too, up to
    /// <paramref name="documentBudget"/> quarter-tokens (the most a request carries, however many files there are); those on summarized messages are still sent, so they still count.</summary>
    public static (long Tokens, bool Reported) EstimateContext(List<JObject> path, CompactionRecord active, int systemPromptTokens = 0, int documentBudget = int.MaxValue)
    {
        int anchor = active is null ? -1 : IndexOf(path, active.UpToMessageId);
        // A reply from before the summary was made reported a size that included what the summary has since replaced.
        int staleThrough = Math.Max(anchor, active is null ? -1 : IndexOf(path, active.TipMessageId));
        for (int i = path.Count - 1; i > staleThrough; i--)
        {
            long reported = path[i]["meta"]?["usage"]?.Value<long?>("contextTokens") ?? 0;
            if (reported > 0)
            {
                // The reported size already holds the documents sent up to that reply: only what later ones add to the (bounded) total is new.
                return (reported + path.Skip(i + 1).Sum(m => (long)EstimateMessage(m)) + EstimateDocuments(path, documentBudget) - EstimateDocuments(path.Take(i + 1), documentBudget), true);
            }
        }
        long total = systemPromptTokens + (active is null ? 0 : ContextWindowService.EstimateTokens(SummaryText(active.Summary)));
        return (total + path.Skip(anchor + 1).Sum(m => (long)EstimateMessage(m)) + EstimateDocuments(path, documentBudget), false);
    }

    /// <summary>An estimate of the tokens the documents on these messages add to a request.</summary>
    private static long EstimateDocuments(IEnumerable<JObject> messages, int documentBudget)
        => (Math.Min(messages.Sum(ChatDocuments.ReferencedCost), Math.Max(0, documentBudget)) + 3) / 4;

    /// <summary>An estimate of the tokens of one saved message as a later request carries it: its text and its calls, with each call's result cut as the replay cuts it
    /// (<see cref="ToolConstants.MaxReplayedToolResultChars"/>). This is what summarizing the message takes out of a request.</summary>
    public static int EstimateReplayedMessage(JObject message)
    {
        long chars = message["content"]?.ToString().Length ?? 0;
        foreach (JObject call in (message["toolCalls"] as JArray)?.OfType<JObject>() ?? [])
        {
            chars += call.Properties().Where(p => p.Name != "result").Sum(p => p.Value.ToString(Newtonsoft.Json.Formatting.None).Length);
            if (call["result"] is { Type: not JTokenType.Null } result)
            {
                int length = result.Type == JTokenType.String ? result.ToString().Length : result.ToString(Newtonsoft.Json.Formatting.None).Length;
                chars += Math.Min(length, ToolConstants.MaxReplayedToolResultChars);
            }
        }
        return (int)((chars + 3) / 4);
    }

    /// <summary>An estimate of one saved message's tokens: its text, plus the calls it made and what they returned.</summary>
    public static int EstimateMessage(JObject message)
    {
        int chars = message["content"]?.ToString().Length ?? 0;
        if (message["toolCalls"] is JArray calls)
        {
            chars += calls.Sum(c => c.ToString(Newtonsoft.Json.Formatting.None).Length);
        }
        return (int)Math.Ceiling(chars / 4.0);
    }

    /// <summary>Whether a conversation of <paramref name="contextTokens"/> in a window of <paramref name="window"/> is due for compaction.
    /// An unknown window (0) is never due, and nor is a threshold outside (0, 1].</summary>
    public static bool IsDue(long contextTokens, int window, double threshold)
        => window > 0 && threshold is > 0 and <= 1 && contextTokens >= window * threshold;

    /// <summary>The text of <paramref name="messages"/> for the summarizer to read: role, text, and one line per tool call, each message cut to
    /// <paramref name="perMessageChars"/> characters so one huge tool result cannot crowd out the rest.</summary>
    public static string BuildTranscript(IEnumerable<JObject> messages, int perMessageChars = 6000)
    {
        StringBuilder sb = new();
        foreach (JObject message in messages)
        {
            string role = message["role"]?.ToString() == "assistant" ? "Assistant" : "User";
            sb.Append(role).Append(": ").AppendLine(Clip(message["content"]?.ToString() ?? "", perMessageChars));
            if (message["toolCalls"] is JArray calls)
            {
                foreach (JObject call in calls.OfType<JObject>())
                {
                    string outcome = call["result"]?["success"]?.Value<bool?>() == false ? "failed" : "ok";
                    sb.Append("  [tool ").Append(call["name"]).Append(' ').Append(Clip(call["arguments"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "", 300)).Append(" -> ").Append(outcome).AppendLine("]");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>The prompt that asks the summarizer for the new summary: the previous summary (if any), then the messages to fold in.</summary>
    public static string BuildPrompt(CompactionPlan plan, string extraInstructions = null)
    {
        StringBuilder sb = new();
        if (plan.Previous is not null)
        {
            sb.AppendLine("Summary so far:").AppendLine(plan.Previous.Summary).AppendLine();
            sb.AppendLine("Newer messages to fold into it:");
        }
        else
        {
            sb.AppendLine("Conversation to summarize:");
        }
        sb.AppendLine(BuildTranscript(plan.ToSummarize));
        if (!string.IsNullOrWhiteSpace(extraInstructions))
        {
            sb.AppendLine().Append("Also: ").AppendLine(extraInstructions.Trim());
        }
        return sb.ToString();
    }

    /// <summary>What the summarizer is told to be.</summary>
    public const string SummarizerSystemPrompt =
        "You shorten a conversation so it can continue in less space. Write a summary that the assistant will read in place of the messages. "
        + "Keep what is needed to carry on: the user's goals and requests, decisions made, facts and names given, files or items worked on, what was done and what is still open, and any preference the user stated. "
        + "Be concrete and brief. Write plain prose or short bullet points, in the third person, with no preamble and no closing remarks. Do not answer or continue the conversation.";

    /// <summary>The text cut to <paramref name="max"/> characters, with a marker when something was dropped.</summary>
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + " …[cut]";
}
