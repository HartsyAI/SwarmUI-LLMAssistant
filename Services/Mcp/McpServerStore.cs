using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.Mcp;

/// <summary>Stores the MCP servers in the <b>shared</b> settings layer, under <c>mcpServers</c>. Only an admin who holds <c>llm_mcp_manage</c> changes
/// them, and they apply to every user who may call MCP tools. (A personal layer, with per-user secrets, is not offered yet: a server is one
/// connection shared by everyone, which is what keeps a stdio program from being started once per user.)</summary>
public static class McpServerStore
{
    /// <summary>The settings key the shared layer keeps its servers under, as an object of server id to server.</summary>
    public const string SettingsKey = "mcpServers";

    /// <summary>Every valid stored server, in id order. A stored entry that no longer validates is skipped rather than connected.</summary>
    public static IReadOnlyList<McpServerConfig> GetAll()
    {
        List<McpServerConfig> result = [];
        if (SettingsService.GetSettings()[SettingsKey] is JObject servers)
        {
            foreach (JProperty property in servers.Properties())
            {
                if (property.Value is not JObject json)
                {
                    continue;
                }
                json["id"] ??= property.Name;
                McpServerConfig config = McpServerConfig.FromJson(json);
                if (config.Validate() is null)
                {
                    result.Add(config);
                }
            }
        }
        return [.. result.OrderBy(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>The server with this id, or null.</summary>
    public static McpServerConfig Get(string id) => GetAll().FirstOrDefault(c => c.Id == id);

    /// <summary>Creates or updates a server. Returns the id, or null with the reason in <paramref name="error"/>. Needs <c>llm_mcp_manage</c>; a
    /// secret sent back still hidden keeps its stored value.</summary>
    public static string Save(JObject data, User user, out string error)
    {
        error = null;
        if (user is null || !user.HasPermission(WebAPI.LLMAssistantAPI.PermMcpManage))
        {
            error = "Managing MCP servers needs the LLM MCP Manage permission.";
            return null;
        }
        if (data is null)
        {
            error = "No server data provided.";
            return null;
        }
        McpServerConfig config = McpServerConfig.FromJson(data);
        error = config.Validate();
        if (error is not null)
        {
            return null;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject layer = SettingsService.GetSettings();
            JObject servers = layer[SettingsKey] as JObject ?? [];
            McpServerConfig stored = servers[config.Id] is JObject old ? McpServerConfig.FromJson(old) : null;
            if (stored is null && servers.Count >= McpServerConfig.MaxServers)
            {
                error = $"Up to {McpServerConfig.MaxServers} servers can be set up.";
                return null;
            }
            config.KeepHiddenSecrets(stored);
            JObject json = config.ToJson();
            json["created"] = servers[config.Id]?["created"] ?? DateTime.UtcNow.ToString("o");
            json["updated"] = DateTime.UtcNow.ToString("o");
            servers[config.Id] = json;
            layer[SettingsKey] = servers;
            SettingsService.ReplaceSharedSettings(layer);
            AuditLogService.RecordSharedWrite(stored is null ? "create" : "update", $"mcp:{config.Id}", user,
                new JObject { ["name"] = config.Name, ["transport"] = config.Transport });
        }
        return config.Id;
    }

    /// <summary>Deletes a server. False when there is none with that id or the user may not manage servers.</summary>
    public static bool Delete(string id, User user)
    {
        if (user is null || !user.HasPermission(WebAPI.LLMAssistantAPI.PermMcpManage))
        {
            return false;
        }
        lock (SettingsService.SettingsLock)
        {
            JObject layer = SettingsService.GetSettings();
            if (layer[SettingsKey] is not JObject servers || !servers.Remove(id))
            {
                return false;
            }
            layer[SettingsKey] = servers;
            SettingsService.ReplaceSharedSettings(layer);
            AuditLogService.RecordSharedWrite("delete", $"mcp:{id}", user);
        }
        return true;
    }
}
