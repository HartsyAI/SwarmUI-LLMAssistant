using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Compaction;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>When a chat is compacted, what is summarized, and when a saved summary stops applying: all of it decided from the thread alone.</summary>
public class CompactionPlannerTests
{
    /// <summary>A linear chat of alternating user and assistant messages with ids <c>m0</c>, <c>m1</c>, and so on.</summary>
    private static JObject Thread(int count, Action<int, JObject> shape = null)
    {
        JArray messages = [];
        for (int i = 0; i < count; i++)
        {
            JObject message = new()
            {
                ["id"] = $"m{i}",
                ["parentId"] = i == 0 ? JValue.CreateNull() : $"m{i - 1}",
                ["role"] = i % 2 == 0 ? "user" : "assistant",
                ["content"] = $"text {i}",
            };
            shape?.Invoke(i, message);
            messages.Add(message);
        }
        return new JObject { ["messages"] = messages, ["activeLeafId"] = $"m{count - 1}" };
    }

    private static List<JObject> Path(JObject thread) => ThreadStorageService.GetActivePath(thread);

    private static CompactionRecord RecordFor(List<JObject> path, int anchorIndex, string summary = "the summary")
        => new("c1", path[anchorIndex]["id"]!.ToString(), CompactionPlanner.Fingerprint(path.Take(anchorIndex + 1)), summary, anchorIndex + 1, 0, "m", "");

    private static JObject WithRecord(JObject thread, CompactionRecord record)
    {
        thread["compactions"] = new JArray(record.ToJson());
        return thread;
    }

    [Fact]
    public void ATooShortChatHasNothingToCompact()
    {
        Assert.Null(CompactionPlanner.Plan(Path(Thread(4)), null));
        Assert.Null(CompactionPlanner.Plan(Path(Thread(1)), null));
    }

    [Fact]
    public void ThePlanKeepsTheNewestMessagesAndCutsBeforeAUserTurn()
    {
        // 12 messages: the newest 4 (m8..m11) stay; m8 is a user message, so the cut falls after m7.
        List<JObject> path = Path(Thread(12));
        CompactionPlan plan = CompactionPlanner.Plan(path, null)!;
        Assert.Equal(7, plan.AnchorIndex);
        Assert.Equal(8, plan.ToSummarize.Count);
        Assert.Equal("m0", plan.ToSummarize[0]["id"]!.ToString());
        Assert.Null(plan.Previous);
    }

    [Fact]
    public void TheCutMovesBackToLandBeforeAUserMessage()
    {
        // 11 messages: keeping 4 would start the kept part at m7 (assistant), so the cut moves to start it at m6 (user).
        CompactionPlan plan = CompactionPlanner.Plan(Path(Thread(11)), null)!;
        Assert.Equal(5, plan.AnchorIndex);
    }

