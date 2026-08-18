using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.Services;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: returns the calling user's full memory profile. The profile is
/// already injected into the system prompt every turn, so this tool is usually redundant —
/// but the LLM may want to call it explicitly for deep recall or to confirm what's stored.
///
/// <para><b>Storage is strictly per-user.</b> Returns only the calling user's own profile.</para>
/// </summary>
public class MemoryReadTool : ToolHandler
{
    public override string HandlerId => ToolConstants.MemoryRead;

    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject profile = UserProfileService.GetProfile(ctx.Session?.User);
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["profile"] = profile
        });
    }
}
