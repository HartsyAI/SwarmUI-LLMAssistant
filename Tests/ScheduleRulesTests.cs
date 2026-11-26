using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Schedules;
using Hartsy.Extensions.LLMAssistant.Tools;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>How a request becomes a stored schedule, who may make which kind, when one is due, and how it moves on: the parts of scheduling that need no user, storage or clock.</summary>
public class ScheduleRulesTests
{
    /// <summary>A UTC instant.</summary>
    private static DateTime Utc(string text) => DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>The time the tests are run at.</summary>
    private static readonly DateTime Now = Utc("2026-11-18T10:00:00Z");

    /// <summary>Tools a maker could use.</summary>
    private static readonly string[] Tools = ["web_search", "file_read", "file_write", ToolConstants.ScheduleCreate, ToolConstants.SessionStart];

    /// <summary>A request for "every 30 minutes", with changes.</summary>
    private static JObject Request(Action<JObject> change = null)
    {
        JObject request = new()
        {
            ["title"] = "Check the feed",
            ["prompt"] = "Look at the feed and summarize it.",
            ["trigger"] = new JObject { ["kind"] = "every", ["everyMinutes"] = 30 },
        };
        change?.Invoke(request);
        return request;
    }

    /// <summary>Builds as the user.</summary>
    private static (JObject Schedule, string Error) AsUser(JObject request, JObject existing = null, string maxMode = null)
        => ScheduleRules.Build(request, existing, ScheduleSources.User, "", maxMode, Tools, Now);

    /// <summary>Builds as the assistant, from chat "chat1".</summary>
    private static (JObject Schedule, string Error) AsAssistant(JObject request, string maxMode = null)
        => ScheduleRules.Build(request, null, ScheduleSources.Assistant, "chat1", maxMode, Tools, Now);

    // ---- Building ----

    [Fact]
    public void A_users_schedule_is_saved_on_with_its_next_run_worked_out()
    {
        (JObject schedule, string error) = AsUser(Request());
        Assert.Null(error);
        Assert.True(schedule["enabled"].Value<bool>());
        Assert.Equal("user", schedule["source"]);
        Assert.Equal(32, schedule["id"].ToString().Length);
        // Every 30 minutes from the moment it was made.
        Assert.Equal(Utc("2026-11-18T10:30:00Z"), ScheduleTrigger.ReadDate(schedule["nextRunAt"]));
        Assert.Equal(0, schedule["runCount"]);
        Assert.Equal("UTC", schedule["timezone"]);
    }

    [Fact]
    public void The_mark_of_a_schedule_made_with_documents_in_reach_stays_when_the_user_saves_it_and_no_request_sets_or_clears_it()
    {
        (JObject made, _) = AsAssistant(Request(r => r[ScheduleFields.FromDocuments] = true));
        Assert.Null(made[ScheduleFields.FromDocuments]);
        made[ScheduleFields.FromDocuments] = true;
        (JObject saved, string error) = AsUser(Request(r => r[ScheduleFields.FromDocuments] = false), made);
        Assert.Null(error);
        Assert.True(saved[ScheduleFields.FromDocuments].Value<bool>());
    }

    [Fact]
    public void A_users_schedule_can_be_saved_off_and_then_has_no_next_run()
    {
        (JObject schedule, _) = AsUser(Request(r => r["enabled"] = false));
        Assert.False(schedule["enabled"].Value<bool>());
        Assert.Null(schedule["nextRunAt"]);
    }

    [Fact]
    public void What_the_assistant_makes_is_always_off_whatever_it_asks_for()
    {
        (JObject schedule, string error) = AsAssistant(Request(r => r["enabled"] = true));
        Assert.Null(error);
        Assert.False(schedule["enabled"].Value<bool>());
        Assert.Equal("assistant", schedule["source"]);
        Assert.Equal("chat1", schedule["createdBy"]);
        Assert.Null(schedule["nextRunAt"]);
    }

    [Fact]
    public void The_user_turning_on_a_schedule_the_assistant_made_keeps_where_it_came_from()
    {
        (JObject made, _) = AsAssistant(Request());
        (JObject turnedOn, string error) = AsUser(Request(r => r["enabled"] = true), made);
        Assert.Null(error);
        Assert.True(turnedOn["enabled"].Value<bool>());
        Assert.Equal("assistant", turnedOn["source"]);
        Assert.Equal("chat1", turnedOn["createdBy"]);
        Assert.Equal(made["id"], turnedOn["id"]);
        Assert.NotNull(turnedOn["nextRunAt"]);
    }

