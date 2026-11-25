using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Compaction;
using Hartsy.Extensions.LLMAssistant.Services.Usage;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using NodaTime;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Usage and cost: the arithmetic, which figures are the backend's and which are estimates, models with no price, the day and week boundaries
/// in the user's time zone, and the soft budget.</summary>
public class UsageCostTests
{
    private static UsageSettings Settings(JObject patch)
    {
        UsageSettings settings = UsageSettings.FromSettings([]).With(patch, out string error);
        Assert.Null(error);
        return settings;
    }

    private static UsageSettings Priced(string model = "m", double input = 2, double output = 10, double? cached = null)
        => Settings(new JObject { ["prices"] = new JObject { [model] = new JObject { ["input"] = input, ["output"] = output, ["cached"] = cached } } });

    private static JObject User(string id, string parent, string text) => new() { ["id"] = id, ["parentId"] = parent, ["role"] = "user", ["content"] = text };

    private static JObject Reply(string id, string parent, string model = "m", long? prompt = null, long completion = 0, long cached = 0, string time = "2026-03-04T12:00:00Z", string text = "")
    {
        JObject meta = new() { ["model"] = model, ["genTime"] = 1.0 };
        if (prompt is long sent)
        {
            meta["usage"] = new JObject { ["promptTokens"] = sent, ["completionTokens"] = completion, ["cachedTokens"] = cached, ["contextTokens"] = sent + completion, ["rounds"] = 1 };
        }
        return new JObject { ["id"] = id, ["parentId"] = parent, ["role"] = "assistant", ["content"] = text, ["rawContent"] = text, ["timestamp"] = time, ["meta"] = meta };
    }

    private static JObject Thread(params JObject[] messages) => new() { ["id"] = "t", ["messages"] = new JArray(messages.Cast<object>().ToArray()) };

    [Fact]
    public void CostIsPerMillionWithCachedInputAtItsOwnPrice()
    {
        ModelPrice price = new(2.5m, 10m, 0.25m);
        // 600,000 fresh input at 2.5, 400,000 cached at 0.25, 100,000 output at 10.
        Assert.Equal(2.6m, price.Cost(1_000_000, 100_000, 400_000));
        Assert.Equal(0.0000025m, price.Cost(1, 0));
    }

    [Fact]
    public void CachedInputWithNoPriceOfItsOwnIsChargedAsInputAndNeverExceedsThePrompt()
    {
        Assert.Equal(2m, new ModelPrice(2m, 10m).Cost(1_000_000, 0, 500_000));
        Assert.Equal(1m, new ModelPrice(4m, 10m, 1m).Cost(1_000_000, 0, 5_000_000));
    }

    [Fact]
    public void ReportedCountsAreUsedAsTheyAreAndNotCalledEstimates()
    {
        ReplyUsage reply = Assert.Single(UsageReader.Replies(Thread(User("u", null, "hello"), Reply("a", "u", prompt: 1000, completion: 200, cached: 300))));
        Assert.False(reply.Estimated);
        Assert.False(reply.Partial);
        Assert.Equal((1000L, 200L, 300L), (reply.PromptTokens, reply.CompletionTokens, reply.CachedTokens));
    }

    [Fact]
    public void AReplyWithNoReportIsEstimatedFromTextAndNothingIsWrittenBack()
    {
        JObject thread = Thread(User("u", null, new string('a', 400)), Reply("a", "u", text: new string('b', 80)));
        string before = thread.ToString();
        ReplyUsage reply = Assert.Single(UsageReader.Replies(thread));
        Assert.True(reply.Estimated);
        Assert.Equal(100, reply.PromptTokens);
        Assert.Equal(20, reply.CompletionTokens);
        Assert.Equal(before, thread.ToString());
        UsageTotals totals = new();
        totals.Add(reply, new ModelPrice(1m, 1m));
        Assert.True(totals.CostEstimated);
        Assert.Equal(1, totals.EstimatedReplies);
    }

