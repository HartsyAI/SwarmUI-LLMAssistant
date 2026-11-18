using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The rules for sessions that need no user or storage: what a session may use, how its limits are read, and how its chat looks.</summary>
public class SessionTests
{
    /// <summary>A chat the way storage keeps it, with these messages on its one branch.</summary>
    private static JObject Thread(params (string Role, string Content)[] messages)
    {
        JArray list = [];
        string parent = null;
        foreach ((string role, string content) in messages)
        {
            string id = $"m{list.Count}";
            list.Add(new JObject { ["id"] = id, ["parentId"] = parent, ["role"] = role, ["content"] = content });
            parent = id;
        }
        return new JObject { ["id"] = "t1", ["messages"] = list, ["activeLeafId"] = parent };
    }

    // ---- Limits ----

    [Fact]
    public void Limits_default_to_one_level_of_sessions()
    {
        SessionLimits limits = SessionLimits.From(null);
        Assert.Equal(1, limits.MaxDepth);
        Assert.Equal(8, limits.MaxChildrenPerParent);
        Assert.Equal(3, limits.MaxRunningPerUser);
        Assert.Equal(900, limits.ApprovalTimeoutSeconds);
    }

    [Fact]
    public void Limits_come_from_settings_and_stay_in_range()
    {
        SessionLimits limits = SessionLimits.From(new JObject { ["sessions"] = new JObject { ["maxDepth"] = 2, ["maxChildrenPerParent"] = 999, ["maxRunningPerUser"] = 0, ["approvalTimeoutSeconds"] = "soon" } });
        Assert.Equal(2, limits.MaxDepth);
        Assert.Equal(32, limits.MaxChildrenPerParent);
        Assert.Equal(1, limits.MaxRunningPerUser);
        Assert.Equal(900, limits.ApprovalTimeoutSeconds);
    }

    // ---- Approval ceiling ----

    [Theory]
    [InlineData("auto", "auto", "auto")]
    [InlineData("auto", "ask", "ask")]
    [InlineData("auto", "plan", "plan")]
    [InlineData("ask", "auto", "ask")]
    [InlineData("ask", "plan", "plan")]
    [InlineData("plan", "auto", "plan")]
    [InlineData("plan", "ask", "plan")]
    [InlineData("ask", "nonsense", "ask")]
    [InlineData("plan", null, "plan")]
    public void A_session_is_never_looser_than_its_parent(string parent, string asked, string expected)
    {
        Assert.Equal(expected, ApprovalPolicy.Stricter(parent, asked));
        Assert.Equal(expected, ApprovalPolicy.Stricter(asked, parent));
    }

    [Fact]
    public void An_unknown_mode_counts_as_auto_so_it_cannot_loosen_anything()
    {
        Assert.Equal("ask", ApprovalPolicy.Stricter("ask", "whatever"));
        Assert.Equal("auto", ApprovalPolicy.Stricter("nonsense", null));
    }

    // ---- What a session may use ----

    [Fact]
    public void A_session_gets_the_tools_it_asks_for_out_of_its_parents()
    {
        string[] ids = SessionManager.NarrowToolIds(["web_search", "file_read", "file_write"], null, ["file_read", "shell_exec"], false);
        Assert.Equal(["file_read"], ids);
    }

    [Fact]
    public void A_session_cannot_gain_a_tool_from_another_assistant()
    {
        string[] ids = SessionManager.NarrowToolIds(["web_search"], ["web_search", "shell_exec", ToolConstants.CodeBash], null, false);
        Assert.Equal(["web_search"], ids);
    }

    [Fact]
    public void With_nothing_asked_a_session_gets_all_of_its_parents_tools_in_a_fixed_order()
    {
        Assert.Equal(["a_tool", "b_tool", "c_tool"], SessionManager.NarrowToolIds(["c_tool", "a_tool", "b_tool"], null, null, false));
    }

    [Fact]
    public void A_session_at_the_deepest_level_cannot_start_sessions()
    {
        string[] ids = SessionManager.NarrowToolIds(["web_search", .. ToolConstants.SessionIds], null, null, atMaxDepth: true);
        Assert.Equal(["web_search"], ids);
        string[] shallow = SessionManager.NarrowToolIds(["web_search", .. ToolConstants.SessionIds], null, null, atMaxDepth: false);
        Assert.Contains(ToolConstants.SessionStart, shallow);
    }

