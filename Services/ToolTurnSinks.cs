using System.Net.WebSockets;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>The chat socket's events: text, rounds, status, tool calls and results, and the done frame. In compare mode every frame is tagged
/// with its lane and writes go through the shared lock.</summary>
public sealed class ChatWsSink(WebSocket socket, int lane, SemaphoreSlim sendLock) : IToolTurnSink
{
    /// <inheritdoc/>
    public bool IsOpen => socket.State == WebSocketState.Open;

    private bool _inThink;

    /// <inheritdoc/>
    public Task Chunk(string text) => Send(new JObject { ["chunk"] = text });

    /// <inheritdoc/>
    public async Task Reasoning(string text)
    {
        if (!_inThink)
        {
            _inThink = true;
            await Send(new JObject { ["chunk"] = "<think>" });
        }
        await Send(new JObject { ["chunk"] = text });
    }

    /// <inheritdoc/>
    public async Task ReasoningEnd()
    {
        if (!_inThink) return;
        _inThink = false;
        await Send(new JObject { ["chunk"] = "</think>" });
    }

    /// <inheritdoc/>
    public Task Iteration(int round) => Send(new JObject { ["iteration"] = round });

    /// <inheritdoc/>
    public Task Status(string phase) => Send(new JObject { ["status"] = phase });

    /// <inheritdoc/>
    public Task ToolCall(JObject call) => Send(new JObject { ["tool_call"] = call });

    /// <inheritdoc/>
    public Task ToolResult(JObject result) => Send(new JObject { ["tool_result"] = result });

    /// <inheritdoc/>
    /// <remarks>Sent as an ordinary <c>tool_result</c>, so the chat closes the call's bubble with the stand-in result.</remarks>
    public Task ToolNotRun(JObject result) => Send(new JObject { ["tool_result"] = result });

    /// <inheritdoc/>
    public Task Done(ToolTurnOutcome outcome)
    {
        JObject frame = outcome.Truncated
            ? new JObject { ["done"] = true, ["truncated"] = true, ["reason"] = outcome.Reason, ["full_text"] = outcome.FullText }
            : new JObject { ["done"] = true, ["full_text"] = outcome.FullText, ["stopReason"] = outcome.StopReason };
        return Send(frame);
    }

    private Task Send(JObject data) => SendJson(socket, data, lane, sendLock);

    /// <summary>Serializes one event to the socket; a lane tags it and writes are serialized through <paramref name="sendLock"/>.</summary>
    public static async Task SendJson(WebSocket socket, JObject data, int lane = -1, SemaphoreSlim sendLock = null)
    {
        if (socket.State != WebSocketState.Open) return;
        if (lane >= 0) data["lane"] = lane;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data.ToString(Newtonsoft.Json.Formatting.None));
        if (sendLock is not null) await sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        finally
        {
            sendLock?.Release();
        }
    }
}

/// <summary>The voice socket's events, in the shape AudioLab's client reads: <c>chunk</c>, <c>native_tool_call</c>, <c>tool_result</c>,
/// a round-limit <c>notice</c>, and a <c>done</c> frame carrying the device actions the turn took.</summary>
/// <remarks>A call is sent only once it has run, right before its result: a relay treats a call without a result as one it must run
/// itself, so a call the turn never ran (the round limit, an error or a stop) is never sent. Nothing is sent once the socket is gone.</remarks>
public sealed class VoiceWsSink(Func<JObject, Task> send, Func<bool> isOpen, JArray deviceCalls) : IToolTurnSink
{
    /// <summary>Calls the model made that have no result yet, by id.</summary>
    private readonly Dictionary<string, JObject> _pendingCalls = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool IsOpen => isOpen();

    /// <inheritdoc/>
    public Task Chunk(string text) => Send(new JObject { ["chunk"] = text });

