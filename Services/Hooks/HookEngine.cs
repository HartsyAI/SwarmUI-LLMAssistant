using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Hooks;

/// <summary>What a command hook answered.</summary>
public sealed class HookCommandResult
{
    /// <summary>False when the program could not run or failed; <see cref="Error"/> says why.</summary>
    public bool Ok = true;

    /// <summary>Why it did not run (not shown to the model).</summary>
    public string Error;

    /// <summary><c>allow</c>, <c>deny</c> or <c>ask</c>; null means allow.</summary>
    public string Decision;

    /// <summary>The reason for a deny, or null.</summary>
    public string Message;

    /// <summary>Replacement arguments for the call, or null to leave them.</summary>
    public JObject Arguments;

    /// <summary>A note for the model, or null.</summary>
    public string Note;
}

/// <summary>Runs a command hook. Separate from <see cref="HookEngine"/> so the rules can be tested without starting a process.</summary>
public interface IHookCommandRunner
{
    /// <summary>Runs the hook's program with <paramref name="payload"/> as JSON on its standard input and reads its answer.</summary>
    Task<HookCommandResult> RunAsync(ToolHook hook, JObject payload, CancellationToken ct);
}

/// <summary>What the pre hooks decided about one call.</summary>
public sealed class PreHookOutcome
{
    /// <summary>A hook refused the call.</summary>
    public bool Denied;

    /// <summary>The reason to give the model when <see cref="Denied"/>.</summary>
    public string Message;

    /// <summary>The name of the hook that refused, or null.</summary>
    public string HookName;

    /// <summary>A hook wants the user asked first, whatever the approval mode.</summary>
    public bool ForceAsk;

    /// <summary>The arguments to run with: the call's own, or rewritten by hooks. Never the caller's object, which is left as it was.</summary>
    public JObject Arguments;

    /// <summary>Notes for the model, in the order hooks added them.</summary>
    public List<string> Notes = [];
}

/// <summary>Applies hooks to one tool call. Pure apart from the command runner it is given.
/// <para>Hooks run in the order they are given. The first deny stops the rest. Ask is remembered. Rewrites apply one after another, so a later
/// hook sees the arguments an earlier one produced. Notes pile up.</para></summary>
public sealed class HookEngine(IHookCommandRunner commands)
{
    /// <summary>Runs the pre hooks that apply to a call.</summary>
    public async Task<PreHookOutcome> RunPreAsync(IEnumerable<ToolHook> hooks, string toolId, string handlerId, JObject args, CancellationToken ct)
    {
        PreHookOutcome outcome = new() { Arguments = (JObject)(args ?? []).DeepClone() };
        foreach (ToolHook hook in hooks.Where(h => h.Enabled && h.Phase == ToolHook.PhasePre && HookMatcher.ToolMatches(h.Tool, toolId)))
        {
            if (hook.Kind == ToolHook.KindCommand)
            {
                HookCommandResult answer = await commands.RunAsync(hook, Payload(ToolHook.PhasePre, toolId, handlerId, outcome.Arguments, null), ct);
                if (ApplyCommandAnswer(hook, toolId, answer, outcome))
                {
                    return outcome;
                }
                continue;
            }
            if (!HookMatcher.Applies(hook, toolId, outcome.Arguments))
            {
                continue;
            }
            switch (hook.Action)
            {
                case ToolHook.ActionDeny:
                    return Deny(outcome, hook, hook.Message);
                case ToolHook.ActionAsk:
                    outcome.ForceAsk = true;
                    break;
                case ToolHook.ActionRewrite:
                    foreach (JProperty property in hook.Set.Properties())
                    {
                        outcome.Arguments[property.Name] = property.Value.DeepClone();
                    }
                    break;
                case ToolHook.ActionNote:
                    AddNote(outcome.Notes, hook.Message);
                    break;
            }
        }
        return outcome;
    }

    /// <summary>Runs the post hooks that apply to a finished call and returns the notes they add to its result.</summary>
    public async Task<List<string>> RunPostAsync(IEnumerable<ToolHook> hooks, string toolId, string handlerId, JObject args, JObject result, CancellationToken ct)
    {
        List<string> notes = [];
        foreach (ToolHook hook in hooks.Where(h => h.Enabled && h.Phase == ToolHook.PhasePost && HookMatcher.ToolMatches(h.Tool, toolId)))
        {
            if (hook.Kind == ToolHook.KindCommand)
            {
                HookCommandResult answer = await commands.RunAsync(hook, Payload(ToolHook.PhasePost, toolId, handlerId, args, result), ct);
                if (answer.Ok)
                {
                    AddNote(notes, answer.Note);
                }
                else
                {
                    Logs.Warning($"[LLMAssistant] Post hook '{hook.Name}' could not run for {toolId}: {answer.Error}");
                }
                continue;
            }
            if (HookMatcher.Applies(hook, toolId, args))
            {
                AddNote(notes, hook.Message);
            }
        }
        return notes;
    }

    /// <summary>Folds a command hook's answer into the outcome. Returns true when the call is refused and the remaining hooks should not run.</summary>
    private static bool ApplyCommandAnswer(ToolHook hook, string toolId, HookCommandResult answer, PreHookOutcome outcome)
    {
        if (!answer.Ok)
        {
            Logs.Warning($"[LLMAssistant] Hook '{hook.Name}' could not run for {toolId}: {answer.Error}");
            if (hook.OnError == ToolHook.OnErrorDeny)
            {
                Deny(outcome, hook, $"A policy hook ('{hook.Name}') could not run, so '{toolId}' was not run.");
                return true;
            }
            return false;
        }
        if (answer.Decision == "deny")
        {
            Deny(outcome, hook, answer.Message);
            return true;
        }
        if (answer.Decision == "ask")
        {
            outcome.ForceAsk = true;
        }
        if (answer.Arguments is not null)
        {
            outcome.Arguments = (JObject)answer.Arguments.DeepClone();
        }
        AddNote(outcome.Notes, answer.Note);
        return false;
    }

    /// <summary>Marks the outcome refused, with the hook's reason or a plain one.</summary>
    private static PreHookOutcome Deny(PreHookOutcome outcome, ToolHook hook, string message)
    {
        outcome.Denied = true;
        outcome.HookName = hook.Name;
        outcome.Message = string.IsNullOrWhiteSpace(message) ? $"Blocked by the '{hook.Name}' hook." : message.Trim();
        return outcome;
    }

    /// <summary>Adds a note unless it is blank.</summary>
    private static void AddNote(List<string> notes, string note)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            notes.Add(note.Trim());
        }
    }

    /// <summary>What a command hook is given on standard input.</summary>
    private static JObject Payload(string phase, string toolId, string handlerId, JObject args, JObject result) => new()
    {
        ["phase"] = phase,
        ["tool"] = toolId,
        ["handler"] = handlerId,
        ["arguments"] = args?.DeepClone() ?? new JObject(),
        ["result"] = result?.DeepClone(),
    };
}
