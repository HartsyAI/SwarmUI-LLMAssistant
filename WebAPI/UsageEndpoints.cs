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

    /// <summary>What one chat used, reply by reply. Reply: <c>{success, threadId, currency, replies[], totals}</c>.</summary>
    public static async Task<JObject> LLMAssistantGetThreadUsage(Session session, string threadId)
    {
        UsageSettings settings = UsageService.SettingsFor(session.User);
        JObject usage = string.IsNullOrWhiteSpace(threadId) ? null : UsageService.ForThread(session.User, threadId, settings);
        if (usage is null)
        {
            return Fail("Thread not found.");
        }
        usage["success"] = true;
        usage["threadId"] = threadId;
        usage["currency"] = settings.Currency;
        return usage;
    }

    /// <summary>The caller's totals for today and this week (from Monday) in <paramref name="timeZone"/> (an IANA name; empty is UTC), and for "this session" when either
    /// <c>sessionSeconds</c> (how long ago it began, which the caller's clock cannot skew) or <c>since</c> (an ISO time by the server's clock) is given.
    /// With <c>budgetOnly</c>, the chats are read only when a budget is switched on. Reply: <c>{success, currency, timeZone, periods: {day, week, session?}, budget:
    /// {enabled, daily, weekly, warnings[]}}</c>; each period is <c>{from, replies, promptTokens, completionTokens, cachedTokens, totalTokens, estimatedReplies,
    /// partialReplies, pricedReplies, unpricedReplies, unpricedTokens, cost, costEstimated}</c>, with <c>cost</c> null when nothing in it has a price.</summary>
    public static async Task<JObject> LLMAssistantGetUsageTotals(Session session, JObject rawInput, string timeZone = "")
    {
        if (!ScheduleTrigger.TryZone(timeZone, out DateTimeZone zone, out string error))
        {
            return Fail(error);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!UsageReport.TryReadSessionStart(rawInput, now, out DateTimeOffset? sessionStart, out error))
        {
            return Fail(error);
        }
        UsageSettings settings = UsageService.SettingsFor(session.User);
        bool budgetOnly = rawInput?["budgetOnly"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        UsageWindows windows = UsageWindows.At(now, zone);
        Dictionary<string, UsageTotals> periods = budgetOnly && !settings.Budget.Enabled ? null : UsageService.Periods(session.User, settings, zone, now, sessionStart);
        JObject json = [];
        foreach ((string name, UsageTotals totals) in periods ?? [])
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
