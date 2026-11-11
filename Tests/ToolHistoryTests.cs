using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>A saved tool turn replays as the calls and results the model made, and each provider's request carries the tool
/// shapes its API expects (OpenAI <c>tool_calls</c>/<c>tool</c> messages, the local engine's native calls).</summary>
public class ToolHistoryTests
{
    private static JObject SavedCall(string id, string name, JObject args, JToken result) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["arguments"] = args,
        ["result"] = result,
    };

    private static ChatMessageData Assistant(string content, params JObject[] calls) => new()
    {
        Role = "assistant",
        Content = content,
        ToolCalls = calls.Length > 0 ? [.. calls] : null,
    };

    [Fact]
    public void SavedToolTurnReplaysTheCallItsResultAndTheProse()
    {
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory(
            [new ChatMessageData { Role = "user", Content = "time?" }, Assistant("It is 14:05.", SavedCall("c1", "get_time", new JObject(), new JObject { ["now"] = "14:05" }))]);
        Assert.Equal(["user", "assistant", "tool", "assistant"], input.Messages.Select(m => m.Role));
        Assert.Equal("c1", input.Messages[1].ToolCalls![0]["id"]!.ToString());
        Assert.Equal("c1", input.Messages[2].ToolCallId);
        Assert.Equal("get_time", input.Messages[2].Name);
        Assert.Equal("{\"now\":\"14:05\"}", input.Messages[2].Content);
        Assert.Equal("It is 14:05.", input.Messages[3].Content);
    }

    [Fact]
    public void MissingResultIsAStandInNotAnEmptyTurn()
    {
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory([Assistant("", SavedCall("c1", "get_time", new JObject(), null))]);
        Assert.Equal("{\"success\":false,\"error\":\"no result recorded\"}", input.Messages.Single(m => m.Role == "tool").Content);
        Assert.DoesNotContain(input.Messages, m => m.Role == "assistant" && m.ToolCalls is null);
    }

    [Fact]
    public void LongResultIsCutWithAMarker()
    {
        string big = new('x', ToolConstants.MaxReplayedToolResultChars + 500);
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory([Assistant("", SavedCall("c1", "web_search", new JObject(), big))]);
        string replayed = input.Messages.Single(m => m.Role == "tool").Content;
        Assert.EndsWith("...[truncated]", replayed, StringComparison.Ordinal);
        Assert.Equal(ToolConstants.MaxReplayedToolResultChars + "...[truncated]".Length, replayed.Length);
    }

    [Fact]
    public void PlainAssistantTurnIsUnchanged()
    {
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory([Assistant("Hello.")]);
        Assert.Single(input.Messages);
        Assert.Equal("assistant", input.Messages[0].Role);
        Assert.Null(input.Messages[0].ToolCalls);
    }

    [Fact]
    public void OpenAICarriesToolCallsAsJsonTextAndToolResultsByCallId()
    {
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory([Assistant("", SavedCall("c1", "get_time", new JObject { ["tz"] = "UTC" }, new JObject { ["now"] = "14:05" }))]);
        JArray messages = RemoteOpenAILLMProvider.BuildOpenAIMessages(input.Messages);
        JObject assistant = (JObject)messages[0];
        Assert.Equal("assistant", assistant["role"]!.ToString());
        Assert.Equal("c1", assistant["tool_calls"]![0]!["id"]!.ToString());
        Assert.Equal("function", assistant["tool_calls"]![0]!["type"]!.ToString());
        Assert.Equal("{\"tz\":\"UTC\"}", assistant["tool_calls"]![0]!["function"]!["arguments"]!.ToString());
        JObject tool = (JObject)messages[1];
        Assert.Equal("tool", tool["role"]!.ToString());
        Assert.Equal("c1", tool["tool_call_id"]!.ToString());
    }

    [Fact]
    public void OpenAIPlainTurnsKeepTheirContent()
    {
        JArray messages = RemoteOpenAILLMProvider.BuildOpenAIMessages([new LLMMessage { Role = "user", Content = "hi" }]);
        Assert.Equal("hi", messages[0]["content"]!.ToString());
    }

    [Fact]
    public void SavedRoundsReplayInTheOrderTheyHappened()
    {
        // call A, its result, then call B made from A's result, then the answer: never A and B side by side before A's result.
        ChatMessageData saved = new()
        {
            Role = "assistant",
            Content = "Looking up A. Using it for B. Done.",
            ToolCalls = [SavedCall("a", "lookup", new JObject(), new JObject { ["v"] = 1 }), SavedCall("b", "fetch", new JObject(), new JObject { ["v"] = 2 })],
            Rounds =
            [
                new JObject { ["text"] = "Looking up A. ", ["toolCallIds"] = new JArray("a") },
                new JObject { ["text"] = "Using it for B. ", ["toolCallIds"] = new JArray("b") },
                new JObject { ["text"] = "Done.", ["toolCallIds"] = new JArray() },
            ],
        };
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory([saved]);
        Assert.Equal(["assistant", "tool", "assistant", "tool", "assistant"], input.Messages.Select(m => m.Role));
        Assert.Equal("Looking up A. ", input.Messages[0].Content);
        Assert.Equal("a", input.Messages[1].ToolCallId);
        Assert.Equal("Using it for B. ", input.Messages[2].Content);
        Assert.Equal("b", input.Messages[2].ToolCalls![0]["id"]!.ToString());
        Assert.Equal("Done.", input.Messages[4].Content);
    }

    [Fact]
    public void ASavedRoundsReasoningReplaysAsReasoningContent()
    {
        // The runner keeps a round's think block in its saved text, so a template that reads reasoning_content still gets it next turn.
        ChatMessageData saved = new()
        {
            Role = "assistant",
            Content = "<think>check the clock</think><think>it said noon</think>It is noon.",
            ToolCalls = [SavedCall("c1", "get_time", new JObject(), new JObject { ["now"] = "noon" })],
            Rounds =
            [
                new JObject { ["text"] = "<think>check the clock</think>", ["toolCallIds"] = new JArray("c1") },
                new JObject { ["text"] = "<think>it said noon</think>It is noon.", ["toolCallIds"] = new JArray() },
            ],
        };
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory([saved]);
        Assert.True(HartsyLocalLLMProvider.HasThinkReply(input.Messages));
        List<HartsyInference.Engine.Requests.TextMessage> engine = [.. input.Messages.Select(m => HartsyLocalLLMProvider.ToTextMessage(m, m.Content, null, splitReasoning: true))];
        Assert.Equal("check the clock", engine[0].ReasoningContent);
        Assert.Equal("", engine[0].Content);
        Assert.Equal("c1", engine[0].ToolCalls![0].Id);
        Assert.Equal("it said noon", engine[2].ReasoningContent);
        Assert.Equal("It is noon.", engine[2].Content);
    }

    [Fact]
    public void AMalformedSavedArgumentsStayAsWrittenInTheReplay()
    {
        // The local engine and OpenAI both get the text as written.
        JObject call = new()
        {
            ["id"] = "c1",
            ["name"] = "get_time",
            ["arguments"] = new JValue("{\"tz\":"),
            ["result"] = new JObject { ["success"] = false, ["error"] = "not valid JSON" },
        };
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory([Assistant("", call)]);
        LLMMessage callTurn = input.Messages.Single(m => m.Role == LLMRoles.Assistant && m.ToolCalls is { Count: > 0 });

        Assert.Equal("{\"tz\":", callTurn.ToolCalls![0]["arguments"]!.ToString());
        Assert.Equal("{\"tz\":", HartsyLocalLLMProvider.ToNativeToolCall(callTurn.ToolCalls[0]).Arguments);
        JArray openai = RemoteOpenAILLMProvider.BuildOpenAIMessages(input.Messages);
        JToken openAiCall = openai.Single(m => m["tool_calls"] is not null)["tool_calls"]![0]!;
        Assert.Equal("{\"tz\":", openAiCall["function"]!["arguments"]!.ToString());
    }
}
