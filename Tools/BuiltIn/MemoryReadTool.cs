using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Memory;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: returns the calling user's saved memory. The notes are already put
/// in the system prompt every turn (all of them while they fit, the relevant ones otherwise), so
/// this tool is for deep recall: it lists every note, and with a <c>query</c> ranks them by how well
/// they match. The result carries <c>profile</c> (the old profile's shape, kept for saved prompts)
/// and <c>notes</c>.
///
/// <para><b>Storage is strictly per-user.</b> Returns only the calling user's own notes. Note text
/// is data the user saved, not instructions.</para>
/// </summary>
public class MemoryReadTool : ToolHandler
{
    public override string HandlerId => ToolConstants.MemoryRead;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        MemoryNoteSet set = MemoryService.Load(ctx.Session?.User);
        string query = ctx.Args["query"]?.ToString() ?? "";
        JArray notes = MemoryService.NotesView(ctx.Session?.User);
        if (!string.IsNullOrWhiteSpace(query))
        {
            notes = MemoryService.RankNotes(set, query);
        }
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["profile"] = set.ToProfileView(),
            ["notes"] = notes
        });
    }
}