    [Fact]
    public void AnEstimateAfterASummaryCountsTheSummaryInsteadOfWhatItReplaced()
    {
        JObject thread = Thread(User("u1", null, new string('a', 40_000)), Reply("a1", "u1", prompt: 10_000, completion: 1), User("u2", "a1", "next"), Reply("a2", "u2", text: "ok"));
        long whole = UsageReader.Replies(thread).Single(r => r.MessageId == "a2").PromptTokens;
        thread["compactions"] = new JArray { new JObject { ["id"] = "c", ["upToMessageId"] = "a1", ["tipMessageId"] = "u2", ["summary"] = "short" } };
        long shortened = UsageReader.Replies(thread).Single(r => r.MessageId == "a2").PromptTokens;
        Assert.True(whole > 10_000);
        Assert.True(shortened < 100, $"{shortened}");
    }

    [Fact]
    public void AStoppedTurnThatReportedOnlySomeRoundsIsMarkedIncomplete()
    {
        JObject reply = Reply("a", null, prompt: 100, completion: 10);
        reply["rounds"] = new JArray { new JObject(), new JObject() };
        Assert.True(Assert.Single(UsageReader.Replies(Thread(reply))).Partial);
    }

    [Fact]
    public void EveryBranchCountsButToolRunsByHandAndMessagesNoModelWroteDoNot()
    {
        JObject manual = new() { ["id"] = "x", ["role"] = "assistant", ["content"] = "", ["meta"] = new JObject { ["manualTool"] = true } };
        JObject imported = new() { ["id"] = "y", ["role"] = "assistant", ["content"] = "from a file" };
        JObject thread = Thread(User("u", null, "q"), Reply("a", "u", prompt: 10, completion: 1), Reply("b", "u", prompt: 20, completion: 2), manual, imported);
        thread["activeLeafId"] = "b";
        Assert.Equal(["a", "b"], UsageReader.Replies(thread).Select(r => r.MessageId));
    }

    [Fact]
    public void AModelWithNoPriceShowsTokensAndNoCost()
    {
        JObject described = UsageService.Describe(Thread(Reply("a", null, model: "local-model", prompt: 100, completion: 10)), Priced("other-model"));
        Assert.Equal(JTokenType.Null, described["replies"][0]["cost"].Type);
        Assert.Equal(JTokenType.Null, described["replies"][0]["price"].Type);
        Assert.Equal(JTokenType.Null, described["totals"]["cost"].Type);
        Assert.Equal(110, described["totals"].Value<long>("totalTokens"));
        Assert.Equal(1, described["totals"].Value<int>("unpricedReplies"));
    }

    [Fact]
    public void AMixedChatCostsOnlyItsPricedRepliesAndSaysWhatItLeftOut()
    {
        JObject thread = Thread(Reply("a", null, prompt: 1_000_000, completion: 0), Reply("b", null, model: "unpriced", prompt: 500, completion: 50));
        JObject totals = (JObject)UsageService.Describe(thread, Priced())["totals"];
        Assert.Equal(2m, totals.Value<decimal>("cost"));
        Assert.Equal(1, totals.Value<int>("unpricedReplies"));
        Assert.Equal(550, totals.Value<long>("unpricedTokens"));
    }

    [Fact]
    public void ModelIdsMatchExactly()
    {
        Assert.NotNull(Priced("GPT-x").PriceFor("GPT-x"));
        Assert.Null(Priced("GPT-x").PriceFor("gpt-x"));
        Assert.Null(Priced("GPT-x").PriceFor(""));
    }

    [Fact]
    public void NothingIsPricedAndNoBudgetIsOnUntilTheUserSaysSo()
    {
        UsageSettings settings = UsageSettings.FromSettings([]);
        Assert.Empty(settings.Prices);
        Assert.False(settings.Budget.Enabled);
        Assert.Equal("USD", settings.Currency);
    }

