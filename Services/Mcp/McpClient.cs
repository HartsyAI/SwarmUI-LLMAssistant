using System.Text;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Mcp;

/// <summary>One tool an MCP server offers.</summary>
/// <param name="Name">The server's own name for it.</param>
/// <param name="Description">What it does, as the server describes it.</param>
/// <param name="InputSchema">The JSON Schema of its arguments (an object schema).</param>
/// <param name="ReadOnly">The server says it changes nothing (<c>readOnlyHint</c>).</param>
/// <param name="OpenWorld">The server says it reaches outside systems (<c>openWorldHint</c>).</param>
public sealed record McpTool(string Name, string Description, JObject InputSchema, bool ReadOnly, bool OpenWorld);

/// <summary>What a tool call returned.</summary>
/// <param name="IsError">The server marked the result as a failure.</param>
/// <param name="Text">The text parts, joined.</param>
/// <param name="Structured">The structured result, when the server sent one.</param>
/// <param name="OtherParts">How many parts were not text (images, audio, resources), which are not passed on.</param>
public sealed record McpCallResult(bool IsError, string Text, JToken Structured, int OtherParts);

/// <summary>The server answered with a JSON-RPC error.</summary>
public sealed class McpException : Exception
{
    /// <summary>The JSON-RPC error code.</summary>
    public readonly int Code;

    /// <summary>An error with the server's code and message.</summary>
    public McpException(int code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>The MCP protocol over a transport: start-up, listing tools and calling one. Only what a tool-using chat needs; the server's resources,
/// prompts and requests for the client's help (sampling, roots) are not used.</summary>
public sealed class McpClient : IDisposable
{
    /// <summary>The protocol version the client asks for; a server may answer with an older one it supports.</summary>
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>Most pages of tools followed, so a server that never stops paging cannot loop forever.</summary>
    private const int MaxToolPages = 20;

    /// <summary>Most tools taken from one server.</summary>
    public const int MaxTools = 500;

    private readonly IMcpTransport _transport;
    private long _nextId;

    /// <summary>The server's own name, from its start-up answer (empty before that).</summary>
    public string ServerName = "";

    /// <summary>The protocol version the server agreed to.</summary>
    public string NegotiatedVersion = "";

    /// <summary>Raised when the server says its tool list changed.</summary>
    public event Action ToolsChanged;

    /// <summary>Whether the connection is still up.</summary>
    public bool IsAlive => _transport.IsAlive;

    /// <summary>The transport this client talks over.</summary>
    public IMcpTransport Transport => _transport;

    /// <summary>A client over <paramref name="transport"/>, which it owns and closes.</summary>
    public McpClient(IMcpTransport transport)
    {
        _transport = transport;
        _transport.ServerNotification += message =>
        {
            if (message["method"]?.ToString() == "notifications/tools/list_changed")
            {
                ToolsChanged?.Invoke();
            }
        };
    }

    /// <summary>Starts the session: tells the server what the client is and speaks, reads what it agrees to, and says it is ready.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        JObject result = await CallAsync("initialize", new JObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = "SwarmUI-LLMAssistant", ["version"] = "1" },
        }, ct);
        ServerName = result["serverInfo"]?["name"]?.ToString() ?? "";
        NegotiatedVersion = result["protocolVersion"]?.ToString() ?? ProtocolVersion;
        if (_transport is McpHttpTransport http)
        {
            http.SetProtocolVersion(NegotiatedVersion);
        }
        await _transport.NotifyAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);
    }

    /// <summary>Lists the server's tools, following its pages.</summary>
    public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
    {
        List<McpTool> tools = [];
        string cursor = null;
        for (int page = 0; page < MaxToolPages && tools.Count < MaxTools; page++)
        {
            JObject result = await CallAsync("tools/list", cursor is null ? new JObject() : new JObject { ["cursor"] = cursor }, ct);
            foreach (JObject tool in (result["tools"] as JArray ?? []).OfType<JObject>())
            {
                string name = tool["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                JObject annotations = tool["annotations"] as JObject;
                JObject schema = tool["inputSchema"] as JObject ?? new JObject { ["type"] = "object", ["properties"] = new JObject() };
                tools.Add(new McpTool(name, tool["description"]?.ToString() ?? tool["title"]?.ToString() ?? "", schema,
                    annotations?["readOnlyHint"]?.Type == JTokenType.Boolean && annotations["readOnlyHint"].Value<bool>(),
                    annotations?["openWorldHint"]?.Type == JTokenType.Boolean && annotations["openWorldHint"].Value<bool>()));
            }
            cursor = result["nextCursor"]?.ToString();
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }
        return tools;
    }

    /// <summary>Calls one of the server's tools.</summary>
    public async Task<McpCallResult> CallToolAsync(string name, JObject arguments, CancellationToken ct)
    {
        JObject result = await CallAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() }, ct);
        StringBuilder text = new();
        int others = 0;
        foreach (JObject part in (result["content"] as JArray ?? []).OfType<JObject>())
        {
            if (part["type"]?.ToString() == "text")
            {
                if (text.Length > 0)
                {
                    text.Append('\n');
                }
                text.Append(part["text"]?.ToString());
            }
            else
            {
                others++;
            }
        }
        bool isError = result["isError"]?.Type == JTokenType.Boolean && result["isError"].Value<bool>();
        return new McpCallResult(isError, text.ToString(), result["structuredContent"], others);
    }

    /// <summary>Sends one request and returns its <c>result</c>; a JSON-RPC error becomes an <see cref="McpException"/>.</summary>
    private async Task<JObject> CallAsync(string method, JObject parameters, CancellationToken ct)
    {
        JObject request = new() { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _nextId), ["method"] = method, ["params"] = parameters };
        JObject response = await _transport.RequestAsync(request, ct);
        if (response["error"] is JObject error)
        {
            throw new McpException(error["code"]?.Value<int?>() ?? -32000, error["message"]?.ToString() ?? "The server reported an error.");
        }
        return response["result"] as JObject ?? [];
    }

    /// <inheritdoc/>
    public void Dispose() => _transport.Dispose();
}
