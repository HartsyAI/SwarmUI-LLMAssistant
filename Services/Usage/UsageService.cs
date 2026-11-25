using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NodaTime;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.Usage;

/// <summary>Usage and cost for a user: their price table, what one chat used, and what they used in a period. Costs are worked out from the saved
/// token counts each time they are asked for, at today's prices, so editing a price changes past figures too.</summary>
public static class UsageService
{
    /// <summary>The user's price table and budget.</summary>
    public static UsageSettings SettingsFor(User user) => UsageSettings.FromSettings(SettingsService.GetUserSettings(user));

    /// <summary>Replaces the parts of the user's price table and budget that <paramref name="patch"/> holds. Returns the saved settings, or null with the reason.</summary>
    public static UsageSettings Save(User user, JObject patch, out string error)
    {
        lock (SettingsService.SettingsLock)
        {
            JObject layer = SettingsService.GetUserSettings(user);
            UsageSettings saved = UsageSettings.FromSettings(layer).With(patch, out error);
            if (saved is null)
            {
                return null;
            }
            // Replaced, not merged: a merge could never remove a model from the table.
            layer[UsageSettings.Key] = saved.ToJson();
            SettingsService.ReplaceUserSettings(user, layer);
            return saved;
        }
    }

    /// <summary>What one chat used: <c>{replies[{messageId, model, promptTokens, completionTokens, cachedTokens, estimated, partial, cost, price}], totals}</c>,
    /// or null when the chat is not found.</summary>
    public static JObject ForThread(User user, string threadId, UsageSettings settings)
    {
        JObject thread = ThreadStorageService.GetThread(user, threadId);
        return thread is null ? null : Describe(thread, settings);
    }

    /// <summary><see cref="ForThread"/> for a chat already loaded.</summary>
    public static JObject Describe(JObject thread, UsageSettings settings)
    {
        UsageTotals totals = new();
        JArray replies = [];
        foreach (ReplyUsage reply in UsageReader.Replies(thread))
        {
            ModelPrice price = settings.PriceFor(reply.Model);
            decimal? cost = totals.Add(reply, price);
            replies.Add(new JObject
            {
                ["messageId"] = reply.MessageId,
                ["model"] = reply.Model,
                ["promptTokens"] = reply.PromptTokens,
                ["completionTokens"] = reply.CompletionTokens,
                ["cachedTokens"] = reply.CachedTokens,
                ["estimated"] = reply.Estimated,
                ["partial"] = reply.Partial,
                ["cost"] = cost is decimal value ? new JValue(value) : JValue.CreateNull(),
                ["price"] = price is null ? JValue.CreateNull() : price.ToJson(),
            });
        }
        return new JObject { ["replies"] = replies, ["totals"] = totals.ToJson() };
    }

    /// <summary>The user's day, week and (with <paramref name="sessionStart"/>) session sums across all their chats, sub-sessions and scheduled runs included.</summary>
    public static Dictionary<string, UsageTotals> Periods(User user, UsageSettings settings, DateTimeZone zone, DateTimeOffset now, DateTimeOffset? sessionStart = null)
    {
        UsageWindows windows = UsageWindows.At(now, zone);
        DateTimeOffset earliest = sessionStart is DateTimeOffset start && start < windows.Week ? start : windows.Week;
        List<string> ids = UsageReport.ChangedSince(ThreadStorageService.GetThreadIndex(user), earliest);
        return UsageReport.Sum(ids.Select(id => ThreadStorageService.GetThread(user, id)).Where(thread => thread is not null), settings, windows, sessionStart);
    }

    /// <summary>The budget as a reply carries it: <c>{enabled, daily, weekly, warnings}</c>.</summary>
    public static JObject BudgetStatus(UsageBudget budget, IReadOnlyDictionary<string, UsageTotals> periods)
    {
        JObject status = budget.ToJson();
        status["warnings"] = periods is null ? [] : UsageReport.Warnings(budget, periods);
        return status;
    }
}