    [Fact]
    public void A_sessions_request_offers_only_its_own_list()
    {
        List<JObject> enabled = [new() { ["id"] = "web_search" }, new() { ["id"] = "file_write" }, new() { ["id"] = ToolConstants.CodeBash }];
        JObject session = new() { [SessionFields.ToolIds] = new JArray("web_search") };
        Assert.Equal(["web_search"], SessionManager.NarrowTools(enabled, session).Select(t => t["id"].ToString()));
        // A chat with no list is not limited.
        Assert.Equal(3, SessionManager.NarrowTools(enabled, new JObject()).Count);
        Assert.Equal(3, SessionManager.NarrowTools(enabled, null).Count);
    }

    [Fact]
    public void A_list_with_nothing_in_it_offers_nothing()
    {
        List<JObject> enabled = [new() { ["id"] = "web_search" }];
        Assert.Empty(SessionManager.NarrowTools(enabled, new JObject { [SessionFields.ToolIds] = new JArray() }));
    }

    [Fact]
    public void Running_a_tool_is_allowed_when_there_is_no_user_or_chat_to_check()
    {
        // Voice, the dev "Run Test" and the user's own picker run have no session behind them.
        Assert.True(SessionManager.AllowsTool(null, "t1", "web_search"));
        Assert.True(SessionManager.AllowsTool(null, null, "web_search"));
    }

    // ---- The chat itself ----

    [Fact]
    public void A_chat_is_a_session_only_when_it_has_a_parent()
    {
        Assert.False(SessionManager.IsSession(null));
        Assert.False(SessionManager.IsSession(new JObject()));
        Assert.False(SessionManager.IsSession(new JObject { [SessionFields.Parent] = "" }));
        Assert.True(SessionManager.IsSession(new JObject { [SessionFields.Parent] = "p1" }));
    }

    [Fact]
    public void The_status_and_depth_of_a_chat_that_is_not_a_session_are_empty_and_zero()
    {
        Assert.Equal("", SessionManager.StatusOf(new JObject()));
        Assert.Equal(0, SessionManager.DepthOf(new JObject()));
        Assert.Equal(2, SessionManager.DepthOf(new JObject { [SessionFields.Depth] = 2 }));
    }

    [Fact]
    public void Only_a_session_is_told_it_is_one()
    {
        Assert.Equal("Be helpful.", SessionManager.AppendSessionNote("Be helpful.", new JObject()));
        string text = SessionManager.AppendSessionNote("Be helpful.", new JObject { [SessionFields.Parent] = "p1" });
        Assert.StartsWith("Be helpful.\n\n# Session\n", text);
        Assert.Contains("report", text);
        // The same chat gives the same text every time, so a local model's prompt cache stays valid.
        Assert.Equal(text, SessionManager.AppendSessionNote("Be helpful.", new JObject { [SessionFields.Parent] = "p2" }));
    }

    [Theory]
    [InlineData("  Fix   the\nbug  ", "Fix the bug")]
    [InlineData("", "Session")]
    [InlineData(null, "Session")]
    [InlineData("   ", "Session")]
    public void A_title_is_one_line_and_never_empty(string title, string expected) => Assert.Equal(expected, SessionManager.CleanTitle(title));

    [Fact]
    public void A_long_title_is_cut()
    {
        string title = SessionManager.CleanTitle(new string('x', 500));
        Assert.Equal(SessionLimits.MaxTitleChars, title.Length);
    }

    [Fact]
    public void The_last_reply_is_the_report_without_the_models_reasoning()
    {
        JObject thread = Thread((Roles.User, "do it"), (Roles.Assistant, "<think>hmm</think>Done: 3 files."), (Roles.User, "ok"));
        Assert.Equal("Done: 3 files.", SessionManager.LastReply(thread));
        Assert.Equal("", SessionManager.LastReply(Thread((Roles.User, "hi"))));
    }

    [Fact]
    public void A_session_is_described_with_what_a_parent_needs()
    {
        JObject child = new()
        {
            ["id"] = "c1",
            ["title"] = "Research",
            ["messageCount"] = 3,
            [SessionFields.Parent] = "p1",
            [SessionFields.Status] = SessionStatuses.AwaitingApproval,
            [SessionFields.Report] = "Found it.",
            [SessionFields.PendingApproval] = new JObject { ["name"] = "file_write", ["arguments"] = new JObject { ["path"] = "a.txt" }, ["approvalId"] = "secret-id" },
        };
        JObject info = SessionManager.Describe(child);
        Assert.Equal("c1", info["id"]);
        Assert.Equal("awaiting_approval", info["status"]);
        Assert.Equal("Found it.", info["report"]);
        Assert.Equal("file_write", info["waitingForApproval"]["tool"]);
        // The id that answers the approval is the user's to use, not the model's.
        Assert.DoesNotContain("secret-id", info.ToString());
    }

