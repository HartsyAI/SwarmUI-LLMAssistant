using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Mcp;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Routes for the MCP Servers screen: list with status, save, delete, test and reconnect. Changing anything needs
/// <c>llm_mcp_manage</c>; the list is open to anyone who can open settings, with secrets, programs and arguments left out unless they may manage.</summary>
public static class McpEndpoints
{
    /// <summary>Every server with where its connection stands and its tools. Request: none. Returns
    /// <c>{success, servers[], canManage, canCall}</c>; each server is its configuration (secrets hidden) plus <c>state</c>
    /// (<c>disabled</c>, <c>connecting</c>, <c>ready</c> or <c>failed</c>), <c>error</c>, <c>serverName</c> and <c>tools[]</c>.</summary>
    public static Task<JObject> LLMAssistantGetMcpServers(Session session)
    {
        bool manage = CanManage(session);
        JArray servers = [];
        Dictionary<string, McpConnection> live = McpServerManager.Instance.Snapshot().ToDictionary(c => c.Config.Id);
        foreach (McpServerConfig config in McpServerStore.GetAll())
        {
            servers.Add(ServerJson(config, live.GetValueOrDefault(config.Id), manage));
        }
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["servers"] = servers,
            ["canManage"] = manage,
            ["canCall"] = session.User?.HasPermission(LLMAssistantAPI.PermToolMcp) ?? false,
        });
    }

    /// <summary>Creates or updates a server and connects it. Request: <c>{ server }</c> (object or JSON string). Secrets sent back as the hidden
    /// placeholder keep their stored value. Returns <c>{success, id, server}</c>; a server that could not connect is still saved, and says why.</summary>
    public static async Task<JObject> LLMAssistantSaveMcpServer(Session session, JObject rawInput)
    {
        JObject data = JsonInput.ReadObject(rawInput["server"]);
        string id = McpServerStore.Save(data, session.User, out string error);
        if (id is null)
        {
            return new JObject { ["success"] = false, ["error"] = error ?? "The server could not be saved." };
        }
        await McpServerManager.Instance.SyncAsync();
        McpServerConfig saved = McpServerStore.Get(id);
        McpConnection live = McpServerManager.Instance.Snapshot().FirstOrDefault(c => c.Config.Id == id);
        return new JObject { ["success"] = true, ["id"] = id, ["server"] = saved is null ? null : ServerJson(saved, live, true) };
    }

    /// <summary>Removes a server and closes its connection. Request: <c>{ serverId }</c>.</summary>
    public static async Task<JObject> LLMAssistantDeleteMcpServer(Session session, string serverId)
    {
        if (!McpServerStore.Delete(serverId, session.User))
        {
            return new JObject { ["success"] = false, ["error"] = "Server not found, or you may not manage MCP servers." };
        }
        await McpServerManager.Instance.SyncAsync();
        return new JObject { ["success"] = true };
    }

    /// <summary>Connects to a server as described, lists its tools and closes it again, saving nothing, so a setup can be tried before it is saved.
    /// Request: <c>{ server }</c>. Starting a program or connecting to a URL needs <c>llm_mcp_manage</c>. Returns <c>{success, serverName, tools[]}</c>.</summary>
    public static async Task<JObject> LLMAssistantTestMcpServer(Session session, JObject rawInput)
    {
        if (!CanManage(session))
        {
            return new JObject { ["success"] = false, ["error"] = "Testing a server starts its program or connects to its URL, so it needs the LLM MCP Manage permission." };
        }
        JObject data = JsonInput.ReadObject(rawInput["server"]);
        if (data is null)
        {
            return new JObject { ["success"] = false, ["error"] = "No server data provided." };
        }
        McpServerConfig config = McpServerConfig.FromJson(data);
        if (string.IsNullOrEmpty(config.Id))
        {
            config.Id = "test";
        }
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            config.Name = "Test";
        }
        config.KeepHiddenSecrets(McpServerStore.Get(config.Id));
        string problem = config.Validate();
        if (problem is not null)
        {
            return new JObject { ["success"] = false, ["error"] = problem };
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel);
        timeout.CancelAfter(McpServerManager.ConnectTimeout);
        McpClient client = null;
        try
        {
            client = config.Transport == McpServerConfig.TransportStdio ? new McpClient(McpStdioTransport.Start(config)) : new McpClient(new McpHttpTransport(config));
            await client.InitializeAsync(timeout.Token);
            List<McpTool> tools = await client.ListToolsAsync(timeout.Token);
            return new JObject
            {
                ["success"] = true,
                ["serverName"] = client.ServerName,
                ["tools"] = new JArray(tools.Select(t => ToolJson(t, config, true))),
            };
        }
        catch (OperationCanceledException)
        {
            return new JObject { ["success"] = false, ["error"] = "The server did not answer in time." };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex is McpException mcp ? $"The server answered with an error: {mcp.Message}" : ex.Message };
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>Closes a server's connection and connects it again now, ignoring any wait after a failure. Request: <c>{ serverId }</c>.
    /// Returns the server's new status.</summary>
    public static async Task<JObject> LLMAssistantReconnectMcpServer(Session session, string serverId)
    {
        if (!CanManage(session))
        {
            return new JObject { ["success"] = false, ["error"] = "Reconnecting a server needs the LLM MCP Manage permission." };
        }
        McpServerConfig config = McpServerStore.Get(serverId);
        if (config is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Server not found." };
        }
        await McpServerManager.Instance.SyncAsync();
        await McpServerManager.Instance.RefreshAsync(serverId);
        McpConnection live = McpServerManager.Instance.Snapshot().FirstOrDefault(c => c.Config.Id == serverId);
        return new JObject { ["success"] = true, ["server"] = ServerJson(config, live, true) };
    }

    /// <summary>Whether the caller may manage MCP servers.</summary>
    private static bool CanManage(Session session) => session.User?.HasPermission(LLMAssistantAPI.PermMcpManage) ?? false;

    /// <summary>A server for the browser: its configuration with secrets hidden, and its live status and tools.</summary>
    private static JObject ServerJson(McpServerConfig config, McpConnection live, bool manage)
    {
        JObject json = config.ToPublicJson(manage);
        McpState state = live?.State ?? (config.Enabled ? McpState.Connecting : McpState.Disabled);
        json["state"] = state.ToString().ToLowerInvariant();
        // A failure message can name host paths and the program's own output, which only someone who manages servers needs.
        json["error"] = manage ? live?.Error ?? "" : (live?.State == McpState.Failed ? "The server could not be reached." : "");
        json["serverName"] = live?.ServerName ?? "";
        // The id of a tool that is on is the one the manager gave it (a name that collided got a suffix); a tool that is off has none yet.
        Dictionary<McpTool, string> ids = (live?.ById ?? []).ToDictionary(pair => pair.Value, pair => pair.Key);
        json["tools"] = new JArray((live?.Tools ?? []).Select(t => ToolJson(t, config, !config.DisabledTools.Contains(t.Name), ids.GetValueOrDefault(t))));
        return json;
    }

    /// <summary>One of a server's tools for the browser.</summary>
    private static JObject ToolJson(McpTool tool, McpServerConfig config, bool enabled, string id = null)
    {
        return new JObject
        {
            ["name"] = tool.Name,
            ["id"] = id ?? McpToolNaming.ToolId(config.Id, tool.Name, new HashSet<string>()),
            ["description"] = tool.Description,
            ["enabled"] = enabled,
            ["readOnly"] = tool.ReadOnly,
            ["openWorld"] = tool.OpenWorld,
        };
    }
}
