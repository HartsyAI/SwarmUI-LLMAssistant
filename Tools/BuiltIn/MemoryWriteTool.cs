using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: writes a single piece of memory to the calling user's profile.
/// The LLM uses this whenever the user shares something worth remembering across sessions —
/// preferred name, pronouns, preferences, current work, or other facts.
///
/// <para><b>Storage is strictly per-user.</b> A user can never write to (or read) another
/// user's profile. The handler delegates to <see cref="UserProfileService"/>, which scopes
/// every read/write to <c>session.User</c>.</para>
/// </summary>
public class MemoryWriteTool : ToolHandler
{
    public override string HandlerId => ToolConstants.MemoryWrite;

    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        string category = args["category"]?.ToString();
        string content = args["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(category))
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "category is required" });
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "content is required" });
        }
        string summary = UserProfileService.WriteMemory(ctx.Session?.User, category, content);
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["category"] = category,
            ["summary"] = summary
        });
    }
}
