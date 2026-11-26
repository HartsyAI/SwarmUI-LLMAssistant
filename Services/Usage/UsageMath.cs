using System.Globalization;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;
using Hartsy.Extensions.LLMAssistant.Services.Compaction;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NodaTime;

namespace Hartsy.Extensions.LLMAssistant.Services.Usage;

/// <summary>The tokens of one saved model reply.</summary>
/// <param name="MessageId">The reply's message id.</param>
/// <param name="Model">The model id the reply was asked of, empty when the request named none.</param>
/// <param name="PromptTokens">Prompt tokens, summed over the turn's model rounds.</param>
/// <param name="CompletionTokens">Tokens the model wrote, summed over the turn's model rounds.</param>
/// <param name="CachedTokens">The part of the prompt tokens served from the endpoint's cache.</param>
/// <param name="Estimated">True when the backend reported nothing and the counts come from the length of the saved text.</param>
/// <param name="Partial">True when the backend reported some of the turn but not all of it (a stopped turn), so the counts are low.</param>
/// <param name="Time">When the reply was saved, or null when it carries no readable time.</param>
public sealed record ReplyUsage(string MessageId, string Model, long PromptTokens, long CompletionTokens, long CachedTokens, bool Estimated, bool Partial, DateTimeOffset? Time);

/// <summary>Reads what a chat's replies used from the thread as saved (<c>meta.usage</c>), estimating a reply that has none. Nothing is written back:
/// an estimate in <c>meta.usage</c> would be taken for a count the model reported (see <see cref="CompactionPlanner.EstimateContext"/>).</summary>
public static class UsageReader
{
    /// <summary>Every model reply in the chat, on every branch (a regenerated reply and each compare lane cost tokens too).
    /// With <paramref name="since"/>, only the replies saved at or after it.</summary>
    public static List<ReplyUsage> Replies(JObject thread, DateTimeOffset? since = null)
    {
        List<ReplyUsage> replies = [];
        if (thread?["messages"] is not JArray messages)
        {
            return replies;
        }
        Dictionary<string, JObject> byId = new(StringComparer.Ordinal);
        foreach (JObject message in messages.OfType<JObject>())
        {
            if (message["id"]?.ToString() is { Length: > 0 } id)
            {
                byId[id] = message;
            }
        }
        List<CompactionRecord> compactions = [.. (thread["compactions"] as JArray ?? []).Select(CompactionRecord.FromJson).Where(r => r is not null)];
        Estimator estimator = new(byId, compactions);
        foreach (JObject message in messages.OfType<JObject>())
        {
            if (!IsModelReply(message))
            {
                continue;
            }
            DateTimeOffset? time = ReadTime(message["timestamp"]);
            if (since is DateTimeOffset from && (time is null || time < from))
            {
                continue;
            }
            JObject meta = (JObject)message["meta"];
            string messageId = message["id"]?.ToString() ?? "";
            string model = meta["model"]?.ToString() ?? "";
            LLMUsage reported = LLMUsage.FromJson(meta["usage"]);
            if (reported is { Any: true } && reported.PromptTokens + reported.CompletionTokens > 0)
            {
                bool partial = reported.Incomplete || (message["rounds"] is JArray rounds && rounds.Count > reported.Rounds);
                replies.Add(new ReplyUsage(messageId, model, reported.PromptTokens, reported.CompletionTokens, reported.CachedTokens, false, partial, time));
            }
            else
            {
                (long prompt, long completion) = estimator.Estimate(message);
                replies.Add(new ReplyUsage(messageId, model, prompt, completion, 0, true, false, time));
            }
        }
        return replies;
    }

    /// <summary>Whether a saved message is something a model wrote: an assistant message from a turn. A tool the user ran from the picker, and an imported
    /// message (which keeps no <c>meta</c>), are not.</summary>
    public static bool IsModelReply(JObject message)
        => message?["role"]?.ToString() == "assistant" && message["meta"] is JObject meta && meta["manualTool"]?.Value<bool?>() != true
            && (meta["usage"] is JObject || meta["genTime"] is not null || !string.IsNullOrEmpty(meta["model"]?.ToString()));

