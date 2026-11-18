using System.IO;
using System.Runtime.InteropServices;
using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: execute a shell command and return stdout/stderr/exit code.
/// DANGEROUS — this gives the LLM full access to the host shell. Disabled by default in the
/// tool definition (see <see cref="Services.ToolRegistryService.BuildDefaultTools"/>); users must
/// explicitly opt in by enabling it globally and per-assistant.
/// <para>Runs through the shared <see cref="ProcessRunner"/>, which kills the whole process tree on a timeout or a Stop. The output cap applies to
/// standard output and standard error each (<c>maxOutputBytes</c> is kept as the argument's name and now means characters of each).</para></summary>
public class ShellExecTool : ToolHandler
{
    /// <summary>The most seconds a command may run (the per-call <c>timeoutSeconds</c> is clamped to this).</summary>
    public const int MaxSeconds = 300;

    public override string HandlerId => ToolConstants.ShellExec;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Exec;

    /// <inheritdoc/>
    public override TimeSpan TimeoutFor(JObject toolDef) => TimeSpan.FromSeconds(MaxSeconds + 10);

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        string command = args["command"]?.ToString();
        if (string.IsNullOrWhiteSpace(command))
        {
            return new JObject { ["success"] = false, ["error"] = "command is required" };
        }
        int timeoutSeconds = args["timeoutSeconds"]?.Value<int?>() ?? 30;
        if (timeoutSeconds <= 0) timeoutSeconds = 30;
        if (timeoutSeconds > MaxSeconds) timeoutSeconds = MaxSeconds;
        int maxOutput = args["maxOutputBytes"]?.Value<int?>() ?? 65536;
        if (maxOutput <= 0) maxOutput = 65536;
        if (maxOutput > 1024 * 1024) maxOutput = 1024 * 1024;

        // Working directory: defaults to SwarmUI Data; any supplied path is resolved relative to Data and must stay inside it (sandbox).
        string dataRoot = Path.GetFullPath("Data");
        string workingDir = dataRoot;
        string requestedCwd = args["workingDirectory"]?.ToString();
        if (!string.IsNullOrWhiteSpace(requestedCwd))
        {
            if (!PathSandbox.TryResolve(dataRoot, requestedCwd, out string resolved, out string error))
            {
                return new JObject { ["success"] = false, ["error"] = $"workingDirectory is outside the SwarmUI Data directory (sandbox violation). {error}" };
            }
            if (!Directory.Exists(resolved))
            {
                return new JObject { ["success"] = false, ["error"] = $"workingDirectory does not exist: {requestedCwd}" };
            }
            workingDir = resolved;
        }

        Logs.Info($"[LLMAssistant] ShellExecTool running: {command} (cwd={workingDir}, timeout={timeoutSeconds}s)");
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        TimeSpan limit = TimeSpan.FromSeconds(timeoutSeconds);
        // cmd.exe parses its own command line and does not understand the quoting an argument list produces, so it is given the line whole.
        ProcessResult result = isWindows
            ? await ProcessRunner.RunAsync("cmd.exe", [], workingDir, null, limit, maxOutput, ctx.Ct, rawArguments: $"/c {command}")
            : await ProcessRunner.RunAsync("/bin/sh", ["-c", command], workingDir, null, limit, maxOutput, ctx.Ct);
        if (result.StartError is not null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Failed to start process: {result.StartError}" };
        }
        bool killed = result.TimedOut || result.Cancelled;
        return new JObject
        {
            ["success"] = !killed,
            ["command"] = command,
            ["workingDirectory"] = Path.GetRelativePath(dataRoot, workingDir),
            ["exitCode"] = result.ExitCode,
            ["killed"] = killed,
            ["timedOut"] = result.TimedOut,
            ["truncated"] = result.Truncated,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
            ["error"] = killed ? $"Process killed after {timeoutSeconds}s timeout" : null
        };
    }
}
