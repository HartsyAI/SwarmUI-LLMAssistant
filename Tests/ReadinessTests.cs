using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Readiness;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>What the setup check says about each part, with made-up inputs (no broken backend is needed to see a failure), and the small pure parts of the headless turn.</summary>
public class ReadinessTests
{
    /// <summary>A healthy setup: one backend with models, a tool or two, nothing else.</summary>
    private static ReadinessInputs Healthy() => new() { Providers = 1, Models = 3, ToolsEnabled = 4 };

    /// <summary>One check of a report.</summary>
    private static ReadinessCheck Check(ReadinessInputs inputs, string id) => ReadinessChecks.Evaluate(inputs).Single(c => c.Id == id);

    [Fact]
    public void A_healthy_setup_is_ok_overall_and_has_every_check_in_a_fixed_order()
    {
        List<ReadinessCheck> checks = ReadinessChecks.Evaluate(Healthy());
        Assert.Equal(["backends", "model", "tools", "mcp", "hooks", "skills", "knowledge", "schedules", "sessions", "storage"], checks.Select(c => c.Id));
        Assert.Equal(CheckStatus.Ok, ReadinessChecks.Overall(checks));
        Assert.DoesNotContain(checks, c => c.Status is CheckStatus.Fail or CheckStatus.Warn);
    }

    [Fact]
    public void The_overall_result_is_the_worst_one()
    {
        ReadinessCheck ok = new("a", "A", CheckStatus.Ok, "");
        ReadinessCheck info = new("b", "B", CheckStatus.Info, "");
        ReadinessCheck warn = new("c", "C", CheckStatus.Warn, "");
        ReadinessCheck fail = new("d", "D", CheckStatus.Fail, "");
        Assert.Equal(CheckStatus.Ok, ReadinessChecks.Overall([ok, info]));
        Assert.Equal(CheckStatus.Warn, ReadinessChecks.Overall([ok, warn, info]));
        Assert.Equal(CheckStatus.Fail, ReadinessChecks.Overall([warn, fail, ok]));
        Assert.Equal(CheckStatus.Ok, ReadinessChecks.Overall([]));
    }

    [Fact]
    public void No_backend_and_a_backend_with_no_model_are_failures_with_what_to_do()
    {
        ReadinessCheck none = Check(new ReadinessInputs(), "backends");
        Assert.Equal(CheckStatus.Fail, none.Status);
        Assert.Contains("Server > Backends", none.Fix);
        ReadinessCheck empty = Check(new ReadinessInputs { Providers = 2 }, "backends");
        Assert.Equal(CheckStatus.Fail, empty.Status);
        Assert.Contains("2 backends", empty.Detail);
        Assert.Contains("no model", empty.Detail);
        Assert.Equal("1 backend, 1 model.", Check(new ReadinessInputs { Providers = 1, Models = 1 }, "backends").Detail);
    }

    [Fact]
    public void A_preferred_model_that_nothing_offers_is_a_warning_and_none_chosen_is_fine()
    {
        Assert.Equal(CheckStatus.Info, Check(Healthy(), "model").Status);
        ReadinessInputs missing = Healthy();
        missing.PreferredModel = "gone-model";
        Assert.Equal(CheckStatus.Warn, Check(missing, "model").Status);
        Assert.Contains("gone-model", Check(missing, "model").Detail);
        missing.PreferredModelFound = true;
        Assert.Equal(CheckStatus.Ok, Check(missing, "model").Status);
        // With no models at all the backends check already failed; this one does not pile on.
        ReadinessInputs noModels = new() { Providers = 1, PreferredModel = "x" };
        Assert.Equal(CheckStatus.Info, Check(noModels, "model").Status);
    }

    [Fact]
    public void A_tool_that_points_at_nothing_fails_and_a_tool_the_user_may_not_run_is_a_warning()
    {
        ReadinessInputs inputs = Healthy();
        inputs.ToolsWithoutHandler = ["my_tool", "other"];
        ReadinessCheck broken = Check(inputs, "tools");
        Assert.Equal(CheckStatus.Fail, broken.Status);
        Assert.Contains("my_tool, other", broken.Detail);
        inputs.ToolsWithoutHandler = [];
        inputs.ToolsDenied = 2;
        ReadinessCheck denied = Check(inputs, "tools");
        Assert.Equal(CheckStatus.Warn, denied.Status);
        Assert.Contains("2 of them", denied.Detail);
        Assert.Equal("1 tool switched on.", Check(new ReadinessInputs { ToolsEnabled = 1 }, "tools").Detail);
    }

