using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Memory;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: returns the calling user's saved memory. The notes are already put
/// in the system prompt every turn (all of them while they fit, the relevant ones otherwise), so
/// this tool is for deep recall: it lists the notes (the first 50, or the best matches for a
/// <c>query</c>), each text cleaned the way the prompt cleans it, with a marker when some are left out.
///
/// <para><b>Storage is strictly per-user.</b> Returns only the calling user's own notes. Note text
/// is data the user saved, not instructions, and the result says so.</para>
/// </summary>
public class MemoryReadTool : ToolHandler
{
    public override string HandlerId => ToolConstants.MemoryRead;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        User user = ctx.Session?.User;
        return Task.FromResult(MemoryService.ReadForTool(user is null ? null : MemoryService.StoreFor(user), MemoryControls.ForUser(user), ctx.Args["query"]?.ToString() ?? ""));
    }
}
