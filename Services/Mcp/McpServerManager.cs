using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Mcp;

/// <summary>Where a server's connection stands.</summary>
public enum McpState
{
    /// <summary>The server is switched off; nothing is connected.</summary>
    Disabled,
    /// <summary>Starting up and listing its tools.</summary>
    Connecting,
    /// <summary>Connected, with its tools listed.</summary>
    Ready,
    /// <summary>The last attempt failed, or the connection dropped; the next call tries again once the wait is over.</summary>
    Failed,
}

/// <summary>Names a server's tools for the model. The id the model sees is the function name it calls, so it must be 1 to 64 letters, digits,
/// dashes and underscores, and must not collide with another tool.</summary>
public static class McpToolNaming
{
    /// <summary>What every MCP tool id starts with.</summary>
    public const string Prefix = "mcp__";

    /// <summary>The longest a function name may be.</summary>
    public const int MaxLength = 64;

    /// <summary>The id for <paramref name="toolName"/> on <paramref name="serverId"/>: <c>mcp__server__tool</c> with anything outside the allowed
    /// characters turned into an underscore. A name that is too long, or that collides with one in <paramref name="taken"/>, is cut short and
    /// given a few characters of a hash of the full name, so it stays unique and stable.</summary>
    public static string ToolId(string serverId, string toolName, ISet<string> taken)
    {
        string head = $"{Prefix}{serverId}__";
        string clean = Regex.Replace(toolName, "[^A-Za-z0-9_-]", "_");
        string id = head + clean;
        if (id.Length <= MaxLength && !taken.Contains(id))
        {
            return id;
        }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverId + "\n" + toolName)))[..6].ToLowerInvariant();
        int room = Math.Max(1, MaxLength - head.Length - 7);
        return $"{head}{(clean.Length > room ? clean[..room] : clean)}_{hash}";
    }
}

/// <summary>One server's connection and what is known about it.</summary>
public sealed class McpConnection
{
    /// <summary>The configuration this connection was made from.</summary>
    public McpServerConfig Config;

    /// <summary>What the connection depends on (<see cref="McpServerConfig.ConnectionStamp"/>), to notice when it must be made again.</summary>
    public string Stamp = "";

    /// <summary>Where it stands.</summary>
    public McpState State = McpState.Disabled;

    /// <summary>Why it failed, or empty.</summary>
    public string Error = "";

    /// <summary>The live client, or null.</summary>
    public McpClient Client;

    /// <summary>The server's own tools, as last listed (including any switched off here).</summary>
    public List<McpTool> Tools = [];

    /// <summary>The model-facing id of each tool that is on, to its tool.</summary>
    public Dictionary<string, McpTool> ById = [];

    /// <summary>How many attempts in a row have failed.</summary>
    public int Failures;

    /// <summary>The earliest a failed connection is tried again.</summary>
    public DateTime NextAttempt = DateTime.MinValue;

    /// <summary>The server's own name.</summary>
    public string ServerName = "";

    /// <summary>Set once the connection has been replaced or removed: an attempt still running then closes what it started instead of keeping it.</summary>
    public bool Retired;

    /// <summary>Held while connecting, so two callers do not start the same server twice.
    public readonly SemaphoreSlim Gate = new(1, 1);
}

/// <summary>Owns every MCP server connection: connects the ones that are switched on, keeps their tool lists, reconnects a dropped one (waiting
/// longer after each failure), and runs tool calls. The shared settings layer is the list of servers; this is what is live.</summary>
public sealed class McpServerManager
{
    /// <summary>The manager the extension uses.</summary>
    public static readonly McpServerManager Instance = new(McpServerStore.GetAll, DefaultClient);

    /// <summary>How long a server may take to start and list its tools.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait between attempts on a server that keeps failing.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly Func<IReadOnlyList<McpServerConfig>> _load;
    private readonly Func<McpServerConfig, McpClient> _factory;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, McpConnection> _connections = [];
    private readonly object _lock = new();

