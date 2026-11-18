using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Hooks;

/// <summary>Runs command hooks as real processes. The hook's program gets the call as one JSON object on standard input and answers with JSON
/// on standard output: <c>{ "decision": "allow|deny|ask", "message": "...", "arguments": { ... }, "note": "..." }</c>, every field optional.
/// Exit code 0 means it answered (empty output means allow), exit code 2 means deny with what it wrote to standard error as the reason, and
/// anything else, a crash or running out of time counts as a failure (see <see cref="ToolHook.OnError"/>).</summary>
public sealed class ProcessHookCommandRunner : IHookCommandRunner
{
    /// <summary>The most output a hook may write that is read.</summary>
    private const int MaxOutputChars = 65536;

    /// <inheritdoc/>
    public async Task<HookCommandResult> RunAsync(ToolHook hook, JObject payload, CancellationToken ct)
    {
        ProcessResult result = await ProcessRunner.RunAsync(hook.Command, hook.Args, null, payload.ToString(Newtonsoft.Json.Formatting.None), TimeSpan.FromSeconds(hook.TimeoutSeconds), MaxOutputChars, ct);
        ct.ThrowIfCancellationRequested();
        if (result.StartError is not null)
        {
            return new HookCommandResult { Ok = false, Error = $"could not start '{hook.Command}': {result.StartError}" };
        }
        if (result.TimedOut)
        {
            return new HookCommandResult { Ok = false, Error = $"ran longer than {hook.TimeoutSeconds} seconds" };
        }
        if (result.ExitCode == 2)
        {
            return new HookCommandResult { Decision = "deny", Message = string.IsNullOrWhiteSpace(result.Stderr) ? null : result.Stderr };
        }
        if (result.ExitCode != 0)
        {
            return new HookCommandResult { Ok = false, Error = $"exited with code {result.ExitCode}: {Trim(result.Stderr)}" };
        }
        if (string.IsNullOrWhiteSpace(result.Stdout))
        {
            return new HookCommandResult();
        }
        try
        {
            JObject answer = JObject.Parse(result.Stdout);
            return new HookCommandResult
            {
                Decision = answer["decision"]?.ToString()?.Trim().ToLowerInvariant(),
                Message = answer["message"]?.ToString(),
                Arguments = answer["arguments"] as JObject,
                Note = answer["note"]?.ToString(),
            };
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new HookCommandResult { Ok = false, Error = "its output was not a JSON object" };
        }
    }

    /// <summary>A short form of text for a log line.</summary>
    private static string Trim(string text) => (text ?? "").Length > 200 ? text[..200] + "..." : text ?? "";
}
