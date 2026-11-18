using Hartsy.Extensions.LLMAssistant.Services.Mcp;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Runs a tool an MCP server offers. One handler serves every MCP tool; the tool's own definition says which server and tool it is, and
/// what it can do (<c>risk</c>, from the server's read-only and open-world hints) and how long it may take.</summary>
public sealed class McpToolHandler : ToolHandler
{
    /// <summary>The longest text of a result passed to the model; more is cut with a note.</summary>
    public const int MaxResultChars = 50_000;

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.Mcp;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Write;

    /// <inheritdoc/>
    public override ToolRisk RiskFor(JObject toolDef) => toolDef?["risk"]?.ToString() switch
    {
        "read" => ToolRisk.Read,
        "network" => ToolRisk.Network,
        _ => ToolRisk.Write,
    };

    /// <summary>The time the server's own limit allows a call (from the tool's definition).</summary>
    public static TimeSpan CallLimit(JObject toolDef)
        => TimeSpan.FromSeconds(Math.Clamp(toolDef?["timeoutSeconds"]?.Value<int?>() ?? 60, 1, McpServerConfig.MaxTimeoutSeconds));

    /// <summary>The limit the executor puts around a call: the server's own plus a few seconds, so the call gives up first and can say why.</summary>
    public override TimeSpan TimeoutFor(JObject toolDef) => CallLimit(toolDef) + TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        McpCallResult result = await McpServerManager.Instance.CallAsync(ctx.ToolId, ctx.Args, CallLimit(ctx.ToolDef), ctx.Ct);
        return ToResult(result);
    }

    /// <summary>The result the model reads for a server's answer.</summary>
    public static JObject ToResult(McpCallResult result)
    {
        string text = result.Text ?? "";
        bool cut = text.Length > MaxResultChars;
        if (cut)
        {
            text = text[..MaxResultChars];
        }
        JObject json = new() { ["success"] = !result.IsError, ["text"] = text };
        if (result.IsError)
        {
            json["error"] = text.Length > 0 ? text : "The tool reported an error.";
        }
        if (result.Structured is { Type: not JTokenType.Null })
        {
            json["structured"] = result.Structured.DeepClone();
        }
        List<string> notes = [];
        if (cut)
        {
            notes.Add($"The text was cut to {MaxResultChars} characters.");
        }
        if (result.OtherParts > 0)
        {
            notes.Add($"{result.OtherParts} part(s) that are not text (images, audio or files) were not passed on.");
        }
        if (notes.Count > 0)
        {
            json["note"] = string.Join(" ", notes);
        }
        return json;
    }
}