    /// <summary>A message's time. A thread read from JSON holds an ISO time as a date, and one built in memory as text.</summary>
    public static DateTimeOffset? ReadTime(JToken token)
    {
        if (token is JValue { Value: DateTimeOffset offset })
        {
            return offset.ToUniversalTime();
        }
        if (token is JValue { Value: DateTime date })
        {
            return new DateTimeOffset(date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc));
        }
        if (token?.Type == JTokenType.String && DateTimeOffset.TryParse(token.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>Rough counts for replies with no report, at about four characters a token: the prompt is the conversation before the reply (shortened as a summary in
    /// force then would have shortened it) plus its tool results, and the completion is its text, reasoning and tool arguments. The system prompt, tool
    /// definitions and attachments are not saved with the chat, so the figure is usually low. Each message of the chat is measured once, however many replies follow it.</summary>
    private sealed class Estimator(Dictionary<string, JObject> byId, List<CompactionRecord> compactions)
    {
        /// <summary>The conversation up to and including a message: its tokens, and for each summary the tokens it covers (-1 until its last covered message is passed)
        /// and whether the message it was made after is passed.</summary>
        private sealed record Lineage(long Tokens, long[] Covered, bool[] Made);

        /// <summary>The lineages worked out so far, by message id.</summary>
        private readonly Dictionary<string, Lineage> known = new(StringComparer.Ordinal);

        /// <summary>The estimate for one reply.</summary>
        public (long Prompt, long Completion) Estimate(JObject reply)
        {
            Lineage before = Through(ParentOf(reply));
            long prompt = before?.Tokens ?? 0;
            for (int i = compactions.Count - 1; before is not null && i >= 0; i--)
            {
                if (before.Made[i] && before.Covered[i] >= 0)
                {
                    prompt = before.Tokens - before.Covered[i] + ContextWindowService.EstimateTokens(CompactionPlanner.SummaryText(compactions[i].Summary));
                    break;
                }
            }
            long written = (reply["rawContent"] ?? reply["content"])?.ToString().Length ?? 0;
            long results = 0;
            foreach (JObject call in (reply["toolCalls"] as JArray ?? []).OfType<JObject>())
            {
                written += (call["name"]?.ToString().Length ?? 0) + (call["arguments"]?.ToString(Formatting.None).Length ?? 0);
                results += call["result"]?.ToString(Formatting.None).Length ?? 0;
            }
            return (prompt + (results + 3) / 4, (written + 3) / 4);
        }

        /// <summary>A message's parent id, or null at a root.</summary>
        private static string ParentOf(JObject message) => message["parentId"]?.Type == JTokenType.String ? message["parentId"].ToString() : null;

        /// <summary>The lineage ending at <paramref name="id"/>, or null when there is none. A loop of parents ends where it closes.</summary>
        private Lineage Through(string id)
        {
            Stack<(string Id, JObject Message)> pending = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            Lineage line = null;
            while (id is not null && !known.TryGetValue(id, out line) && seen.Add(id) && byId.TryGetValue(id, out JObject node))
            {
                pending.Push((id, node));
                id = ParentOf(node);
            }
            while (pending.TryPop(out (string Id, JObject Message) next))
            {
                long tokens = (line?.Tokens ?? 0) + CompactionPlanner.EstimateMessage(next.Message);
                long[] covered = new long[compactions.Count];
                bool[] made = new bool[compactions.Count];
                for (int i = 0; i < compactions.Count; i++)
                {
                    covered[i] = next.Id == compactions[i].UpToMessageId ? tokens : line?.Covered[i] ?? -1;
                    made[i] = next.Id == compactions[i].TipMessageId || (line?.Made[i] ?? false);
                }
                line = new Lineage(tokens, covered, made);
                known[next.Id] = line;
            }
            return line;
        }
    }
}

/// <summary>Sums replies into the figures a chat or a period shows. A cost exists only for replies whose model has a price.</summary>
public sealed class UsageTotals
{
    /// <summary>Replies counted.</summary>
    public int Replies;

    /// <summary>Prompt tokens.</summary>
    public long PromptTokens;

    /// <summary>Completion tokens.</summary>
    public long CompletionTokens;

    /// <summary>Cached prompt tokens (part of <see cref="PromptTokens"/>).</summary>
    public long CachedTokens;

    /// <summary>Replies whose counts are estimates.</summary>
    public int EstimatedReplies;

    /// <summary>Replies whose report is missing some rounds.</summary>
    public int PartialReplies;

    /// <summary>Replies of a model with a price.</summary>
    public int PricedReplies;

    /// <summary>Tokens of replies whose model has no price; <see cref="Cost"/> leaves them out.</summary>
    public long UnpricedTokens;

    /// <summary>The cost of the priced replies.</summary>
    public decimal Cost;

    /// <summary>Whether any priced reply's counts are estimates, so the cost is one too.</summary>
    public bool CostEstimated;

    /// <summary>Adds one reply at <paramref name="price"/> (null: no price) and returns its cost, or null.</summary>
    public decimal? Add(ReplyUsage reply, ModelPrice price)
    {
        Replies++;
        PromptTokens += reply.PromptTokens;
        CompletionTokens += reply.CompletionTokens;
        CachedTokens += reply.CachedTokens;
        EstimatedReplies += reply.Estimated ? 1 : 0;
        PartialReplies += reply.Partial ? 1 : 0;
        if (price is null)
        {
            UnpricedTokens += reply.PromptTokens + reply.CompletionTokens;
            return null;
        }
        decimal cost = price.Cost(reply.PromptTokens, reply.CompletionTokens, reply.CachedTokens);
        PricedReplies++;
        Cost += cost;
        CostEstimated |= reply.Estimated;
        return cost;
    }

    /// <summary>The totals as the routes send them. <c>cost</c> is null when no reply has a price: never a made-up zero.</summary>
    public JObject ToJson() => new()
    {
        ["replies"] = Replies,
        ["promptTokens"] = PromptTokens,
        ["completionTokens"] = CompletionTokens,
        ["cachedTokens"] = CachedTokens,
        ["totalTokens"] = PromptTokens + CompletionTokens,
        ["estimatedReplies"] = EstimatedReplies,
        ["partialReplies"] = PartialReplies,
        ["pricedReplies"] = PricedReplies,
        ["unpricedReplies"] = Replies - PricedReplies,
        ["unpricedTokens"] = UnpricedTokens,
        ["cost"] = PricedReplies > 0 ? new JValue(Cost) : JValue.CreateNull(),
        ["costEstimated"] = CostEstimated,
    };
}

/// <summary>Where the user's day and week began.</summary>
/// <param name="Day">The start of today in the user's time zone.</param>
/// <param name="Week">The start of this week's Monday in the user's time zone.</param>
public sealed record UsageWindows(DateTimeOffset Day, DateTimeOffset Week)
{
    /// <summary>The windows holding <paramref name="now"/> in <paramref name="zone"/>. A day that begins inside a daylight-saving gap starts at its first real moment.</summary>
    public static UsageWindows At(DateTimeOffset now, DateTimeZone zone)
    {
        LocalDate today = Instant.FromDateTimeOffset(now).InZone(zone).Date;
        LocalDate monday = today.With(DateAdjusters.PreviousOrSame(IsoDayOfWeek.Monday));
        return new UsageWindows(zone.AtStartOfDay(today).ToDateTimeOffset().ToUniversalTime(), zone.AtStartOfDay(monday).ToDateTimeOffset().ToUniversalTime());
    }
}

/// <summary>The sums for the periods a user can ask about, and the budget warnings that follow from them.</summary>
public static class UsageReport
{
    /// <summary>Sums the replies of <paramref name="chats"/> into the day, the week and, when <paramref name="sessionStart"/> is given, the time since it.
    /// The chats are taken one at a time, so a caller may read them lazily.</summary>
    public static Dictionary<string, UsageTotals> Sum(IEnumerable<IReadOnlyList<ReplyUsage>> chats, UsageSettings settings, UsageWindows windows, DateTimeOffset? sessionStart = null)
    {
        Dictionary<string, (DateTimeOffset From, UsageTotals Totals)> periods = new()
        {
            ["day"] = (windows.Day, new UsageTotals()),
            ["week"] = (windows.Week, new UsageTotals()),
        };
        if (sessionStart is DateTimeOffset start)
        {
            periods["session"] = (start, new UsageTotals());
        }
        foreach (IReadOnlyList<ReplyUsage> chat in chats)
        {
            foreach (ReplyUsage reply in chat)
            {
                ModelPrice price = settings.PriceFor(reply.Model);
                foreach ((DateTimeOffset from, UsageTotals totals) in periods.Values)
                {
                    if (reply.Time >= from)
                    {
                        totals.Add(reply, price);
                    }
                }
            }
        }
        return periods.ToDictionary(p => p.Key, p => p.Value.Totals);
    }

    /// <summary>The longest session accepted in <c>sessionSeconds</c> (ten years).</summary>
    public const double MaxSessionSeconds = 315_360_000;

    /// <summary>When the caller's session began, from <c>sessionSeconds</c> (seconds before <paramref name="now"/>) or else <c>since</c> (an ISO time); null when neither is sent.
    /// <c>since</c> is read from the request itself, because a time bound to a string loses its offset on the way (JSON reads it as a date).</summary>
    public static bool TryReadSessionStart(JObject request, DateTimeOffset now, out DateTimeOffset? start, out string error)
    {
        start = null;
        error = null;
        JToken seconds = request?["sessionSeconds"];
        if (seconds is not null && seconds.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(seconds.ToString()))
        {
            if (!double.TryParse(seconds.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ago) || !(ago >= 0 && ago <= MaxSessionSeconds))
            {
                error = "sessionSeconds must be a number of seconds, 0 or more.";
                return false;
            }
            start = now.AddSeconds(-ago);
            return true;
        }
        JToken since = request?["since"];
        if (since is not null && since.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(since.ToString()))
        {
            start = UsageReader.ReadTime(since);
            if (start is null)
            {
                error = "since must be an ISO time, such as 2026-01-31T09:00:00Z.";
                return false;
            }
        }
        return true;
    }

    /// <summary>The chats in a thread index that changed at or after <paramref name="earliest"/> (a chat untouched since then holds no reply from the period), each
    /// with a stamp that changes whenever the chat is saved (empty when the index has none).</summary>
    public static List<(string Id, string Stamp)> ChangedSince(JArray index, DateTimeOffset earliest)
        => [.. index.OfType<JObject>()
            .Where(row => UsageReader.ReadTime(row["updatedAt"]) is not DateTimeOffset updated || updated >= earliest)
            .Select(row => (Id: row["id"]?.ToString(), Stamp: ThreadSearchIndex.IsoTime(row["updatedAt"])))
            .Where(row => !string.IsNullOrEmpty(row.Id))
            .DistinctBy(row => row.Id)];

    /// <summary>The limits the day's and week's cost have reached: <c>[{period, spent, limit, estimated}]</c>, empty when the budget is off. A warning only:
    /// no request is ever refused because of it.</summary>
    public static JArray Warnings(UsageBudget budget, IReadOnlyDictionary<string, UsageTotals> periods)
    {
        JArray warnings = [];
        if (!budget.Enabled)
        {
            return warnings;
        }
        foreach ((string period, decimal limit) in new[] { ("day", budget.Daily), ("week", budget.Weekly) })
        {
            if (limit > 0 && periods.TryGetValue(period, out UsageTotals totals) && totals.PricedReplies > 0 && totals.Cost >= limit)
            {
                warnings.Add(new JObject { ["period"] = period, ["spent"] = totals.Cost, ["limit"] = limit, ["estimated"] = totals.CostEstimated });
            }
        }
        return warnings;
    }
}
