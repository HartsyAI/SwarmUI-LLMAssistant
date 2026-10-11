using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>A saved tool turn replays as the calls and results the model made, and each provider's request carries the tool
/// shapes its API expects (Anthropic <c>tool_use</c>/<c>tool_result</c> blocks, OpenAI <c>tool_calls</c>/<c>tool</c> messages).</summary>
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
    public void AnthropicMergesToolResultsAndTheNextUserTurn()
    {
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory(
        [
            new ChatMessageData { Role = "user", Content = "time and weather?" },
            Assistant("", SavedCall("c1", "get_time", new JObject(), new JObject { ["now"] = "14:05" }), SavedCall("c2", "get_weather", new JObject(), new JObject { ["sky"] = "clear" })),
            new ChatMessageData { Role = "user", Content = "thanks" },
        ]);
        JArray messages = AnthropicLLMProvider.BuildAnthropicMessages(input.Messages);
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m["role"]!.ToString()));
        JArray results = (JArray)messages[2]["content"]!;
        Assert.Equal(["tool_result", "tool_result", "text"], results.Select(b => b["type"]!.ToString()));
        Assert.Equal("c1", results[0]["tool_use_id"]!.ToString());
        Assert.Equal("thanks", results[2]["text"]!.ToString());
    }

    [Fact]
    public void AnthropicToolUseCarriesItsInputAsAnObject()
    {
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory([Assistant("Checking.", SavedCall("c1", "get_time", new JObject { ["tz"] = "UTC" }, new JObject()))]);
        JArray messages = AnthropicLLMProvider.BuildAnthropicMessages(input.Messages);
        // Saved prose follows the turn's tool results (see ExtendedLLMInput.AddToolTurns), so the tool_use turn holds only the call.
        JArray blocks = (JArray)messages[0]["content"]!;
        Assert.Equal(["tool_use"], blocks.Select(b => b["type"]!.ToString()));
        Assert.Equal("UTC", ((JObject)blocks[0]["input"]!)["tz"]!.ToString());
        Assert.Equal(["assistant", "user", "assistant"], messages.Select(m => m["role"]!.ToString()));
        Assert.Equal("Checking.", messages[2]["content"]!.ToString());
    }

    [Fact]
    public void AnthropicDropsBlankAssistantTurns()
    {
        List<LLMMessage> source = [new LLMMessage { Role = "user", Content = "hi" }, new LLMMessage { Role = "assistant", Content = "   " }, new LLMMessage { Role = "user", Content = "again" }];
        JArray messages = AnthropicLLMProvider.BuildAnthropicMessages(source);
        Assert.Equal(["user"], messages.Select(m => m["role"]!.ToString()));
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
}
