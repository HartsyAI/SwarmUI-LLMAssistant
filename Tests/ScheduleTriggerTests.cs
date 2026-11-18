using Hartsy.Extensions.LLMAssistant.Services.Schedules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>When a schedule runs: cron parsing and its day rules, the cadence of "every", one-time triggers, time zones with daylight saving, and what a trigger is allowed to be.
/// All with fixed dates; nothing here reads a clock.</summary>
public class ScheduleTriggerTests
{
    /// <summary>A UTC instant.</summary>
    private static DateTime Utc(string text) => DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>A cron trigger in a zone (UTC by default).</summary>
    private static ScheduleTrigger Cron(string expression, string zone = "UTC")
    {
        Assert.True(ScheduleTrigger.TryFromJson(new JObject { ["kind"] = "cron", ["cron"] = expression }, zone, Utc("2026-01-01T00:00:00Z"), out ScheduleTrigger trigger, out string error), error);
        return trigger;
    }

    /// <summary>The next run of a cron expression after a time.</summary>
    private static DateTime? Next(string expression, string after, string zone = "UTC") => Cron(expression, zone).NextAfter(Utc(after));

    // ---- Cron fields ----

    [Fact]
    public void Steps_lists_and_ranges_pick_the_minutes_they_name()
    {
        Assert.Equal(Utc("2026-11-18T10:15:00Z"), Next("*/15 * * * *", "2026-11-18T10:07:00Z"));
        Assert.Equal(Utc("2026-11-18T11:00:00Z"), Next("*/15 * * * *", "2026-11-18T10:45:00Z"));
        Assert.Equal(Utc("2026-11-18T10:30:00Z"), Next("0,30 * * * *", "2026-11-18T10:00:00Z"));
        Assert.Equal(Utc("2026-11-18T10:20:00Z"), Next("10-50/10 * * * *", "2026-11-18T10:10:30Z"));
    }

    [Fact]
    public void The_next_run_is_always_after_the_time_asked_about_even_when_that_time_is_a_run()
    {
        Assert.Equal(Utc("2026-11-19T09:00:00Z"), Next("0 9 * * *", "2026-11-18T09:00:00Z"));
        Assert.Equal(Utc("2026-11-18T09:00:00Z"), Next("0 9 * * *", "2026-11-18T08:59:59Z"));
        // Seconds inside the minute do not matter.
        Assert.Equal(Utc("2026-11-19T09:00:00Z"), Next("0 9 * * *", "2026-11-18T09:00:45Z"));
    }

    [Fact]
    public void Weekdays_and_names_work_and_sunday_is_zero_or_seven()
    {
        // 2026-11-20 is a Friday.
        Assert.Equal(Utc("2026-11-23T09:00:00Z"), Next("0 9 * * 1-5", "2026-11-20T17:00:00Z"));
        Assert.Equal(Utc("2026-11-23T06:30:00Z"), Next("30 6 * * mon-fri", "2026-11-20T17:00:00Z"));
        Assert.Equal(Utc("2026-11-22T00:00:00Z"), Next("0 0 * * 0", "2026-11-20T17:00:00Z"));
        Assert.Equal(Utc("2026-11-22T00:00:00Z"), Next("0 0 * * 7", "2026-11-20T17:00:00Z"));
        Assert.Equal(Utc("2026-12-01T00:00:00Z"), Next("0 0 1 dec *", "2026-11-20T17:00:00Z"));
    }

    [Fact]
    public void When_both_day_of_month_and_weekday_are_given_either_one_matches()
    {
        // The 15th, or any Monday. 2026-11-01 is a Sunday, so the first match is Monday the 2nd.
        Assert.Equal(Utc("2026-11-02T00:00:00Z"), Next("0 0 15 * 1", "2026-11-01T00:00:00Z"));
        // From Monday the 30th (itself a run), the next is the following Monday, December 7th.
        Assert.Equal(Utc("2026-12-07T00:00:00Z"), Next("0 0 15 * 1", "2026-11-30T00:00:00Z"));
        // With one of them a star, only the other counts.
        Assert.Equal(Utc("2026-11-15T00:00:00Z"), Next("0 0 15 * *", "2026-11-01T00:00:00Z"));
    }

