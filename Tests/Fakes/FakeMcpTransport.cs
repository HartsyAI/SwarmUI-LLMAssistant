using Hartsy.Extensions.LLMAssistant.Services.Mcp;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Tests.Fakes;

/// <summary>An in-memory MCP server: answers the protocol from a script, records what it was sent, and can be told to drop.</summary>
internal sealed class FakeMcpTransport : IMcpTransport
{
    /// <summary>The tools it lists, as <c>tools/list</c> entries; one page unless <see cref="Pages"/> splits them.</summary>
    public List<JObject> Tools = [];

    /// <summary>When above 1, the tools are served in this many pages through <c>nextCursor</c>.</summary>
    public int Pages = 1;

    /// <summary>What <c>tools/call</c> answers for a tool name; a missing one answers with its name as text.</summary>
    public Func<string, JObject, JObject> OnCall = (name, _) => new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = $"called {name}" }) };

    /// <summary>When set, <c>initialize</c> fails with this.</summary>
    public Exception InitializeFailure;

    /// <summary>When set, <c>initialize</c> waits until this task finishes (a server that is slow to start).</summary>
    public Task HoldInitialize;

    /// <summary>When set, a <c>tools/call</c> waits until this task finishes (or the call is cancelled).</summary>
    public Task HoldCalls;

    /// <summary>Every request and notification, in order.</summary>
    public List<JObject> Seen { get; } = [];

    /// <summary>Whether it has been disposed.</summary>
    public bool Disposed;

    private bool _dropped;

    /// <inheritdoc/>
    public event Action<JObject> ServerNotification;

    /// <inheritdoc/>
    public bool IsAlive => !Disposed && !_dropped;

    /// <summary>Makes the connection look gone, as when the server's process exits.</summary>
    public void Drop() => _dropped = true;

    /// <summary>Sends a notification as the server.</summary>
    public void Notify(string method) => ServerNotification?.Invoke(new JObject { ["jsonrpc"] = "2.0", ["method"] = method });

    /// <inheritdoc/>
    public async Task<JObject> RequestAsync(JObject request, CancellationToken ct)
    {
        Seen.Add(request);
        if (!IsAlive)
        {
            throw new IOException("The server program is not running.");
        }
        JToken id = request["id"];
        string method = request["method"]!.ToString();
        JObject parameters = request["params"] as JObject ?? [];
        switch (method)
        {
            case "initialize":
                if (HoldInitialize is not null)
                {
                    await HoldInitialize;
                }
                if (InitializeFailure is not null)
                {
                    throw InitializeFailure;
                }
                return Result(id, new JObject { ["protocolVersion"] = "2025-06-18", ["serverInfo"] = new JObject { ["name"] = "fake-server", ["version"] = "1" }, ["capabilities"] = new JObject() });
            case "tools/list":
            {
                int page = parameters["cursor"] is { } cursor ? int.Parse(cursor.ToString()) : 0;
                int size = (int)Math.Ceiling(Tools.Count / (double)Math.Max(1, Pages));
                JObject result = new() { ["tools"] = new JArray(Tools.Skip(page * size).Take(size)) };
                if ((page + 1) * size < Tools.Count)
                {
                    result["nextCursor"] = (page + 1).ToString();
                }
                return Result(id, result);
            }
            case "tools/call":
                if (HoldCalls is not null)
                {
                    await Task.WhenAny(HoldCalls, Task.Delay(Timeout.Infinite, ct));
                    ct.ThrowIfCancellationRequested();
                }
                try
                {
                    return Result(id, OnCall(parameters["name"]!.ToString(), parameters["arguments"] as JObject ?? []));
                }
                catch (McpException error)
                {
                    return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = error.Code, ["message"] = error.Message } };
                }
            default:
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32601, ["message"] = $"no such method {method}" } };
        }
    }

    /// <inheritdoc/>
    public Task NotifyAsync(JObject notification, CancellationToken ct)
    {
        Seen.Add(notification);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose() => Disposed = true;

    /// <summary>A <c>tools/list</c> entry.</summary>
    public static JObject Tool(string name, string description = "", bool readOnly = false, bool openWorld = false, JObject schema = null) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema ?? new JObject { ["type"] = "object", ["properties"] = new JObject { ["text"] = new JObject { ["type"] = "string" } } },
        ["annotations"] = new JObject { ["readOnlyHint"] = readOnly, ["openWorldHint"] = openWorld },
    };

    private static JObject Result(JToken id, JObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
}