    /// <inheritdoc/>
    public Task Reasoning(string text) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ReasoningEnd() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Iteration(int round) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Status(string phase) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ToolCall(JObject call)
    {
        _pendingCalls[call["id"]?.ToString() ?? ""] = call;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ToolResult(JObject result)
    {
        string id = result["id"]?.ToString() ?? "";
        string name = result["name"]?.ToString();
        JObject arguments = new();
        if (_pendingCalls.Remove(id, out JObject call))
        {
            arguments = call["arguments"] as JObject ?? new JObject();
            await Send(new JObject { ["native_tool_call"] = new JObject { ["id"] = id, ["name"] = call["name"]?.ToString() ?? name, ["arguments"] = arguments } });
        }
        // Only a validated device action: a rejected call would hand the device arguments the server refused to run.
        if (result["result"] is JObject outcome && outcome["success"]?.Value<bool>() == true && DeviceActionTool.IsDeviceAction(name))
        {
            deviceCalls.Add(new JObject { ["name"] = name, ["arguments"] = arguments });
        }
        await Send(new JObject { ["tool_result"] = new JObject { ["id"] = id, ["name"] = name, ["result"] = result["result"] } });
    }

    /// <inheritdoc/>
    public Task ToolNotRun(JObject result)
    {
        _pendingCalls.Remove(result["id"]?.ToString() ?? "");
        return Task.CompletedTask;
    }

    /// <summary>The device actions a turn took: each successful call to a device tool, as <c>{name, arguments}</c>.</summary>
    public static JArray DeviceCalls(JArray toolEvents)
    {
        JArray calls = [];
        foreach (JObject entry in toolEvents.OfType<JObject>())
        {
            if (entry["result"] is JObject result && result["success"]?.Value<bool>() == true && DeviceActionTool.IsDeviceAction(entry["name"]?.ToString()))
            {
                calls.Add(new JObject { ["name"] = entry["name"]?.ToString(), ["arguments"] = entry["arguments"] ?? new JObject() });
            }
        }
        return calls;
    }

    /// <inheritdoc/>
    public async Task Done(ToolTurnOutcome outcome)
    {
        _pendingCalls.Clear();
        if (outcome.Truncated)
        {
            await Send(new JObject { ["notice"] = $"tool round limit reached ({outcome.RoundLimit ?? "max_rounds"}); the model's last tool call was not run" });
        }
        await Send(new JObject
        {
            ["done"] = true,
            // Speech is the visible text: a reasoning trace is never read aloud.
            ["full_text"] = outcome.SpokenText ?? outcome.FullText,
            ["toolCalls"] = deviceCalls,
            ["stopReason"] = DoneStopReason(outcome),
        });
    }

    /// <summary>The <c>done</c> frame's <c>stopReason</c>: a round limit (or any tool-call stop) is <c>tool_round_limit</c>, never
    /// <c>tool_call</c>, which a relay reads as a call it still has to run.</summary>
    internal static string DoneStopReason(ToolTurnOutcome outcome)
        => outcome.Truncated || outcome.StopReason == "tool_call" ? "tool_round_limit" : outcome.StopReason;

    /// <summary>Sends one frame while the socket is open.</summary>
    private Task Send(JObject frame) => isOpen() ? send(frame) : Task.CompletedTask;
}

/// <summary>Records what a turn did, for callers that read the result rather than stream it.</summary>
public sealed class CollectingSink : IToolTurnSink
{
    /// <summary>Every tool call, in order.</summary>
    public List<JObject> Calls { get; } = [];

    /// <summary>Every tool result, in order.</summary>
    public List<JObject> Results { get; } = [];

    /// <summary>The outcome, once the turn is done.</summary>
    public ToolTurnOutcome Outcome { get; private set; }

    /// <inheritdoc/>
    public bool IsOpen => true;

    /// <inheritdoc/>
    public Task Chunk(string text) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Reasoning(string text) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ReasoningEnd() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Iteration(int round) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Status(string phase) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ToolCall(JObject call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ToolResult(JObject result)
    {
        Results.Add(result);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ToolNotRun(JObject result)
    {
        Results.Add(result);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Done(ToolTurnOutcome outcome)
    {
        Outcome = outcome;
        return Task.CompletedTask;
    }
}