    [Theory]
    [InlineData("""{"prices":{"m":{"input":1}}}""")]
    [InlineData("""{"prices":{"m":{"input":-1,"output":1}}}""")]
    [InlineData("""{"prices":{"m":{"input":"cheap","output":1}}}""")]
    [InlineData("""{"prices":{" ":{"input":1,"output":1}}}""")]
    [InlineData("""{"prices":[]}""")]
    [InlineData("""{"currency":"a label far too long to be one"}""")]
    [InlineData("""{"budget":{"enabled":"yes"}}""")]
    [InlineData("""{"budget":{"daily":-3}}""")]
    public void UnusableSettingsAreRefusedWithAReason(string patch)
    {
        Assert.Null(UsageSettings.FromSettings([]).With(JObject.Parse(patch), out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void SavingATableReplacesItAndLeavesThePartsNotSent()
    {
        UsageSettings first = Settings(JObject.Parse("""{"currency":" EUR ","prices":{"a":{"input":"1.5","output":2,"cached":""}},"budget":{"enabled":true,"daily":"3"}}"""));
        Assert.Equal(("EUR", 1.5m, null as decimal?), (first.Currency, first.Prices["a"].Input, first.Prices["a"].Cached));
        UsageSettings second = first.With(JObject.Parse("""{"prices":{"b":{"input":0,"output":0}}}"""), out _);
        Assert.Equal(["b"], second.Prices.Keys);
        Assert.Equal("EUR", second.Currency);
        Assert.Equal(3m, second.Budget.Daily);
        UsageSettings read = UsageSettings.FromSettings(new JObject { [UsageSettings.Key] = second.ToJson() });
        Assert.Equal(second.ToJson().ToString(), read.ToJson().ToString());
    }

    [Fact]
    public void AnOrdinarySettingsSaveCannotWriteThePriceTable()
    {
        JObject filtered = SettingsEndpoints.FilterIncoming(new JObject { [UsageSettings.Key] = new JObject(), ["preferredModel"] = "m" });
        Assert.Null(filtered[UsageSettings.Key]);
        Assert.NotNull(filtered["preferredModel"]);
    }

    [Theory]
    // A Wednesday afternoon UTC is already Thursday in Auckland (UTC+13 in March).
    [InlineData("Pacific/Auckland", "2026-03-04T12:00:00Z", "2026-03-04T11:00:00Z", "2026-03-01T11:00:00Z")]
    // Sunday 23:59 in New York still belongs to the week that began the Monday before.
    [InlineData("America/New_York", "2026-02-02T04:59:00Z", "2026-02-01T05:00:00Z", "2026-01-26T05:00:00Z")]
    // A minute later it is Monday, and a new week.
    [InlineData("America/New_York", "2026-02-02T05:00:00Z", "2026-02-02T05:00:00Z", "2026-02-02T05:00:00Z")]
    // Clocks went forward on Sunday 29 March: the week began at UTC midnight, the day after the change at 23:00 UTC.
    [InlineData("Europe/London", "2026-03-30T10:00:00Z", "2026-03-29T23:00:00Z", "2026-03-29T23:00:00Z")]
    [InlineData("Europe/London", "2026-03-29T10:00:00Z", "2026-03-29T00:00:00Z", "2026-03-23T00:00:00Z")]
    // Midnight did not exist in Sao Paulo on 4 November 2018: the day began at 01:00.
    [InlineData("America/Sao_Paulo", "2018-11-04T12:00:00Z", "2018-11-04T03:00:00Z", "2018-10-29T03:00:00Z")]
    [InlineData("UTC", "2026-03-04T00:00:00Z", "2026-03-04T00:00:00Z", "2026-03-02T00:00:00Z")]
    public void DaysAndWeeksBeginAtTheUsersMidnightAndMonday(string zone, string now, string day, string week)
    {
        UsageWindows windows = UsageWindows.At(DateTimeOffset.Parse(now), DateTimeZoneProviders.Tzdb[zone]);
        Assert.Equal(DateTimeOffset.Parse(day), windows.Day);
        Assert.Equal(DateTimeOffset.Parse(week), windows.Week);
    }

    [Fact]
    public void RepliesLandInThePeriodsTheirTimeFallsIn()
    {
        DateTimeZone zone = DateTimeZoneProviders.Tzdb["America/New_York"];
        UsageWindows windows = UsageWindows.At(DateTimeOffset.Parse("2026-03-04T20:00:00Z"), zone);
        // Parsed from text, as a saved thread is: the times become dates inside the JSON.
        JObject thread = JObject.Parse(Thread(
            Reply("lastWeek", null, prompt: 1, time: "2026-03-02T04:59:59Z"),
            Reply("monday", null, prompt: 10, time: "2026-03-02T05:00:00Z"),
            Reply("yesterday", null, prompt: 100, time: "2026-03-04T04:59:59.9999999Z"),
            Reply("today", null, prompt: 1000, time: "2026-03-04T05:00:00Z"),
            Reply("session", null, prompt: 10000, time: "2026-03-04T19:00:00Z")).ToString());
        Dictionary<string, UsageTotals> periods = UsageReport.Sum([UsageReader.Replies(thread)], Priced(), windows, DateTimeOffset.Parse("2026-03-04T18:00:00Z"));
        Assert.Equal(11000, periods["day"].PromptTokens);
        Assert.Equal(11110, periods["week"].PromptTokens);
        Assert.Equal(10000, periods["session"].PromptTokens);
        Assert.False(UsageReport.Sum([UsageReader.Replies(thread)], Priced(), windows).ContainsKey("session"));
    }

    [Fact]
    public void ASessionThatBeganBeforeThisWeekReachesBack()
    {
        UsageWindows windows = UsageWindows.At(DateTimeOffset.Parse("2026-03-04T20:00:00Z"), DateTimeZone.Utc);
        JObject thread = Thread(Reply("old", null, prompt: 5, time: "2026-02-27T00:00:00Z"));
        Dictionary<string, UsageTotals> periods = UsageReport.Sum([UsageReader.Replies(thread)], Priced(), windows, DateTimeOffset.Parse("2026-02-26T00:00:00Z"));
        Assert.Equal(5, periods["session"].PromptTokens);
        Assert.Equal(0, periods["week"].Replies);
    }

    [Theory]
    [InlineData("2026-03-04T09:00:00Z")]
    [InlineData("2026-03-04T11:00:00+02:00")]
    [InlineData("2026-03-04T04:00:00-05:00")]
    [InlineData("2026-03-04T09:00:00")]
    public void ATimeInARequestIsTheSameMomentWhateverItsOffset(string text)
    {
        // As a route receives it: JSON turns the text into a date.
        JToken token = JObject.Parse($$"""{"since":"{{text}}"}""")["since"];
        Assert.Equal(DateTimeOffset.Parse("2026-03-04T09:00:00Z"), UsageReader.ReadTime(token));
    }

    [Fact]
    public void OnlyChatsChangedSinceThePeriodBeganAreRead()
    {
        JArray index = JArray.Parse("""[{"id":"new","updatedAt":"2026-03-04T10:00:00Z"},{"id":"old","updatedAt":"2026-02-01T10:00:00Z"},{"id":"unknown"}]""");
        List<(string Id, string Stamp)> changed = UsageReport.ChangedSince(index, DateTimeOffset.Parse("2026-03-02T00:00:00Z"));
        Assert.Equal(["new", "unknown"], changed.Select(c => c.Id));
        Assert.Equal(["2026-03-04T10:00:00.0000000Z", ""], changed.Select(c => c.Stamp));
    }

    [Fact]
    public void AChatIsReadAgainOnlyWhenItHasChanged()
    {
        UsageCache cache = new();
        int loads = 0;
        JObject Load(long prompt)
        {
            loads++;
            return Thread(Reply("a", null, prompt: prompt));
        }
        Assert.Equal(1, cache.Replies("u|t", "s1", () => Load(1))[0].PromptTokens);
        Assert.Equal(1, cache.Replies("u|t", "s1", () => Load(2))[0].PromptTokens);
        Assert.Equal(3, cache.Replies("u|t", "s2", () => Load(3))[0].PromptTokens);
        Assert.Equal(4, cache.Replies("v|t", "s2", () => Load(4))[0].PromptTokens);
        Assert.Equal(3, loads);
        // A row with no stamp is never remembered, and a chat that is gone has no replies.
        cache.Replies("u|x", "", () => Load(5));
        cache.Replies("u|x", "", () => Load(5));
        Assert.Equal(5, loads);
        Assert.Empty(cache.Replies("u|t", "s3", () => null));
    }

    [Fact]
    public void TheSessionBeginsSoManySecondsAgoOrAtAServerTime()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-03-04T12:00:00Z");
        DateTimeOffset? Read(string json)
        {
            Assert.True(UsageReport.TryReadSessionStart(JObject.Parse(json), now, out DateTimeOffset? start, out _), json);
            return start;
        }
        Assert.Equal(now.AddSeconds(-90), Read("""{"sessionSeconds":90}"""));
        Assert.Equal(now.AddSeconds(-90), Read("""{"sessionSeconds":"90","since":"2020-01-01T00:00:00Z"}"""));
        Assert.Equal(now.AddHours(-1), Read("""{"since":"2026-03-04T13:00:00+02:00"}"""));
        Assert.Null(Read("""{"since":""}"""));
        Assert.Null(Read("{}"));
        foreach (string bad in new[] { """{"sessionSeconds":-1}""", """{"sessionSeconds":"soon"}""", """{"sessionSeconds":1e30}""", """{"since":"yesterday"}""" })
        {
            Assert.False(UsageReport.TryReadSessionStart(JObject.Parse(bad), now, out _, out string error), bad);
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Fact]
    public void AnIncompleteReportMarksTheReplyAndTheTotals()
    {
        JObject reply = Reply("a", null, prompt: 100, completion: 10);
        reply["meta"]["usage"]["incomplete"] = true;
        JObject totals = (JObject)UsageService.Describe(Thread(reply), Priced())["totals"];
        Assert.Equal(1, totals.Value<int>("partialReplies"));
        Assert.Equal(0, totals.Value<int>("estimatedReplies"));
    }

    [Fact]
    public void EveryEstimateInALongChatCountsWhatCameBeforeItUnderTheSummaryInForce()
    {
        List<JObject> messages = [];
        string parent = null;
        for (int i = 0; i < 6; i++)
        {
            messages.Add(User($"u{i}", parent, new string('a', 400)));
            messages.Add(Reply($"a{i}", $"u{i}", text: new string('b', 40)));
            parent = $"a{i}";
        }
        JObject thread = Thread([.. messages]);
        Assert.Equal([100, 210, 320, 430, 540, 650], UsageReader.Replies(thread).Select(r => r.PromptTokens));
        // Two summaries: the later one in the list wins from the message it was made after.
        thread["compactions"] = new JArray
        {
            new JObject { ["id"] = "c1", ["upToMessageId"] = "a0", ["tipMessageId"] = "u2", ["summary"] = "one" },
            new JObject { ["id"] = "c2", ["upToMessageId"] = "a2", ["tipMessageId"] = "u4", ["summary"] = "two" },
        };
        long[] prompts = [.. UsageReader.Replies(thread).Select(r => r.PromptTokens)];
        long summary = ContextWindowService.EstimateTokens(CompactionPlanner.SummaryText("one"));
        Assert.Equal([100, 210, 210 + summary, 320 + summary, 210 + summary, 320 + summary], prompts);
    }

    [Fact]
    public void TheBudgetWarnsOnceALimitIsReachedAndOnlyWhenSwitchedOn()
    {
        UsageTotals day = new();
        day.Add(new ReplyUsage("a", "m", 1_000_000, 0, 0, false, false, null), new ModelPrice(2m, 0m));
        Dictionary<string, UsageTotals> periods = new() { ["day"] = day, ["week"] = day };
        Assert.Empty(UsageReport.Warnings(new UsageBudget(false, 1m, 1m), periods));
        Assert.Empty(UsageReport.Warnings(new UsageBudget(true, 2.01m, 0m), periods));
        JArray warnings = UsageReport.Warnings(new UsageBudget(true, 2m, 1m), periods);
        Assert.Equal(["day", "week"], warnings.Select(w => w.Value<string>("period")));
        UsageTotals unpriced = new();
        unpriced.Add(new ReplyUsage("b", "x", 5, 5, 0, false, false, null), null);
        Assert.Empty(UsageReport.Warnings(new UsageBudget(true, 0.01m, 0m), new Dictionary<string, UsageTotals> { ["day"] = unpriced }));
    }
}