    [Fact]
    public void Editing_a_schedule_without_a_switch_leaves_it_as_it_was()
    {
        (JObject on, _) = AsUser(Request());
        (JObject edited, _) = AsUser(Request(r => r["title"] = "Renamed"), on);
        Assert.True(edited["enabled"].Value<bool>());
        Assert.Equal("Renamed", edited["title"]);
        (JObject off, _) = AsUser(Request(r => r["enabled"] = false));
        (JObject stillOff, _) = AsUser(Request(), off);
        Assert.False(stillOff["enabled"].Value<bool>());
    }

    [Fact]
    public void Editing_keeps_what_the_scheduler_recorded_and_the_cadence_of_every()
    {
        (JObject first, _) = AsUser(Request());
        first["runCount"] = 7;
        first["lastRunAt"] = "2026-11-18T10:30:00.0000000Z";
        first["threadId"] = "host1";
        // The same "every", edited much later: still counting from when it was made.
        (JObject edited, _) = ScheduleRules.Build(Request(r => r["title"] = "Again"), first, ScheduleSources.User, "", null, Tools, Utc("2026-11-20T04:12:00Z"));
        Assert.Equal(7, edited["runCount"]);
        Assert.Equal("host1", edited["threadId"]);
        Assert.Equal(first["lastRunAt"], edited["lastRunAt"]);
        Assert.Equal(first["createdAt"], edited["createdAt"]);
        Assert.Equal(Utc("2026-11-20T04:30:00Z"), ScheduleTrigger.ReadDate(edited["nextRunAt"]));
        // Changing the kind of trigger starts it afresh.
        (JObject cron, _) = ScheduleRules.Build(Request(r => r["trigger"] = new JObject { ["kind"] = "cron", ["cron"] = "0 9 * * *" }), first, ScheduleSources.User, "", null, Tools, Now);
        Assert.Equal("cron", cron["trigger"]["kind"]);
    }

    // ---- What a run may use ----

    [Fact]
    public void A_schedule_can_use_only_tools_its_maker_could_and_never_a_schedule_or_session_tool_it_was_barred_from()
    {
        (JObject all, _) = AsUser(Request());
        // Every tool the maker had, except the ones that make schedules.
        Assert.Equal(["file_read", "file_write", "session_start", "web_search"], all["toolIds"].Select(t => t.ToString()));

        (JObject some, _) = AsUser(Request(r => r["toolIds"] = new JArray("web_search", "shell_exec", ToolConstants.ScheduleCreate)));
        Assert.Equal(["web_search"], some["toolIds"].Select(t => t.ToString()));

        (JObject none, _) = AsUser(Request(r => r["toolIds"] = new JArray()));
        Assert.Empty(none["toolIds"]);
    }

    [Fact]
    public void Every_run_is_barred_from_making_schedules()
    {
        Assert.Equal(ToolConstants.ScheduleIds, ScheduleRules.BarredFromRuns);
        Assert.Equal(3, ScheduleRules.BarredFromRuns.Length);
    }

    // ---- Approval ----

    [Theory]
    [InlineData(null, null, "ask")]
    [InlineData("auto", null, "auto")]
    [InlineData("plan", null, "plan")]
    [InlineData("nonsense", null, "ask")]
    [InlineData("auto", "ask", "ask")]
    [InlineData("auto", "plan", "plan")]
    [InlineData("ask", "auto", "ask")]
    [InlineData(null, "plan", "plan")]
    public void A_schedule_is_asked_before_anything_unless_it_says_otherwise_and_is_never_looser_than_its_maker(string asked, string maxMode, string expected)
    {
        (JObject schedule, _) = AsUser(Request(r => r["approvalMode"] = asked), null, maxMode);
        Assert.Equal(expected, schedule["approvalMode"]);
    }

    // ---- What is refused ----