    [Fact]
    public void The_last_messages_are_bounded_and_cut()
    {
        JObject thread = Thread((Roles.User, "one"), (Roles.Assistant, new string('y', 5000)), (Roles.User, "three"));
        JArray last = SessionManager.LastMessages(thread, 2);
        Assert.Equal(2, last.Count);
        Assert.EndsWith("[cut]", last[0]["content"].ToString());
        Assert.True(last[0]["content"].ToString().Length < 2100);
        Assert.Equal("three", last[1]["content"]);
        Assert.Equal(3, SessionManager.LastMessages(thread, 999).Count);
        Assert.Single(SessionManager.LastMessages(thread, 0));
    }

    // ---- Statuses ----

    [Theory]
    [InlineData("queued", true)]
    [InlineData("running", true)]
    [InlineData("awaiting_approval", true)]
    [InlineData("idle", false)]
    [InlineData("done", false)]
    [InlineData("failed", false)]
    [InlineData("interrupted", false)]
    [InlineData("archived", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_session_with_a_turn_in_progress_is_active(string status, bool active) => Assert.Equal(active, SessionStatuses.IsActive(status));

    [Fact]
    public void Every_status_a_session_can_be_in_is_valid_and_nothing_else_is()
    {
        foreach (string status in new[] { "queued", "running", "awaiting_approval", "idle", "done", "failed", "interrupted", "archived" })
        {
            Assert.True(SessionStatuses.IsValid(status));
        }
        Assert.False(SessionStatuses.IsValid("paused"));
        Assert.False(SessionStatuses.IsValid(null));
    }

    // ---- The tools ----

    [Fact]
    public void The_session_tools_are_off_by_default_and_not_part_of_any_assistants_starting_list()
    {
        JObject defaults = ToolRegistryService.BuildDefaultTools();
        Assert.Equal(7, ToolConstants.SessionIds.Length);
        foreach (string id in ToolConstants.SessionIds)
        {
            Assert.False(defaults[id]["enabled"].Value<bool>());
            Assert.Equal(id, defaults[id]["handlerId"]);
            Assert.DoesNotContain(id, ToolConstants.BuiltInIds);
        }
    }

    [Fact]
    public void Starting_a_session_needs_a_title_and_a_prompt_and_each_other_tool_needs_an_id()
    {
        JObject defaults = ToolRegistryService.BuildDefaultTools();
        Assert.Equal(["title", "prompt"], defaults[ToolConstants.SessionStart]["parameters"]["required"].Select(t => t.ToString()));
        foreach (string id in new[] { ToolConstants.SessionRead, ToolConstants.SessionWait, ToolConstants.SessionStop, ToolConstants.SessionArchive })
        {
            Assert.Equal(["id"], defaults[id]["parameters"]["required"].Select(t => t.ToString()));
        }
        Assert.Empty(defaults[ToolConstants.SessionList]["parameters"]["required"]);
        // The tool list a model reads: start takes tools as an array of ids.
        Assert.Equal("array", defaults[ToolConstants.SessionStart]["parameters"]["properties"]["tools"]["type"]);
    }

    [Fact]
    public void A_waiting_tool_outlasts_the_usual_time_limit_and_the_others_do_not()
    {
        Assert.True(new Tools.BuiltIn.SessionWaitTool().TimeoutFor(null) > TimeSpan.FromSeconds(SessionLimits.MaxWaitSeconds));
        Assert.Equal(TimeSpan.FromSeconds(60), new Tools.BuiltIn.SessionReadTool().TimeoutFor(null));
    }

    [Fact]
    public void Starting_and_messaging_count_as_running_something_and_reading_does_not()
    {
        Assert.Equal(Tools.ToolRisk.Exec, new Tools.BuiltIn.SessionStartTool().Risk);
        Assert.Equal(Tools.ToolRisk.Exec, new Tools.BuiltIn.SessionSendTool().Risk);
        Assert.Equal(Tools.ToolRisk.Read, new Tools.BuiltIn.SessionListTool().Risk);
        Assert.Equal(Tools.ToolRisk.Read, new Tools.BuiltIn.SessionReadTool().Risk);
        Assert.Equal(Tools.ToolRisk.Read, new Tools.BuiltIn.SessionWaitTool().Risk);
        Assert.Equal(Tools.ToolRisk.Write, new Tools.BuiltIn.SessionStopTool().Risk);
        Assert.Equal(Tools.ToolRisk.Write, new Tools.BuiltIn.SessionArchiveTool().Risk);
    }
}
