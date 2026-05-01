using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace SwarmUI.Extensions.LLMAssistant.Tools;

/// <summary>Abstract base class for tool handlers. Each concrete handler implements a single tool's execution logic.</summary>
public abstract class ToolHandler
{
    /// <summary>Unique identifier used to look up this handler via ToolRegistryService.</summary>
    public abstract string HandlerId { get; }

    /// <summary>Executes the tool with the given arguments and returns a JSON result.</summary>
    /// <param name="args">Arguments supplied by the LLM (validated against tool's parameter schema).</param>
    /// <param name="session">Current user session (for permission checks and user context).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A JObject with at minimum a "success" boolean; may include additional result fields.</returns>
    public abstract Task<JObject> Execute(JObject args, Session session, CancellationToken ct);

    /// <summary>Optionally enriches the tool definition with per-user context before it's
    /// shown to the LLM (eg injecting the user's available presets into <c>generate_image</c>'s
    /// description and parameter enum). The default returns the definition unchanged.
    /// <para>Implementations must not mutate <paramref name="toolDef"/> directly — clone it first.</para></summary>
    /// <param name="toolDef">The stored tool definition (do not mutate).</param>
    /// <param name="session">Current user session.</param>
    /// <returns>Either <paramref name="toolDef"/> as-is, or a clone with per-user enrichment applied.</returns>
    public virtual JObject EnrichForUser(JObject toolDef, Session session) => toolDef;
}