    /// <summary>A manager reading its servers from <paramref name="load"/> and connecting with <paramref name="factory"/>; both are replaced in tests.</summary>
    public McpServerManager(Func<IReadOnlyList<McpServerConfig>> load, Func<McpServerConfig, McpClient> factory, Func<DateTime> now = null)
    {
        _load = load;
        _factory = factory;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The real client for a server: a program it starts, or a URL it connects to.</summary>
    private static McpClient DefaultClient(McpServerConfig config)
        => new(config.Transport == McpServerConfig.TransportStdio ? McpStdioTransport.Start(config) : new McpHttpTransport(config));

    /// <summary>The wait before the next attempt after <paramref name="failures"/> failures in a row: 2, 4, 8 seconds and so on, up to <see cref="MaxBackoff"/>.</summary>
    public static TimeSpan BackoffFor(int failures)
        => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Clamp(failures, 1, 10))));

    /// <summary>Brings the connections in line with the stored servers: connects the ones switched on and not yet connected, and closes the ones
    /// removed, switched off or changed. Returns when the attempts it started have finished.</summary>
    public async Task SyncAsync()
    {
        List<McpServerConfig> wanted = [.. _load()];
        List<Task> attempts = [];
        List<McpConnection> toClose = [];
        lock (_lock)
        {
            foreach (string gone in _connections.Keys.Where(id => !wanted.Any(c => c.Id == id)).ToList())
            {
                toClose.Add(_connections[gone]);
                _connections.Remove(gone);
            }
            foreach (McpServerConfig config in wanted)
            {
                string stamp = config.ConnectionStamp();
                if (_connections.TryGetValue(config.Id, out McpConnection existing) && existing.Stamp == stamp)
                {
                    // Reached the same way: keep the connection, and pick up the rest (name, time limit, tools switched off) in place.
                    existing.Config = config;
                    if (existing.State == McpState.Ready)
                    {
                        ApplyTools(existing, existing.Tools);
                    }
                    continue;
                }
                if (existing is not null)
                {
                    toClose.Add(existing);
                }
                McpConnection fresh = new() { Config = config, Stamp = stamp, State = config.Enabled ? McpState.Connecting : McpState.Disabled };
                _connections[config.Id] = fresh;
                if (config.Enabled)
                {
                    attempts.Add(ConnectAsync(fresh));
                }
            }
        }
        foreach (McpConnection old in toClose)
        {
            old.Retired = true;
            Close(old);
        }
        await Task.WhenAll(attempts);
    }

    /// <summary>Connects one server: starts it, says hello, lists its tools. Never throws; a failure is recorded on the connection.</summary>
    private async Task ConnectAsync(McpConnection connection)
    {
        await connection.Gate.WaitAsync();
        try
        {
            if (connection.Retired || (connection.State == McpState.Ready && connection.Client is { IsAlive: true }))
            {
                return;
            }
            Close(connection, keepState: true);
            connection.State = McpState.Connecting;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel);
            timeout.CancelAfter(ConnectTimeout);
            McpClient client = null;
            try
            {
                client = _factory(connection.Config);
                await client.InitializeAsync(timeout.Token);
                List<McpTool> tools = await client.ListToolsAsync(timeout.Token);
                client.ToolsChanged += () => _ = RefreshToolsAsync(connection, client);
                lock (_lock)
                {
                    if (connection.Retired)
                    {
                        // Replaced or removed while it was starting: nothing refers to this connection any more, so do not keep its program.
                        client.Dispose();
                        return;
                    }
                    connection.Client = client;
                    connection.ServerName = client.ServerName;
                    ApplyTools(connection, tools);
                    connection.State = McpState.Ready;
                    connection.Error = "";
                    connection.Failures = 0;
                }
                Logs.Info($"[LLMAssistant] MCP server '{connection.Config.Id}' is ready with {connection.ById.Count} tool(s).");
            }
            catch (Exception ex)
            {
                client?.Dispose();
                string reason = ex is OperationCanceledException ? "It did not answer in time." : Describe(ex);
                lock (_lock)
                {
                    connection.Client = null;
                    connection.State = McpState.Failed;
                    connection.Error = reason;
                    connection.Failures++;
                    connection.NextAttempt = _now() + BackoffFor(connection.Failures);
                    connection.ById = [];
                }
                Logs.Warning($"[LLMAssistant] MCP server '{connection.Config.Id}' could not connect: {reason}");
            }
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    /// <summary>Re-lists a server's tools after it said they changed.</summary>
    private async Task RefreshToolsAsync(McpConnection connection, McpClient client)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel);
            timeout.CancelAfter(ConnectTimeout);
            List<McpTool> tools = await client.ListToolsAsync(timeout.Token);
            lock (_lock)
            {
                if (connection.Client == client)
                {
                    ApplyTools(connection, tools);
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Could not refresh MCP tools for '{connection.Config.Id}': {ex.Message}");
        }
    }

    /// <summary>Stores a listing on the connection and works out the id of each tool that is on. Call under the lock.</summary>
    private static void ApplyTools(McpConnection connection, List<McpTool> tools)
    {
        connection.Tools = tools;
        connection.ById = [];
        HashSet<string> taken = [];
        foreach (McpTool tool in tools.Where(t => !connection.Config.DisabledTools.Contains(t.Name)))
        {
            string id = McpToolNaming.ToolId(connection.Config.Id, tool.Name, taken);
            taken.Add(id);
            connection.ById[id] = tool;
        }
    }

    /// <summary>A short, readable reason for a connection failure.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        McpException mcp => $"The server answered with an error: {mcp.Message}",
        SwarmReadableErrorException readable => readable.Message,
        _ => ex.Message,
    };

    /// <summary>Closes a connection's client (and, for a program, the program and its children).</summary>
    private static void Close(McpConnection connection, bool keepState = false)
    {
        McpClient client = connection.Client;
        connection.Client = null;
        if (!keepState)
        {
            connection.State = McpState.Disabled;
        }
        try
        {
            client?.Dispose();
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Closing MCP server '{connection.Config?.Id}': {ex.Message}");
        }
    }

    /// <summary>Closes every connection (the host is shutting down).</summary>
    public void DisposeAll()
    {
        List<McpConnection> all;
        lock (_lock)
        {
            all = [.. _connections.Values];
            _connections.Clear();
        }
        foreach (McpConnection connection in all)
        {
            connection.Retired = true;
            Close(connection);
        }
    }

    /// <summary>Closes and reconnects one server now, whatever its backoff. Returns when the attempt has finished.</summary>
    public async Task RefreshAsync(string serverId)
    {
        McpConnection connection;
        lock (_lock)
        {
            _connections.TryGetValue(serverId, out connection);
        }
        if (connection is null || !connection.Config.Enabled)
        {
            return;
        }
        await connection.Gate.WaitAsync();
        try
        {
            Close(connection, keepState: true);
            connection.State = McpState.Failed;
            connection.NextAttempt = DateTime.MinValue;
        }
        finally
        {
            connection.Gate.Release();
        }
        await ConnectAsync(connection);
    }

    /// <summary>A snapshot of every connection, for the settings screen.</summary>
    public List<McpConnection> Snapshot()
    {
        lock (_lock)
        {
            return [.. _connections.Values];
        }
    }

    /// <summary>The tool definitions of every connected server, in the shape the tool list uses, for <see cref="Services.ToolSources"/>.</summary>
    public List<JObject> ToolDefinitions()
    {
        List<JObject> defs = [];
        lock (_lock)
        {
            foreach (McpConnection connection in _connections.Values.Where(c => c.State == McpState.Ready))
            {
                foreach ((string id, McpTool tool) in connection.ById)
                {
                    JObject schema = (JObject)tool.InputSchema.DeepClone();
                    if (schema["type"]?.ToString() != "object")
                    {
                        schema = new JObject { ["type"] = "object", ["properties"] = new JObject() };
                    }
                    string serverName = string.IsNullOrWhiteSpace(connection.Config.Name) ? connection.Config.Id : connection.Config.Name;
                    defs.Add(new JObject
                    {
                        ["id"] = id,
                        ["name"] = tool.Name,
                        ["description"] = string.IsNullOrWhiteSpace(tool.Description) ? $"A tool from the {serverName} server." : tool.Description,
                        ["parameters"] = schema,
                        ["handlerType"] = "mcp",
                        ["handlerId"] = ToolConstants.Mcp,
                        ["enabled"] = true,
                        ["isBuiltIn"] = false,
                        ["isDynamic"] = true,
                        ["source"] = "mcp",
                        ["server"] = connection.Config.Id,
                        ["serverName"] = serverName,
                        ["remoteName"] = tool.Name,
                        // Reading is free; reaching outside systems is network; anything else is a change, so Ask mode asks and Plan mode refuses.
                        // A server the admin does not trust to describe its own tools is asked about every time.
                        ["risk"] = connection.Config.AlwaysAsk ? "write" : tool.ReadOnly ? "read" : tool.OpenWorld ? "network" : "write",
                        ["timeoutSeconds"] = connection.Config.TimeoutSeconds,
                    });
                }
            }
        }
        return defs;
    }

    /// <summary>Runs one tool by its model-facing id. Reconnects a dropped server first (once its wait is over). Throws a readable error when the
    /// server is unavailable, the tool is unknown, or the call fails or runs out of time.</summary>
    public async Task<McpCallResult> CallAsync(string toolId, JObject arguments, TimeSpan timeout, CancellationToken ct)
    {
        McpConnection connection = null;
        McpTool tool = null;
        lock (_lock)
        {
            foreach (McpConnection candidate in _connections.Values)
            {
                if (candidate.ById.TryGetValue(toolId, out McpTool found))
                {
                    connection = candidate;
                    tool = found;
                    break;
                }
            }
        }
        if (connection is null)
        {
            // Not connected right now: find the server by the id's own prefix, so a dropped one can be brought back.
            string serverId = toolId.StartsWith(McpToolNaming.Prefix, StringComparison.Ordinal) ? toolId[McpToolNaming.Prefix.Length..].Split("__")[0] : "";
            lock (_lock)
            {
                _connections.TryGetValue(serverId, out connection);
            }
            if (connection is null)
            {
                throw new SwarmReadableErrorException($"The MCP tool '{toolId}' is not available.");
            }
        }
        if (connection.State != McpState.Ready || connection.Client is not { IsAlive: true })
        {
            if (connection.State == McpState.Failed && _now() < connection.NextAttempt)
            {
                int seconds = (int)Math.Ceiling((connection.NextAttempt - _now()).TotalSeconds);
                throw new SwarmReadableErrorException($"The MCP server '{connection.Config.Id}' is not available ({connection.Error}). It will be tried again in about {seconds} seconds.");
            }
            if (connection.State == McpState.Disabled)
            {
                throw new SwarmReadableErrorException($"The MCP server '{connection.Config.Id}' is switched off.");
            }
            await ConnectAsync(connection);
            lock (_lock)
            {
                tool = connection.ById.GetValueOrDefault(toolId);
            }
            if (connection.State != McpState.Ready || tool is null)
            {
                throw new SwarmReadableErrorException($"The MCP server '{connection.Config.Id}' is not available ({connection.Error}).");
            }
        }
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await connection.Client.CallToolAsync(tool.Name, arguments, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SwarmReadableErrorException($"The MCP tool '{tool.Name}' did not finish within {(int)timeout.TotalSeconds} seconds.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            // The connection dropped under the call; the next call reconnects (after a short wait).
            lock (_lock)
            {
                connection.State = McpState.Failed;
                connection.Error = ex.Message;
                connection.Failures++;
                connection.NextAttempt = _now() + BackoffFor(connection.Failures);
            }
            throw new SwarmReadableErrorException($"The MCP server '{connection.Config.Id}' dropped the connection: {ex.Message}");
        }
        catch (McpException ex)
        {
            throw new SwarmReadableErrorException($"The MCP server '{connection.Config.Id}' refused the call: {ex.Message}");
        }
    }

    /// <summary>Resolves a model-facing id to its tool and server, or null when it is not connected.</summary>
    public (McpServerConfig Server, McpTool Tool)? Resolve(string toolId)
    {
        lock (_lock)
        {
            foreach (McpConnection connection in _connections.Values)
            {
                if (connection.ById.TryGetValue(toolId, out McpTool tool))
                {
                    return (connection.Config, tool);
                }
            }
        }
        return null;
    }
}
