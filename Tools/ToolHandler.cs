using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace SwarmUI.Extensions.LLMAssistant.Tools;

/// <summary>Per-call context passed to a <see cref="ToolHandler"/>. A struct-shaped context lets
/// us add new fields (eg <c>ThreadId</c>, <c>ModelId</c>, future audit fields) without churning
/// every tool's signature each time.</summary>
public class ToolExecutionContext
{
    /// <summary>Arguments the LLM emitted in the tool call.</summary>
    public JObject Args = [];

    /// <summary>Calling user session. Always set by the executor; tools should not assume it's null-safe.</summary>
    public Session Session;

    /// <summary>The assistant that invoked this tool. Tools that read per-assistant config
    /// (eg <c>generate_image</c>'s default preset) should use this to resolve config.</summary>
    public string AssistantId;

    /// <summary>The chat thread id, if the call originated from a chat WS request. Lets tools
    /// attach side-effects to a thread (eg orphan-file GC keys off thread membership).</summary>
    public string ThreadId;

    /// <summary>The model id this LLM call is using. Lets tools tune their output for the model
    /// (eg shorter completions for context-limited models).</summary>
    public string ModelId;

    /// <summary>Cancellation token. Linked to the WebSocket lifecycle by the chat endpoint —
    /// honoring it is required for cancellation to actually work end-to-end.</summary>
    public System.Threading.CancellationToken Ct;
}

/// <summary>Abstract base class for tool handlers. Each concrete handler implements a single tool's execution logic.</summary>
public abstract class ToolHandler
{
    /// <summary>Unique identifier used to look up this handler via ToolRegistryService.</summary>
    public abstract string HandlerId { get; }

    /// <summary>Executes the tool with the given context and returns a JSON result.</summary>
    /// <returns>A JObject with at minimum a "success" boolean; may include additional result fields.</returns>
    public abstract Task<JObject> Execute(ToolExecutionContext ctx);

    /// <summary>Optionally enriches the tool definition with per-user/per-assistant context
    /// before it's shown to the LLM (eg <c>generate_image</c> injecting the user's available
    /// presets into its description). Default returns the definition unchanged.
    /// <para>Implementations must clone — do not mutate the input.</para></summary>
    /// <param name="toolDef">The stored tool definition (do not mutate).</param>
    /// <param name="session">Current user session.</param>
    /// <param name="assistantId">The assistant about to run this tool, if known. Tools that
    /// read per-assistant config should use this so per-character defaults take precedence.</param>
    public virtual JObject EnrichForUser(JObject toolDef, Session session, string assistantId = null) => toolDef;
}
