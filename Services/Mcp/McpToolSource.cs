using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.Mcp;

/// <summary>Offers the tools of every connected MCP server to the users who may call them (<c>llm_tool_mcp</c>). A user without it sees none,
/// so they are not listed to a user who could never run them.</summary>
public sealed class McpToolSource(McpServerManager manager) : IToolSource
{
    /// <inheritdoc/>
    public string Id => "mcp";

    /// <inheritdoc/>
    public IEnumerable<JObject> GetTools(User user)
        => user is not null && user.HasPermission(LLMAssistantAPI.PermToolMcp) ? manager.ToolDefinitions() : [];
}
