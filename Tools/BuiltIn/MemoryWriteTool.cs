using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
using Hartsy.Extensions.LLMAssistant.Services.Memory;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: saves one note to the calling user's memory. The model uses this
/// whenever the user shares something worth remembering across chats (or asks it to "remember this"):
/// a preferred name, pronouns, a preference, what they are working on, or another durable fact.
/// The note records the chat it was saved in and its origin: <c>explicit</c> when the user ran the tool
/// or their latest message asked to have something remembered, otherwise <c>assistant</c> (the model chose
/// to save it, possibly because of something it read).
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

    /// <summary>What the model is told when it tries to save a note the user did not give it, in a chat that holds a document.</summary>
    public const string DocumentRefusal = "This chat holds an attached document, so a note is saved only when the user asks for it, in their own words. Nothing from a document is saved. Do not try again unless the user asks you to remember something they said.";

    /// <inheritdoc/>
    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        if (MemoryService.WriteRefusedWhilePaused(MemoryControls.ForUser(ctx.Session?.User)) is JObject refused)
        {
            return refused;
        }
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
        JObject thread = !string.IsNullOrEmpty(ctx.ThreadId) && ctx.Session?.User is not null ? ThreadStorageService.GetThread(ctx.Session.User, ctx.ThreadId) : null;
        // A call with no model call id was run by the user themselves (the tool's Run button).
        string origin = MemoryService.OriginForCall(thread, ctx.CallId is null);
        // A document can say anything, including "remember this": with one in the chat the model may only save what the user asked for, in the user's own words.
        if (ctx.CallId is not null && ChatDocuments.ThreadHasDocuments(thread) && !(origin == MemoryOrigins.Explicit && MemoryOpParser.SaidByUser(content, MemoryService.LatestUserAsk(thread))))
        {
            return new JObject { ["success"] = false, ["error"] = DocumentRefusal };
        }
        MemoryWriteResult result = await MemoryService.WriteAsync(ctx.Session, category, content, origin, ctx.ThreadId, ctx.Ct);
        return new JObject
        {
            ["success"] = result.Saved,
            ["category"] = category,
            ["summary"] = result.Summary,
            ["noteId"] = result.Note?.Id,
            ["origin"] = result.Note?.Origin
        };
    }
}