    [Fact]
    public void MCP_servers_are_counted_by_state_and_a_failed_one_is_a_warning()
    {
        Assert.Equal(CheckStatus.Info, Check(Healthy(), "mcp").Status);
        ReadinessInputs ready = Healthy();
        (ready.McpTotal, ready.McpReady) = (2, 2);
        Assert.Equal(CheckStatus.Ok, Check(ready, "mcp").Status);
        Assert.Equal("2 of 2 ready.", Check(ready, "mcp").Detail);

        ReadinessInputs mixed = Healthy();
        (mixed.McpTotal, mixed.McpReady, mixed.McpFailed, mixed.McpDisabled, mixed.McpConnecting) = (5, 1, 1, 2, 1);
        ReadinessCheck check = Check(mixed, "mcp");
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("1 of 5 ready", check.Detail);
        Assert.Contains("2 switched off", check.Detail);
        Assert.Contains("1 connecting", check.Detail);
        Assert.Contains("1 failed", check.Detail);
    }

    [Fact]
    public void The_reason_a_server_failed_is_in_the_report_only_when_it_was_given()
    {
        ReadinessInputs forAdmin = Healthy();
        (forAdmin.McpTotal, forAdmin.McpFailed) = (1, 1);
        forAdmin.McpErrors = [("files", "could not start: C:\\tools\\server.exe --key abc")];
        Assert.Contains("could not start", Check(forAdmin, "mcp").Detail);
        // For anyone else the inputs carry no reason, so the report cannot.
        ReadinessInputs forUser = Healthy();
        (forUser.McpTotal, forUser.McpFailed) = (1, 1);
        string detail = Check(forUser, "mcp").Detail;
        Assert.DoesNotContain("could not start", detail);
        Assert.DoesNotContain("server.exe", detail);
        Assert.Contains("1 failed", detail);
    }

    [Fact]
    public void Hooks_that_cannot_be_read_are_a_warning()
    {
        Assert.Equal(CheckStatus.Info, Check(Healthy(), "hooks").Status);
        ReadinessInputs inputs = Healthy();
        (inputs.HooksTotal, inputs.HooksInvalid) = (3, 0);
        Assert.Equal(CheckStatus.Ok, Check(inputs, "hooks").Status);
        inputs.HooksInvalid = 2;
        ReadinessCheck bad = Check(inputs, "hooks");
        Assert.Equal(CheckStatus.Warn, bad.Status);
        Assert.Contains("2 of 3", bad.Detail);
    }

    [Fact]
    public void The_knowledge_library_says_how_it_is_searched_and_whether_the_embedding_model_answered()
    {
        Assert.Equal(CheckStatus.Info, Check(Healthy(), "knowledge").Status);
        ReadinessInputs words = Healthy();
        words.KnowledgeDocuments = 2;
        Assert.Equal("2 documents, searched by words.", Check(words, "knowledge").Detail);

        ReadinessInputs untried = Healthy();
        (untried.KnowledgeDocuments, untried.EmbeddingSet) = (1, true);
        ReadinessCheck notTried = Check(untried, "knowledge");
        Assert.Equal(CheckStatus.Ok, notTried.Status);
        Assert.Contains("not tried", notTried.Detail);

        ReadinessInputs failing = Healthy();
        (failing.KnowledgeDocuments, failing.EmbeddingSet, failing.EmbeddingProbed, failing.EmbeddingError) = (1, true, true, "404 model not found");
        ReadinessCheck warn = Check(failing, "knowledge");
        Assert.Equal(CheckStatus.Warn, warn.Status);
        Assert.Contains("404 model not found", warn.Detail);

        ReadinessInputs working = Healthy();
        (working.KnowledgeDocuments, working.EmbeddingSet, working.EmbeddingProbed) = (1, true, true);
        ReadinessCheck ok = Check(working, "knowledge");
        Assert.Equal(CheckStatus.Ok, ok.Status);
        Assert.Contains("meaning and words", ok.Detail);
        Assert.DoesNotContain("not tried", ok.Detail);
    }

    [Fact]
    public void Schedules_fail_when_the_scheduler_is_not_running_and_warn_when_they_skipped_runs()
    {
        Assert.Equal(CheckStatus.Info, Check(Healthy(), "schedules").Status);
        ReadinessInputs inputs = Healthy();
        inputs.SchedulesEnabled = 2;
        ReadinessCheck stopped = Check(inputs, "schedules");
        Assert.Equal(CheckStatus.Fail, stopped.Status);
        Assert.Contains("not running", stopped.Detail);
        inputs.SchedulerRunning = true;
        Assert.Equal(CheckStatus.Ok, Check(inputs, "schedules").Status);
        inputs.SchedulesSkipping = 1;
        ReadinessCheck skipping = Check(inputs, "schedules");
        Assert.Equal(CheckStatus.Warn, skipping.Status);
        Assert.Contains("1 skipped", skipping.Detail);
    }

