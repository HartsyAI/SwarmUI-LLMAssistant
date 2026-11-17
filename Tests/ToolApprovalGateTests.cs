using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Tests.Fakes;
using Hartsy.Extensions.LLMAssistant.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The gate that asks the user before a tool call that changes something, using a fake sink that stands in for the chat socket.</summary>
public class ToolApprovalGateTests
{
    private const string Owner = "alice";
    private const string Thread = "thread1";

    private static (ToolApprovalGate Gate, RecordingChatSink Sink, PendingRequestRegistry<string> Pending, ToolApprovalGrants Grants) Make(string mode, bool canApprove = true, string answer = null)
    {
        PendingRequestRegistry<string> pending = new();
        ToolApprovalGrants grants = new();
        RecordingChatSink sink = new() { CanApprove = canApprove };
        if (answer is not null)
        {
            // The user answers as soon as the request is shown, the way the browser does through LLMAssistantResolveToolApproval.
            sink.OnApproval = request =>
            {
                Assert.True(pending.TryResolve(Owner, request["approvalId"]?.ToString(), answer));
                return Task.CompletedTask;
            };
        }
        return (new ToolApprovalGate(mode, Owner, Thread, sink, pending, grants), sink, pending, grants);
    }

    [Fact]
    public async Task AutoModeNeverAsks()
    {
        (ToolApprovalGate gate, RecordingChatSink sink, _, _) = Make("auto");

        Assert.Null(await gate.CheckAsync("shell_exec", ToolRisk.Exec, [], "c1", CancellationToken.None));

        Assert.DoesNotContain(sink.Events, e => e.StartsWith("tool_approval"));
    }

    [Fact]
    public async Task AskModeLetsReadsThroughWithoutAsking()
    {
        (ToolApprovalGate gate, RecordingChatSink sink, _, _) = Make("ask");

        Assert.Null(await gate.CheckAsync("file_read", ToolRisk.Read, [], "c1", CancellationToken.None));

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task AskModeAsksForAChange_AndRunsItWhenAllowed()
    {
        (ToolApprovalGate gate, RecordingChatSink sink, _, _) = Make("ask", answer: "allow");

        JObject refusal = await gate.CheckAsync("file_write", ToolRisk.Write, new JObject { ["path"] = "a.txt" }, "call-9", CancellationToken.None);

        Assert.Null(refusal);
        Assert.Equal(["tool_approval:file_write"], sink.Events);
    }

    [Fact]
    public async Task TheApprovalRequestCarriesWhatTheUserNeedsToDecide()
    {
        JObject seen = null;
        (ToolApprovalGate gate, RecordingChatSink sink, PendingRequestRegistry<string> pending, _) = Make("ask");
        sink.OnApproval = request =>
        {
            seen = request;
            pending.TryResolve(Owner, request["approvalId"].ToString(), "allow");
            return Task.CompletedTask;
        };

        await gate.CheckAsync("shell_exec", ToolRisk.Exec, new JObject { ["command"] = "dir" }, "call-1", CancellationToken.None);

        Assert.Equal("call-1", seen["id"]?.ToString());
        Assert.Equal("shell_exec", seen["name"]?.ToString());
        Assert.Equal("exec", seen["risk"]?.ToString());
        Assert.Equal("ask", seen["mode"]?.ToString());
        Assert.Equal("dir", seen["arguments"]?["command"]?.ToString());
        Assert.False(string.IsNullOrEmpty(seen["approvalId"]?.ToString()));
    }

    [Fact]
    public async Task ADeniedCallGivesTheModelAResultItCanActOn()
    {
        (ToolApprovalGate gate, _, _, _) = Make("ask", answer: "deny");

        JObject refusal = await gate.CheckAsync("file_write", ToolRisk.Write, [], "c1", CancellationToken.None);

        Assert.False(refusal["success"].Value<bool>());
        Assert.True(refusal["denied"].Value<bool>());
        Assert.Contains("declined", refusal["error"].ToString());
    }

    [Fact]
    public async Task AllowForThisChatSkipsTheQuestionNextTime_ButOnlyForThatTool()
    {
        (ToolApprovalGate gate, RecordingChatSink sink, _, ToolApprovalGrants grants) = Make("ask", answer: "allow_chat");

        Assert.Null(await gate.CheckAsync("file_write", ToolRisk.Write, [], "c1", CancellationToken.None));
        Assert.True(grants.Has(Owner, Thread, "file_write"));
        sink.Events.Clear();
        Assert.Null(await gate.CheckAsync("file_write", ToolRisk.Write, [], "c2", CancellationToken.None));
        Assert.Empty(sink.Events);

        sink.Events.Clear();
        Assert.Null(await gate.CheckAsync("memory_write", ToolRisk.Write, [], "c3", CancellationToken.None));
        Assert.Equal(["tool_approval:memory_write"], sink.Events);
    }

    [Fact]
    public async Task PlanModeRefusesAChangeWithoutAskingAndSaysWhy()
    {
        (ToolApprovalGate gate, RecordingChatSink sink, _, _) = Make("plan");

        JObject refusal = await gate.CheckAsync("file_write", ToolRisk.Write, [], "c1", CancellationToken.None);

        Assert.True(refusal["denied"].Value<bool>());
        Assert.Contains("Plan mode", refusal["error"].ToString());
        Assert.Empty(sink.Events);
        Assert.Null(await gate.CheckAsync("file_read", ToolRisk.Read, [], "c2", CancellationToken.None));
    }

    [Fact]
    public async Task ASinkThatCannotAskRefusesInsteadOfWaitingForever()
    {
        (ToolApprovalGate gate, RecordingChatSink sink, PendingRequestRegistry<string> pending, _) = Make("ask", canApprove: false);

        JObject refusal = await gate.CheckAsync("file_write", ToolRisk.Write, [], "c1", CancellationToken.None);

        Assert.True(refusal["denied"].Value<bool>());
        Assert.Contains("cannot ask", refusal["error"].ToString());
        Assert.Empty(sink.Events);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task StoppingWhileWaitingEndsTheWait_AndLeavesNothingPending()
    {
        (ToolApprovalGate gate, _, PendingRequestRegistry<string> pending, _) = Make("ask");
        using CancellationTokenSource stop = new();

        Task<JObject> check = gate.CheckAsync("file_write", ToolRisk.Write, [], "c1", stop.Token);
        Assert.False(check.IsCompleted);
        Assert.Equal(1, pending.Count);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public void AGrantInOneChatDoesNotReachAnotherChatOrUser()
    {
        ToolApprovalGrants grants = new();
        grants.Add("alice", "chatA", "file_write");

        Assert.True(grants.Has("alice", "chatA", "file_write"));
        Assert.False(grants.Has("alice", "chatB", "file_write"));
        Assert.False(grants.Has("bob", "chatA", "file_write"));
        grants.Clear("alice", "chatA");
        Assert.False(grants.Has("alice", "chatA", "file_write"));
    }
}
