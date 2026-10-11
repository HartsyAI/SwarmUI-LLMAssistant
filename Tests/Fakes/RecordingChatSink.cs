using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Tests.Fakes;

/// <summary>A turn sink that records every event in order, as <c>kind:detail</c> strings, so a test can assert the frame sequence.</summary>
internal sealed class RecordingChatSink : IToolTurnSink
{
    public List<string> Events { get; } = [];

    public ToolTurnOutcome Outcome { get; private set; }

    public bool IsOpen { get; set; } = true;

    public Task Chunk(string text) { Events.Add("chunk:" + text); return Task.CompletedTask; }

    public Task Iteration(int round) { Events.Add("iteration:" + round); return Task.CompletedTask; }

    public Task Status(string phase) { Events.Add("status:" + phase); return Task.CompletedTask; }

    public Task ToolCall(JObject call) { Events.Add("tool_call:" + call["name"]); return Task.CompletedTask; }

    public Task ToolResult(JObject result) { Events.Add("tool_result:" + result["name"]); return Task.CompletedTask; }

    public Task Done(ToolTurnOutcome outcome) { Outcome = outcome; Events.Add("done"); return Task.CompletedTask; }
}