    [Fact]
    public void An_expression_that_never_fires_ends_the_search_with_nothing()
    {
        Assert.Null(Next("0 0 31 2 *", "2026-01-01T00:00:00Z"));
        Assert.Null(Next("0 0 30 2 *", "2026-01-01T00:00:00Z"));
    }

    [Fact]
    public void Leap_days_are_found()
    {
        Assert.Equal(Utc("2028-02-29T00:00:00Z"), Next("0 0 29 2 *", "2026-03-01T00:00:00Z"));
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("")]
    [InlineData("61 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("*/x * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("abc * * * *")]
    [InlineData(", * * * *")]
    [InlineData("1,,2 * * * *")]
    public void A_malformed_expression_is_refused_with_a_reason(string expression)
    {
        Assert.False(CronExpression.TryParse(expression, out CronExpression cron, out string error));
        Assert.Null(cron);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Extra_spaces_are_tidied_in_the_text_that_is_kept()
    {
        Assert.True(CronExpression.TryParse("  0   9 *  * 1-5 ", out CronExpression cron, out _));
        Assert.Equal("0 9 * * 1-5", cron.Text);
    }

    // ---- Time zones ----

    [Fact]
    public void A_cron_is_read_in_the_schedules_time_zone()
    {
        // 09:00 in New York is 14:00 UTC in winter (EST, -5) and 13:00 UTC in summer (EDT, -4).
        Assert.Equal(Utc("2026-01-15T14:00:00Z"), Next("0 9 * * *", "2026-01-15T00:00:00Z", "America/New_York"));
        Assert.Equal(Utc("2026-07-15T13:00:00Z"), Next("0 9 * * *", "2026-07-15T00:00:00Z", "America/New_York"));
    }

    [Fact]
    public void A_time_that_does_not_exist_when_clocks_go_forward_is_skipped_for_that_day()
    {
        // 2026-03-08, 02:00 to 02:59 in New York does not exist. 02:30 that day is skipped, and the next run is the 9th at 02:30 EDT (06:30 UTC).
        Assert.Equal(Utc("2026-03-09T06:30:00Z"), Next("30 2 * * *", "2026-03-07T12:00:00Z", "America/New_York"));
    }

    [Fact]
    public void A_time_that_happens_twice_when_clocks_go_back_runs_once_at_its_first_occurrence()
    {
        // 2026-11-01, 01:30 in New York happens at 05:30 UTC (EDT) and again at 06:30 UTC (EST). It runs at the first only.
        Assert.Equal(Utc("2026-11-01T05:30:00Z"), Next("30 1 * * *", "2026-10-31T12:00:00Z", "America/New_York"));
        Assert.Equal(Utc("2026-11-02T06:30:00Z"), Next("30 1 * * *", "2026-11-01T05:30:00Z", "America/New_York"));
        // Asked from inside the repeated hour, the repeat is not a second run either.
        Assert.Equal(Utc("2026-11-02T06:30:00Z"), Next("30 1 * * *", "2026-11-01T06:10:00Z", "America/New_York"));
    }

    [Fact]
    public void An_unknown_time_zone_is_refused_and_not_turned_into_utc()
    {
        Assert.False(ScheduleTrigger.TryFromJson(new JObject { ["kind"] = "cron", ["cron"] = "0 9 * * *" }, "Mars/Olympus", Utc("2026-01-01T00:00:00Z"), out _, out string error));
        Assert.Contains("Mars/Olympus", error);
        Assert.True(ScheduleTrigger.TryZone(null, out NodaTime.DateTimeZone zone, out _));
        Assert.Equal(NodaTime.DateTimeZone.Utc, zone);
        Assert.True(ScheduleTrigger.TryZone("utc", out _, out _));
        // The name's case does not matter, and the stored form is the database's own.
        Assert.True(ScheduleTrigger.TryZone("europe/paris", out NodaTime.DateTimeZone paris, out _));
        Assert.Equal("Europe/Paris", paris.Id);
        // The operating system's own names are not the ones used.
        Assert.False(ScheduleTrigger.TryZone("Romance Standard Time", out _, out _));
    }

    // ---- Every, and once ----

    [Fact]
    public void Every_keeps_a_steady_cadence_from_its_anchor()
    {
        ScheduleTrigger trigger = Every(30, "2026-11-18T10:00:00Z");
        Assert.Equal(Utc("2026-11-18T10:30:00Z"), trigger.NextAfter(Utc("2026-11-18T10:00:00Z")));
        Assert.Equal(Utc("2026-11-18T10:30:00Z"), trigger.NextAfter(Utc("2026-11-18T10:29:59Z")));
        Assert.Equal(Utc("2026-11-18T11:00:00Z"), trigger.NextAfter(Utc("2026-11-18T10:30:00Z")));
        // A restart days later does not shift it: it is still on the half hour.
        Assert.Equal(Utc("2026-11-21T04:30:00Z"), trigger.NextAfter(Utc("2026-11-21T04:12:00Z")));
        // Before the anchor, the first run is the anchor.
        Assert.Equal(Utc("2026-11-18T10:00:00Z"), trigger.NextAfter(Utc("2026-11-18T09:00:00Z")));
    }

    [Fact]
    public void A_one_time_trigger_has_a_next_run_only_until_it_has_passed()
    {
        ScheduleTrigger trigger = At("2026-12-01T09:00:00Z");
        Assert.Equal(Utc("2026-12-01T09:00:00Z"), trigger.NextAfter(Utc("2026-11-30T00:00:00Z")));
        Assert.Null(trigger.NextAfter(Utc("2026-12-01T09:00:00Z")));
        Assert.Null(trigger.NextAfter(Utc("2027-01-01T00:00:00Z")));
    }

    [Fact]
    public void A_preview_lists_the_next_runs_in_order()
    {
        List<DateTime> times = Cron("0 9 * * 1-5").Preview(Utc("2026-11-20T10:00:00Z"), 4);
        Assert.Equal([Utc("2026-11-23T09:00:00Z"), Utc("2026-11-24T09:00:00Z"), Utc("2026-11-25T09:00:00Z"), Utc("2026-11-26T09:00:00Z")], times);
        Assert.Single(At("2026-12-01T09:00:00Z").Preview(Utc("2026-11-01T00:00:00Z"), 5));
        Assert.Empty(At("2020-12-01T09:00:00Z").Preview(Utc("2026-11-01T00:00:00Z"), 5));
    }

    // ---- What a trigger may be ----

    /// <summary>An "every" trigger with an anchor.</summary>
    private static ScheduleTrigger Every(int minutes, string anchor)
    {
        Assert.True(ScheduleTrigger.TryFromJson(new JObject { ["kind"] = "every", ["everyMinutes"] = minutes, ["anchor"] = anchor }, "UTC", Utc("2026-01-01T00:00:00Z"), out ScheduleTrigger trigger, out string error), error);
        return trigger;
    }

    /// <summary>A one-time trigger.</summary>
    private static ScheduleTrigger At(string at)
    {
        Assert.True(ScheduleTrigger.TryFromJson(new JObject { ["kind"] = "at", ["at"] = at }, "UTC", Utc("2026-01-01T00:00:00Z"), out ScheduleTrigger trigger, out string error), error);
        return trigger;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(60 * 24 * 30 + 1)]
    public void An_interval_outside_the_allowed_range_is_refused(int minutes)
    {
        Assert.False(ScheduleTrigger.TryFromJson(new JObject { ["kind"] = "every", ["everyMinutes"] = minutes }, "UTC", Utc("2026-01-01T00:00:00Z"), out _, out string error));
        Assert.Contains("minutes", error);
    }

    [Fact]
    public void The_shortest_and_longest_intervals_are_allowed()
    {
        Assert.NotNull(Every(ScheduleLimits.MinIntervalMinutes, "2026-01-01T00:00:00Z"));
        Assert.NotNull(Every(ScheduleLimits.MaxEveryMinutes, "2026-01-01T00:00:00Z"));
    }

    [Theory]
    [InlineData("every", "soon")]
    [InlineData("at", "next tuesday")]
    [InlineData("daily", "")]
    [InlineData(null, "")]
    public void A_trigger_that_is_not_made_of_what_its_kind_needs_is_refused(string kind, string value)
    {
        JObject json = new() { ["kind"] = kind, ["everyMinutes"] = value, ["at"] = value, ["cron"] = value };
        Assert.False(ScheduleTrigger.TryFromJson(json, "UTC", Utc("2026-01-01T00:00:00Z"), out _, out string error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.False(ScheduleTrigger.TryFromJson(null, "UTC", Utc("2026-01-01T00:00:00Z"), out _, out _));
    }

    [Theory]
    [InlineData("* * * * *", false)]
    [InlineData("*/2 * * * *", false)]
    [InlineData("*/4 * * * *", false)]
    [InlineData("*/5 * * * *", true)]
    [InlineData("0,3 * * * *", false)]
    [InlineData("0,30 * * * *", true)]
    [InlineData("0 * * * *", true)]
    [InlineData("0 9 * * 1-5", true)]
    [InlineData("0 0 29 2 *", true)]
    [InlineData("0 0 31 2 *", true)]
    public void A_cron_whose_runs_come_closer_than_the_shortest_interval_is_refused(string expression, bool allowed)
    {
        bool ok = ScheduleTrigger.TryFromJson(new JObject { ["kind"] = "cron", ["cron"] = expression }, "UTC", Utc("2026-01-01T00:00:00Z"), out _, out string error);
        Assert.Equal(allowed, ok);
        if (!allowed)
        {
            Assert.Contains("at least 5 minutes", error);
        }
    }

    [Fact]
    public void A_trigger_is_stored_and_read_back_the_same()
    {
        ScheduleTrigger every = Every(90, "2026-11-18T10:00:00Z");
        JObject stored = every.ToJson();
        Assert.Equal("every", stored["kind"]);
        Assert.Equal(90, stored["everyMinutes"]);
        Assert.True(ScheduleTrigger.TryFromJson(stored, "UTC", Utc("2027-01-01T00:00:00Z"), out ScheduleTrigger again, out _));
        // The anchor survived: the cadence did not restart at the time it was read.
        Assert.Equal(every.NextAfter(Utc("2026-11-19T00:00:00Z")), again.NextAfter(Utc("2026-11-19T00:00:00Z")));

        Assert.Equal("0 9 * * 1-5", Cron("0 9 * * 1-5").ToJson()["cron"]);
        Assert.Equal(Utc("2026-12-01T09:00:00Z"), ScheduleTrigger.ReadDate(At("2026-12-01T09:00:00Z").ToJson()["at"]));
    }

    [Fact]
    public void Dates_are_read_as_utc_instants_with_or_without_an_offset()
    {
        Assert.Equal(Utc("2026-12-01T09:00:00Z"), ScheduleTrigger.ReadDate(new JValue("2026-12-01T09:00:00Z")));
        Assert.Equal(Utc("2026-12-01T09:00:00Z"), ScheduleTrigger.ReadDate(new JValue("2026-12-01T10:00:00+01:00")));
        Assert.Equal(Utc("2026-12-01T09:00:00Z"), ScheduleTrigger.ReadDate(new JValue("2026-12-01T09:00:00")));
        Assert.Null(ScheduleTrigger.ReadDate(new JValue("tomorrow")));
        Assert.Null(ScheduleTrigger.ReadDate(null));
        Assert.Null(ScheduleTrigger.ReadDate(new JValue(5)));
    }

    [Fact]
    public void A_trigger_describes_itself_for_a_list()
    {
        Assert.Equal("every 30 minutes", Every(30, "2026-01-01T00:00:00Z").Describe());
        Assert.Equal("every 2 hour(s)", Every(120, "2026-01-01T00:00:00Z").Describe());
        Assert.Equal("every 1 day(s)", Every(1440, "2026-01-01T00:00:00Z").Describe());
        Assert.Equal("cron 0 9 * * 1-5 (UTC)", Cron("0 9 * * 1-5").Describe());
        Assert.StartsWith("once at 2026-12-01", At("2026-12-01T09:00:00Z").Describe());
    }
}