    [Theory]
    [InlineData("", "do it", "title and prompt are required")]
    [InlineData("T", "", "title and prompt are required")]
    [InlineData("   ", "do it", "title and prompt are required")]
    public void A_schedule_needs_a_title_and_a_prompt(string title, string prompt, string expected)
    {
        (JObject schedule, string error) = AsUser(Request(r => { r["title"] = title; r["prompt"] = prompt; }));
        Assert.Null(schedule);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void A_prompt_over_the_limit_is_refused()
    {
        (JObject schedule, string error) = AsUser(Request(r => r["prompt"] = new string('x', ScheduleLimits.MaxPromptChars + 1)));
        Assert.Null(schedule);
        Assert.Contains("too long", error);
        Assert.NotNull(AsUser(Request(r => r["prompt"] = new string('x', ScheduleLimits.MaxPromptChars))).Schedule);
    }

    [Fact]
    public void A_trigger_that_is_not_allowed_is_refused_with_its_reason()
    {
        (_, string every) = AsUser(Request(r => r["trigger"] = new JObject { ["kind"] = "every", ["everyMinutes"] = 1 }));
        Assert.Contains("minutes", every);
        (_, string cron) = AsUser(Request(r => r["trigger"] = new JObject { ["kind"] = "cron", ["cron"] = "* * * * *" }));
        Assert.Contains("at least 5 minutes", cron);
        (_, string none) = AsUser(Request(r => r.Remove("trigger")));
        Assert.Contains("trigger", none);
        (_, string zone) = AsUser(Request(r => { r["trigger"] = new JObject { ["kind"] = "cron", ["cron"] = "0 9 * * *" }; r["timezone"] = "Nowhere/Land"; }));
        Assert.Contains("Nowhere/Land", zone);
    }

    [Fact]
    public void A_one_time_schedule_in_the_past_cannot_be_switched_on_but_can_be_saved_off()
    {
        JObject past = new() { ["kind"] = "at", ["at"] = "2020-01-01T00:00:00Z" };
        (JObject on, string error) = AsUser(Request(r => r["trigger"] = past));
        Assert.Null(on);
        Assert.Contains("already passed", error);
        (JObject off, _) = AsUser(Request(r => { r["trigger"] = past; r["enabled"] = false; }));
        Assert.NotNull(off);
        // The assistant's are saved off anyway, so one in the past is saved as it is (the user will see it is over).
        Assert.NotNull(AsAssistant(Request(r => r["trigger"] = past)).Schedule);
    }

    [Fact]
    public void A_title_is_one_line_and_bounded()
    {
        (JObject schedule, _) = AsUser(Request(r => r["title"] = "  Daily\n  digest  "));
        Assert.Equal("Daily digest", schedule["title"]);
        Assert.Equal(ScheduleLimits.MaxTitleChars, ScheduleRules.CleanTitle(new string('t', 400)).Length);
        Assert.Equal("", ScheduleRules.CleanTitle(null));
    }

    // ---- Due or not ----

    /// <summary>A stored schedule that is on and due at a time.</summary>
    private static JObject Due(string next, bool enabled = true) => new() { ["enabled"] = enabled, ["nextRunAt"] = next };

    [Fact]
    public void A_schedule_is_due_once_its_time_has_come()
    {
        Assert.Equal(ScheduleDecision.NotDue, ScheduleRules.Decide(Due("2026-11-18T10:00:01Z"), Now));
        Assert.Equal(ScheduleDecision.Run, ScheduleRules.Decide(Due("2026-11-18T10:00:00Z"), Now));
        Assert.Equal(ScheduleDecision.Run, ScheduleRules.Decide(Due("2026-11-18T09:59:00Z"), Now));
    }

    [Fact]
    public void A_schedule_that_is_off_or_has_no_next_time_is_never_due()
    {
        Assert.Equal(ScheduleDecision.NotDue, ScheduleRules.Decide(Due("2026-11-18T09:00:00Z", enabled: false), Now));
        Assert.Equal(ScheduleDecision.NotDue, ScheduleRules.Decide(new JObject { ["enabled"] = true }, Now));
        Assert.Equal(ScheduleDecision.NotDue, ScheduleRules.Decide(null, Now));
        Assert.Equal(ScheduleDecision.NotDue, ScheduleRules.Decide(new JObject(), Now));
    }

    [Fact]
    public void A_time_missed_by_longer_than_the_catch_up_window_is_skipped_not_made_up()
    {
        Assert.Equal(ScheduleDecision.Run, ScheduleRules.Decide(Due("2026-11-18T04:00:00Z"), Now));
        Assert.Equal(ScheduleDecision.Missed, ScheduleRules.Decide(Due("2026-11-18T03:59:59Z"), Now));
        Assert.Equal(ScheduleDecision.Missed, ScheduleRules.Decide(Due("2026-11-01T00:00:00Z"), Now));
    }

    // ---- Moving on ----

    [Fact]
    public void After_a_due_time_a_schedule_moves_to_its_next_run_after_now_with_no_backlog()
    {
        (JObject schedule, _) = AsUser(Request());
        // The server was down for two days: the next run is the first one after now, not one per missed time.
        ScheduleRules.Advance(schedule, Utc("2026-11-20T09:05:00Z"));
        Assert.Equal(Utc("2026-11-20T09:30:00Z"), ScheduleTrigger.ReadDate(schedule["nextRunAt"]));
        Assert.True(schedule["enabled"].Value<bool>());
    }

    [Fact]
    public void A_one_time_schedule_switches_itself_off_once_it_has_run()
    {
        (JObject schedule, _) = AsUser(Request(r => r["trigger"] = new JObject { ["kind"] = "at", ["at"] = "2026-11-18T11:00:00Z" }));
        Assert.Equal(ScheduleDecision.NotDue, ScheduleRules.Decide(schedule, Now));
        Assert.Equal(ScheduleDecision.Run, ScheduleRules.Decide(schedule, Utc("2026-11-18T11:00:00Z")));
        ScheduleRules.Advance(schedule, Utc("2026-11-18T11:00:00Z"));
        Assert.False(schedule["enabled"].Value<bool>());
        Assert.Null(schedule["nextRunAt"]);
        Assert.Equal(ScheduleDecision.NotDue, ScheduleRules.Decide(schedule, Utc("2026-11-19T11:00:00Z")));
    }

    [Fact]
    public void A_run_is_named_for_its_schedule_and_when_it_started()
    {
        Assert.Equal("Check the feed · 2026-11-18 10:00 UTC", ScheduleRules.RunTitle("Check the feed", Now));
        Assert.True(ScheduleRules.RunTitle(new string('x', 200), Now).Length <= ScheduleLimits.MaxTitleChars);
    }

    // ---- The tools ----

    [Fact]
    public void The_schedule_tools_are_off_by_default_and_not_part_of_any_assistants_starting_list()
    {
        JObject defaults = ToolRegistryService.BuildDefaultTools();
        foreach (string id in ToolConstants.ScheduleIds)
        {
            Assert.False(defaults[id]["enabled"].Value<bool>());
            Assert.Equal(id, defaults[id]["handlerId"]);
            Assert.DoesNotContain(id, ToolConstants.BuiltInIds);
        }
        Assert.Equal(["title", "prompt"], defaults[ToolConstants.ScheduleCreate]["parameters"]["required"].Select(t => t.ToString()));
        Assert.Equal(["id"], defaults[ToolConstants.ScheduleCancel]["parameters"]["required"].Select(t => t.ToString()));
        Assert.Empty(defaults[ToolConstants.ScheduleList]["parameters"]["required"]);
    }

    [Fact]
    public void Making_a_schedule_counts_as_running_something_and_listing_does_not()
    {
        Assert.Equal(ToolRisk.Exec, new ScheduleCreateTool().Risk);
        Assert.Equal(ToolRisk.Read, new ScheduleListTool().Risk);
        Assert.Equal(ToolRisk.Write, new ScheduleCancelTool().Risk);
    }

    [Fact]
    public void A_session_or_a_scheduled_run_is_never_given_the_schedule_tools()
    {
        string[] ids = Hartsy.Extensions.LLMAssistant.Services.Sessions.SessionManager.NarrowToolIds(["web_search", .. ToolConstants.ScheduleIds, .. ToolConstants.SessionIds], null, null, atMaxDepth: false);
        Assert.DoesNotContain(ToolConstants.ScheduleCreate, ids);
        Assert.Contains(ToolConstants.SessionStart, ids);
        Assert.Contains("web_search", ids);
    }
}
