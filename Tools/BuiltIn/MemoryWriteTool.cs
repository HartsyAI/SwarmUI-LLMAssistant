using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Memory;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: saves one note to the calling user's memory. The model uses this
/// whenever the user shares something worth remembering across chats (or asks it to "remember this"):
/// a preferred name, pronouns, a preference, what they are working on, or another durable fact.
/// The note records the chat it was saved in and that it was saved with this tool (origin
/// <c>explicit</c>).
///
/// <para><b>Storage is strictly per-user.</b> A user can never write to (or read) another
/// user's notes. The handler delegates to <see cref="MemoryService"/>, which scopes every
/// read/write to <c>session.User</c>.</para>
/// </summary>
public class MemoryWriteTool : ToolHandler
{
    public override string HandlerId => ToolConstants.MemoryWrite;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Write;

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        string category = args["category"]?.ToString();
        string content = args["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(category))
        {
            return new JObject { ["success"] = false, ["error"] = "category is required" };
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return new JObject { ["success"] = false, ["error"] = "content is required" };
        }
        MemoryWriteResult result = await MemoryService.WriteAsync(ctx.Session, category, content, MemoryOrigins.Explicit, ctx.ThreadId, ctx.Ct);
        return new JObject
        {
            ["success"] = result.Saved,
            ["category"] = category,
            ["summary"] = result.Summary,
            ["noteId"] = result.Note?.Id
        };
    }
}
