using Hartsy.Extensions.LLMAssistant.Services.Schedules;
using Hartsy.Extensions.LLMAssistant.Services.Usage;
using Newtonsoft.Json.Linq;
using NodaTime;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Token usage and cost: the price table and budget, one chat's figures, and the day's, week's and session's totals.</summary>
public static class UsageEndpoints
{
    /// <summary>The caller's price table and budget. Reply: <c>{success, usage: {currency, prices: {model: {input, output, cached}}, budget: {enabled, daily, weekly}}}</c>.</summary>
    public static async Task<JObject> LLMAssistantGetUsageSettings(Session session)
    {
        return new JObject { ["success"] = true, ["usage"] = UsageService.SettingsFor(session.User).ToJson() };
    }

    /// <summary>Saves the caller's price table and budget. Each of <c>currency</c>, <c>prices</c> and <c>budget</c> is optional and replaces that part;
    /// a <c>prices</c> object replaces the whole table. Reply as <see cref="LLMAssistantGetUsageSettings"/>.</summary>
    public static async Task<JObject> LLMAssistantSaveUsageSettings(Session session, JObject rawInput)
    {
        UsageSettings saved = UsageService.Save(session.User, rawInput, out string error);
        return saved is null ? Fail(error) : new JObject { ["success"] = true, ["usage"] = saved.ToJson() };
    }

    /// <summary>What one chat used, reply by reply. <paramref name="timeZone"/> (an IANA name) places the day and week the budget is checked against.
    /// Reply: <c>{success, threadId, currency, replies[], totals, budget: {enabled, daily, weekly, warnings[]}}</c>.</summary>
    public static async Task<JObject> LLMAssistantGetThreadUsage(Session session, string threadId, string timeZone = "")
    {
        UsageSettings settings = UsageService.SettingsFor(session.User);
        JObject usage = string.IsNullOrWhiteSpace(threadId) ? null : UsageService.ForThread(session.User, threadId, settings);
        if (usage is null)
        {
            return Fail("Thread not found.");
        }
        // A zone this server does not know must not hide the chat's own figures, so the budget falls back to UTC.
        if (!ScheduleTrigger.TryZone(timeZone, out DateTimeZone zone, out _))
        {
            zone = DateTimeZone.Utc;
        }
        // Every chat of the week is read only when a budget is switched on.
        Dictionary<string, UsageTotals> periods = settings.Budget.Enabled ? UsageService.Periods(session.User, settings, zone, DateTimeOffset.UtcNow) : null;
        usage["success"] = true;
        usage["threadId"] = threadId;
        usage["currency"] = settings.Currency;
        usage["budget"] = UsageService.BudgetStatus(settings.Budget, periods);
        return usage;
    }

    /// <summary>The caller's totals for today and this week (from Monday) in <paramref name="timeZone"/> (an IANA name; empty is UTC), and since
    /// <c>since</c> (an ISO time, for "this session") when given. Reply: <c>{success, currency, timeZone, periods: {day, week, session?}, budget}</c>;
    /// each period is <c>{from, replies, promptTokens, completionTokens, cachedTokens, totalTokens, estimatedReplies, partialReplies, pricedReplies,
    /// unpricedReplies, unpricedTokens, cost, costEstimated}</c>, with <c>cost</c> null when nothing in it has a price.</summary>
    public static async Task<JObject> LLMAssistantGetUsageTotals(Session session, JObject rawInput, string timeZone = "")
    {
        if (!ScheduleTrigger.TryZone(timeZone, out DateTimeZone zone, out string error))
        {
            return Fail(error);
        }
        DateTimeOffset? sessionStart = null;
        // Read from the request itself: a time bound to a string loses its offset on the way (JSON reads it as a date).
        JToken since = rawInput?["since"];
        if (since is not null && since.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(since.ToString()))
        {
            sessionStart = UsageReader.ReadTime(since);
            if (sessionStart is null)
            {
                return Fail("since must be an ISO time, such as 2026-01-31T09:00:00Z.");
            }
        }
        UsageSettings settings = UsageService.SettingsFor(session.User);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UsageWindows windows = UsageWindows.At(now, zone);
        Dictionary<string, UsageTotals> periods = UsageService.Periods(session.User, settings, zone, now, sessionStart);
        JObject json = [];
        foreach ((string name, UsageTotals totals) in periods)
        {
            JObject period = totals.ToJson();
            period["from"] = (name == "day" ? windows.Day : name == "week" ? windows.Week : sessionStart.Value).ToString("o");
            json[name] = period;
        }
        return new JObject
        {
            ["success"] = true,
            ["currency"] = settings.Currency,
            ["timeZone"] = zone.Id,
            ["periods"] = json,
            ["budget"] = UsageService.BudgetStatus(settings.Budget, periods),
        };
    }

    private static JObject Fail(string error) => new() { ["success"] = false, ["error"] = error };
}