    [Fact]
    public void ASecondCompactionBuildsOnTheFirstAndSummarizesOnlyTheNewerMessages()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        CompactionRecord first = RecordFor(path, 7);
        JObject longer = Thread(20);
        longer["compactions"] = new JArray(first.ToJson());
        List<JObject> longPath = Path(longer);
        CompactionRecord active = CompactionPlanner.FindActive(longer, longPath)!;
        CompactionPlan plan = CompactionPlanner.Plan(longPath, active)!;
        Assert.Same(active, plan.Previous);
        Assert.True(plan.AnchorIndex > 7);
        Assert.Equal("m8", plan.ToSummarize[0]["id"]!.ToString());
        Assert.Equal(plan.AnchorIndex - 7, plan.ToSummarize.Count);
    }

    [Fact]
    public void AChatAlreadyCompactedAsFarAsItCanBeHasNothingMore()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        CompactionRecord active = RecordFor(path, 7);
        Assert.Null(CompactionPlanner.Plan(path, active));
    }

    [Fact]
    public void ASummaryAppliesWhileItsMessagesAreUnchanged()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        WithRecord(thread, RecordFor(path, 7));
        Assert.NotNull(CompactionPlanner.FindActive(thread, path));
    }

    [Fact]
    public void ASummaryStopsApplyingWhenACoveredMessageIsEdited()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        WithRecord(thread, RecordFor(path, 7));
        path[3]["content"] = "edited after the summary was written";
        Assert.Null(CompactionPlanner.FindActive(thread, Path(thread)));
    }

    [Fact]
    public void ASummaryStopsApplyingOnABranchMadeBeforeItsLastMessage()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        WithRecord(thread, RecordFor(path, 7));
        // A new reply branches off m4; the active path is now m0..m4 and the new reply, which does not contain m7.
        ((JArray)thread["messages"]!).Add(new JObject { ["id"] = "x1", ["parentId"] = "m4", ["role"] = "assistant", ["content"] = "another answer" });
        thread["activeLeafId"] = "x1";
        Assert.Null(CompactionPlanner.FindActive(thread, Path(thread)));
    }

    [Fact]
    public void ASummaryStillAppliesOnABranchMadeAfterItsLastMessage()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        WithRecord(thread, RecordFor(path, 7));
        ((JArray)thread["messages"]!).Add(new JObject { ["id"] = "x1", ["parentId"] = "m9", ["role"] = "assistant", ["content"] = "another answer" });
        thread["activeLeafId"] = "x1";
        Assert.NotNull(CompactionPlanner.FindActive(thread, Path(thread)));
    }

    [Fact]
    public void AnOlderSummaryAppliesWhenTheNewerOneDoesNot()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        CompactionRecord older = RecordFor(path, 3, "older") with { Id = "old" };
        CompactionRecord newer = RecordFor(path, 7, "newer") with { Id = "new" };
        thread["compactions"] = new JArray(older.ToJson(), newer.ToJson());
        // Branch at m5: the newer summary's last message (m7) is off the path, the older one's (m3) is on it.
        ((JArray)thread["messages"]!).Add(new JObject { ["id"] = "x1", ["parentId"] = "m5", ["role"] = "user", ["content"] = "different" });
        thread["activeLeafId"] = "x1";
        Assert.Equal("old", CompactionPlanner.FindActive(thread, Path(thread))!.Id);
    }

    [Fact]
    public void ApplyReplacesTheCoveredMessagesWithOneSummaryMessage()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        List<JObject> applied = CompactionPlanner.Apply(path, RecordFor(path, 7));
        // m8 is the user's, so the summary opens it instead of standing as a second user message in a row.
        Assert.Equal(4, applied.Count);
        Assert.Equal("user", applied[0]["role"]!.ToString());
        Assert.Equal("m8", applied[0]["id"]!.ToString());
        Assert.StartsWith(CompactionPlanner.SummaryHeading, applied[0]["content"]!.ToString());
        Assert.Contains("the summary", applied[0]["content"]!.ToString());
        Assert.EndsWith("text 8", applied[0]["content"]!.ToString());
        Assert.Equal("assistant", applied[1]["role"]!.ToString());
        // The saved message itself is not changed.
        Assert.Equal("text 8", path[8]["content"]!.ToString());
        Assert.Same(path, CompactionPlanner.Apply(path, null));
    }

    [Fact]
    public void WhenTheNextMessageIsTheAssistantsTheSummaryIsAUserMessageOfItsOwn()
    {
        List<JObject> path = Path(Thread(12));
        // A cut after m6 leaves m7 (the assistant's) next.
        List<JObject> applied = CompactionPlanner.Apply(path, RecordFor(path, 6));
        Assert.Equal(6, applied.Count);
        Assert.Equal("user", applied[0]["role"]!.ToString());
        Assert.Equal("assistant", applied[1]["role"]!.ToString());
        Assert.Equal("m7", applied[1]["id"]!.ToString());
    }

    [Fact]
    public void NoTwoUserMessagesAreEverSentInARow()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        foreach (int anchor in new[] { 1, 2, 3, 6, 7 })
        {
            List<JObject> applied = CompactionPlanner.Apply(path, RecordFor(path, anchor));
            for (int i = 1; i < applied.Count; i++)
            {
                Assert.False(applied[i]["role"]!.ToString() == "user" && applied[i - 1]["role"]!.ToString() == "user", $"anchor {anchor}, position {i}");
            }
        }
    }

    [Fact]
    public void TheModelIsSentTheSummaryNotTheCoveredMessages()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        WithRecord(thread, RecordFor(path, 7));
        List<ChatMessageData> history = ChatEndpoints.BuildHistoryFromThread(thread, new JObject(), new JObject());
        Assert.Equal(4, history.Count);
        Assert.Contains("the summary", history[0].Content);
        Assert.DoesNotContain(history, m => m.Content.Contains("text 3"));
    }

    [Fact]
    public void TheMessageCountLimitNeverCutsTheSummary()
    {
        JObject thread = Thread(12);
        List<JObject> path = Path(thread);
        WithRecord(thread, RecordFor(path, 7));
        List<ChatMessageData> history = ChatEndpoints.BuildHistoryFromThread(thread, new JObject(), new JObject { ["maxContextMessages"] = 2 });
        Assert.Equal(3, history.Count);
        Assert.Contains("the summary", history[0].Content);
        Assert.Equal("text 11", history[^1].Content);
    }

    [Fact]
    public void ContextUsesTheNewestReportedFigureAndEstimatesWhatCameAfter()
    {
        JObject thread = Thread(6, (i, m) =>
        {
            if (i == 3)
            {
                m["meta"] = new JObject { ["usage"] = new JObject { ["contextTokens"] = 1000 } };
            }
        });
        (long tokens, bool reported) = CompactionPlanner.EstimateContext(Path(thread), null);
        Assert.True(reported);
        // "text 4" and "text 5" are 6 characters each: 2 tokens apiece.
        Assert.Equal(1004, tokens);
    }

    [Fact]
    public void AReportedFigureBeforeTheSummaryIsStaleAndIsNotUsed()
    {
        JObject thread = Thread(12, (i, m) =>
        {
            if (i == 5)
            {
                m["meta"] = new JObject { ["usage"] = new JObject { ["contextTokens"] = 90000 } };
            }
        });
        List<JObject> path = Path(thread);
        (long tokens, bool reported) = CompactionPlanner.EstimateContext(path, RecordFor(path, 7), systemPromptTokens: 10);
        Assert.False(reported);
        Assert.True(tokens < 100);
    }

    [Fact]
    public void ARepliesReportFromBeforeTheSummaryWasMadeIsStaleEvenWhenTheReplyIsAfterItsLastMessage()
    {
        // The last reply (m11) reported 90000 tokens, then the chat was compacted through m7. m11 is after m7, but its figure predates the summary.
        JObject thread = Thread(12, (i, m) =>
        {
            if (i == 11)
            {
                m["meta"] = new JObject { ["usage"] = new JObject { ["contextTokens"] = 90000 } };
            }
        });
        List<JObject> path = Path(thread);
        CompactionRecord active = RecordFor(path, 7) with { TipMessageId = "m11" };
        (long tokens, bool reported) = CompactionPlanner.EstimateContext(path, active);
        Assert.False(reported);
        Assert.True(tokens < 200);
        // A reply after the summary reports again.
        thread = Thread(14, (i, m) =>
        {
            if (i == 13)
            {
                m["meta"] = new JObject { ["usage"] = new JObject { ["contextTokens"] = 700 } };
            }
        });
        path = Path(thread);
        (tokens, reported) = CompactionPlanner.EstimateContext(path, RecordFor(path, 7) with { TipMessageId = "m11" });
        Assert.True(reported);
        Assert.Equal(700, tokens);
    }

    [Fact]
    public void WithNoReportTheWholeConversationIsEstimated()
    {
        (long tokens, bool reported) = CompactionPlanner.EstimateContext(Path(Thread(4)), null, systemPromptTokens: 100);
        Assert.False(reported);
        Assert.Equal(100 + 4 * 2, tokens);
    }

    [Theory]
    [InlineData(8000, 10000, 0.8, true)]
    [InlineData(7999, 10000, 0.8, false)]
    [InlineData(999999, 0, 0.8, false)]
    [InlineData(9000, 10000, 0, false)]
    [InlineData(9000, 10000, 1.5, false)]
    public void IsDueNeedsAKnownWindowAndASaneThreshold(long tokens, int window, double threshold, bool due)
        => Assert.Equal(due, CompactionPlanner.IsDue(tokens, window, threshold));

    [Fact]
    public void SettingsDefaultToAutoAtEightyPercent()
    {
        Assert.True(CompactionPlanner.AutoEnabled(new JObject()));
        Assert.Equal(0.8, CompactionPlanner.Threshold(new JObject()));
        JObject settings = new() { ["parameters"] = new JObject { ["autoCompact"] = false, ["compactThreshold"] = 0.6 } };
        Assert.False(CompactionPlanner.AutoEnabled(settings));
        Assert.Equal(0.6, CompactionPlanner.Threshold(settings));
        Assert.Equal(0.8, CompactionPlanner.Threshold(new JObject { ["parameters"] = new JObject { ["compactThreshold"] = 7 } }));
    }

    [Fact]
    public void WindowComesFromTheSettingThenTheModelAndNeverAGuess()
    {
        LLMModelInfo model = new() { Id = "m", ContextLength = 32768 };
        Assert.Equal(32768, ContextWindowService.Resolve(model, new JObject()));
        Assert.Equal(8192, ContextWindowService.Resolve(model, new JObject { ["parameters"] = new JObject { ["contextWindow"] = 8192 } }));
        Assert.Equal(0, ContextWindowService.Resolve(new LLMModelInfo { Id = "x" }, new JObject()));
        Assert.Equal(0, ContextWindowService.Resolve(null, null));
    }

    [Fact]
    public void TheTranscriptNamesRolesClipsLongMessagesAndSummarizesToolCalls()
    {
        JObject thread = Thread(2, (i, m) =>
        {
            if (i == 0)
            {
                m["content"] = new string('x', 50);
            }
            if (i == 1)
            {
                m["toolCalls"] = new JArray(new JObject { ["name"] = "file_read", ["arguments"] = new JObject { ["path"] = "a.txt" }, ["result"] = new JObject { ["success"] = false } });
            }
        });
        string transcript = CompactionPlanner.BuildTranscript(Path(thread), perMessageChars: 10);
        Assert.Contains("User: xxxxxxxxxx …[cut]", transcript);
        Assert.Contains("Assistant: text 1", transcript);
        Assert.Contains("[tool file_read", transcript);
        Assert.Contains("failed]", transcript);
    }

    [Fact]
    public async Task TheSummarizerSeesThePreviousSummaryAndOnlyTheNewerMessagesAndItsRecordCoversThemAll()
    {
        JObject thread = Thread(20);
        List<JObject> path = Path(thread);
        thread["compactions"] = new JArray(RecordFor(path, 7, "first summary").ToJson());
        ExtendedLLMInput seen = null;
        CompactionRecord record = (await CompactionService.SummarizeAsync(thread, null, "model-a", "mention the budget", CancellationToken.None, (input, _) =>
        {
            seen = input;
            return Task.FromResult(new LLMGenerateResult("second summary", "", LLMGenerateResult.Stop));
        }))!;
        Assert.Contains("first summary", seen!.UserMessage);
        Assert.DoesNotContain("text 3", seen.UserMessage);
        Assert.Contains("text 9", seen.UserMessage);
        Assert.Contains("mention the budget", seen.UserMessage);
        Assert.Null(seen.ConversationId);
        Assert.Equal("second summary", record.Summary);
        Assert.Equal(CompactionPlanner.Fingerprint(path.Take(record.MessageCount)), record.CoverHash);
        Assert.Equal("m19", record.TipMessageId);
        Assert.True(record.MessageCount > 8);
    }

    [Fact]
    public async Task ASummarizerThatWritesNothingFailsWithoutChangingTheChat()
    {
        JObject thread = Thread(12);
        await Assert.ThrowsAsync<SwarmUI.Utils.SwarmReadableErrorException>(() => CompactionService.SummarizeAsync(thread, null, "m", null, CancellationToken.None,
            (_, _) => Task.FromResult(new LLMGenerateResult("  ", "", LLMGenerateResult.Length))));
        Assert.Null(thread["compactions"]);
    }
}
