using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.Hooks;

/// <summary>The hooks that apply to one turn's tool calls, loaded once when the turn starts (so editing a hook mid-turn takes effect on the
/// next turn), plus the engine that applies them.</summary>
public sealed class ToolHookRunner(IReadOnlyList<ToolHook> hooks, HookEngine engine)
{
    /// <summary>The runner for a user's turn, using the real process runner for command hooks.</summary>
    public static ToolHookRunner For(User user) => new(HookService.GetHooks(user), new HookEngine(new ProcessHookCommandRunner()));

    /// <summary>Whether no enabled hook exists, so a call can skip hook handling altogether.</summary>
    public bool IsEmpty => !hooks.Any(h => h.Enabled);

    /// <summary>Runs the pre hooks for a call.</summary>
    public Task<PreHookOutcome> PreAsync(string toolId, string handlerId, JObject args, CancellationToken ct) => engine.RunPreAsync(hooks, toolId, handlerId, args, ct);

    /// <summary>Runs the post hooks for a finished call and returns their notes.</summary>
    public Task<List<string>> PostAsync(string toolId, string handlerId, JObject args, JObject result, CancellationToken ct) => engine.RunPostAsync(hooks, toolId, handlerId, args, result, ct);
}