    [Fact]
    public void Sessions_are_counted_and_storage_that_cannot_be_written_fails()
    {
        Assert.Equal(CheckStatus.Info, Check(Healthy(), "sessions").Status);
        ReadinessInputs busy = Healthy();
        (busy.SessionsWorking, busy.SessionsQueued) = (3, 2);
        Assert.Equal("3 working, 2 waiting for a place.", Check(busy, "sessions").Detail);
        Assert.Equal(CheckStatus.Ok, Check(Healthy(), "storage").Status);
        ReadinessInputs locked = Healthy();
        (locked.StorageWritable, locked.StorageError) = (false, "Access to the path is denied.");
        ReadinessCheck check = Check(locked, "storage");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("denied", check.Detail);
        Assert.Contains("permissions", check.Fix);
    }

    [Fact]
    public void A_part_that_could_not_be_looked_at_replaces_its_line_instead_of_failing_the_report()
    {
        ReadinessInputs inputs = Healthy();
        inputs.Unchecked.Add(new ReadinessCheck("hooks", "Hooks", CheckStatus.Warn, "Could not be checked.", "See the server log."));
        inputs.Unchecked.Add(new ReadinessCheck("extra", "Extra", CheckStatus.Warn, "Could not be checked."));
        List<ReadinessCheck> checks = ReadinessChecks.Evaluate(inputs);
        Assert.Equal("Could not be checked.", checks.Single(c => c.Id == "hooks").Detail);
        Assert.Equal(11, checks.Count);
        Assert.Equal(CheckStatus.Warn, ReadinessChecks.Overall(checks));
    }

    [Fact]
    public void A_status_is_written_in_lower_case_in_the_report()
    {
        Assert.Equal("ok", ReadinessService.Name(CheckStatus.Ok));
        Assert.Equal("warn", ReadinessService.Name(CheckStatus.Warn));
        Assert.Equal("fail", ReadinessService.Name(CheckStatus.Fail));
        Assert.Equal("info", ReadinessService.Name(CheckStatus.Info));
    }

    // ---- The headless turn's pure parts ----

    [Fact]
    public void A_chat_made_by_a_request_is_titled_from_the_start_of_its_message()
    {
        Assert.Equal("Summarize the report", HeadlessTurnService.TitleOf("  Summarize\n the   report  "));
        Assert.Equal("Request", HeadlessTurnService.TitleOf(""));
        Assert.Equal("Request", HeadlessTurnService.TitleOf(null));
        string title = HeadlessTurnService.TitleOf(new string('w', 500));
        Assert.Equal(61, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void The_reply_text_has_no_reasoning_in_it()
    {
        Assert.Equal("The answer.", HeadlessTurnService.CleanResponse("<think>let me see\nhmm</think>The answer."));
        Assert.Equal("a  b", HeadlessTurnService.CleanResponse("a <think>x</think><think>y</think> b"));
        Assert.Equal("", HeadlessTurnService.CleanResponse(null));
        Assert.Equal("plain", HeadlessTurnService.CleanResponse("  plain  "));
    }

    [Fact]
    public void A_long_tool_result_is_cut_and_a_short_one_is_returned_whole()
    {
        JObject small = new() { ["success"] = true, ["text"] = "ok" };
        Assert.Same(small, HeadlessTurnService.CapResult(small));
        JObject huge = new() { ["content"] = new string('x', HeadlessTurnService.MaxResultChars * 2) };
        JToken cut = HeadlessTurnService.CapResult(huge);
        Assert.True(cut["truncated"].Value<bool>());
        Assert.True(cut["chars"].Value<int>() > HeadlessTurnService.MaxResultChars);
        Assert.Equal(HeadlessTurnService.MaxResultChars, cut["preview"].ToString().Length);
        Assert.Null(HeadlessTurnService.CapResult(null));
    }

    [Fact]
    public void A_dry_run_adds_the_message_to_a_copy_of_the_chat_and_leaves_the_chat_alone()
    {
        JObject thread = new()
        {
            ["id"] = "t1",
            ["messages"] = new JArray(new JObject { ["id"] = "m1", ["role"] = "user", ["content"] = "hello", ["parentId"] = null }, new JObject { ["id"] = "m2", ["role"] = "assistant", ["content"] = "hi", ["parentId"] = "m1" }),
            ["activeLeafId"] = "m2",
        };
        string before = thread.ToString();
        JObject copy = HeadlessTurnService.WithPendingMessage(thread, "next question");
        Assert.Equal(before, thread.ToString());
        Assert.Equal(3, copy["messages"].Count());
        JObject added = (JObject)copy["messages"].Last;
        Assert.Equal("user", added["role"]);
        Assert.Equal("next question", added["content"]);
        Assert.Equal("m2", added["parentId"]);
        Assert.Equal(added["id"], copy["activeLeafId"]);
        Assert.StartsWith("pending-", added["id"].ToString());

        // An empty chat gets it as the first message.
        JObject empty = HeadlessTurnService.WithPendingMessage(new JObject { ["id"] = "", ["messages"] = new JArray() }, "first");
        Assert.Single(empty["messages"]);
        Assert.Equal(JTokenType.Null, empty["messages"][0]["parentId"].Type);
    }
}
