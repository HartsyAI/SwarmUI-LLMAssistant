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

    /// <inheritdoc/>
    public Task Chunk(string text) => Send(new JObject { ["chunk"] = text });

    /// <inheritdoc/>
    public Task Iteration(int round) => Send(new JObject { ["iteration"] = round });

    /// <inheritdoc/>
    public Task Status(string phase) => Send(new JObject { ["status"] = phase });

    /// <inheritdoc/>
    public Task ToolCall(JObject call) => Send(new JObject { ["tool_call"] = call });

    /// <inheritdoc/>
    public Task ToolResult(JObject result) => Send(new JObject { ["tool_result"] = result });

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

/// <summary>The voice socket's events, in the shape AudioLab's client reads: <c>native_tool_call</c>, <c>tool_result</c>, and a
/// <c>done</c> frame carrying the device actions the turn took.</summary>
public sealed class VoiceWsSink(Func<JObject, Task> send, Func<bool> isOpen, JArray deviceCalls) : IToolTurnSink
{
    private readonly Dictionary<string, JObject> _argumentsById = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool IsOpen => isOpen();

    /// <inheritdoc/>
    public Task Chunk(string text) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Iteration(int round) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Status(string phase) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ToolCall(JObject call)
    {
        string id = call["id"]?.ToString() ?? "";
        _argumentsById[id] = call["arguments"] as JObject ?? new JObject();
        return send(new JObject { ["native_tool_call"] = new JObject { ["id"] = id, ["name"] = call["name"]?.ToString(), ["arguments"] = _argumentsById[id] } });
    }

    /// <inheritdoc/>
    public Task ToolResult(JObject result)
    {
        string id = result["id"]?.ToString() ?? "";
        if (result["result"] is JObject outcome && outcome["success"]?.Value<bool>() == true && DeviceActionTool.IsDeviceAction(result["name"]?.ToString()))
        {
            deviceCalls.Add(new JObject { ["name"] = result["name"]?.ToString(), ["arguments"] = _argumentsById.GetValueOrDefault(id) ?? new JObject() });
        }
        return send(new JObject { ["tool_result"] = new JObject { ["id"] = id, ["name"] = result["name"]?.ToString(), ["result"] = result["result"] } });
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
    public Task Done(ToolTurnOutcome outcome) => send(new JObject
    {
        ["done"] = true,
        ["full_text"] = outcome.FullText,
        ["toolCalls"] = deviceCalls,
        // A round limit reports as tool_call here, the same as before the runner: the device still knows the model wanted another call.
        ["stopReason"] = outcome.Truncated ? "tool_call" : outcome.StopReason,
    });
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
    public Task Done(ToolTurnOutcome outcome)
    {
        Outcome = outcome;
        return Task.CompletedTask;
    }
}
