using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: execute a shell command and return stdout/stderr/exit code.
/// DANGEROUS — this gives the LLM full access to the host shell. Disabled by default in the
/// tool definition (see <see cref="Services.ToolRegistryService.BuildDefaultTools"/>); users must
/// explicitly opt in by enabling it globally and per-assistant.</summary>
public class ShellExecTool : ToolHandler
{
    public override string HandlerId => ToolConstants.ShellExec;

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        CancellationToken ct = ctx.Ct;
        string command = args["command"]?.ToString();
        if (string.IsNullOrWhiteSpace(command))
        {
            return new JObject { ["success"] = false, ["error"] = "command is required" };
        }

        int timeoutSeconds = args["timeoutSeconds"]?.Value<int?>() ?? 30;
        if (timeoutSeconds <= 0) timeoutSeconds = 30;
        if (timeoutSeconds > 300) timeoutSeconds = 300;

        int maxOutputBytes = args["maxOutputBytes"]?.Value<int?>() ?? 65536;
        if (maxOutputBytes <= 0) maxOutputBytes = 65536;
        if (maxOutputBytes > 1024 * 1024) maxOutputBytes = 1024 * 1024;

        // Working directory: defaults to SwarmUI Data; any supplied path is resolved relative to Data
        // and must stay inside it (sandbox).
        string dataRoot = Path.GetFullPath("Data");
        string workingDir = dataRoot;
        string requestedCwd = args["workingDirectory"]?.ToString();
        if (!string.IsNullOrWhiteSpace(requestedCwd))
        {
            string resolved = Path.GetFullPath(Path.Combine(dataRoot, requestedCwd));
            if (!resolved.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !resolved.Equals(dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "workingDirectory is outside the SwarmUI Data directory (sandbox violation)."
                };
            }
            if (!Directory.Exists(resolved))
            {
                return new JObject { ["success"] = false, ["error"] = $"workingDirectory does not exist: {requestedCwd}" };
            }
            workingDir = resolved;
        }

        // Shell selection: cmd.exe on Windows, /bin/sh elsewhere.
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        ProcessStartInfo psi = new()
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? $"/c {command}" : "-c \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Logs.Info($"[LLMAssistant] ShellExecTool running: {command} (cwd={workingDir}, timeout={timeoutSeconds}s)");

        Process proc;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                return new JObject { ["success"] = false, ["error"] = "Failed to start process" };
            }
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = $"Failed to start process: {ex.Message}" };
        }

        StringBuilder stdout = new();
        StringBuilder stderr = new();
        int totalBytes = 0;
        bool outputTruncated = false;

        void AppendLine(StringBuilder sb, string line)
        {
            if (line is null) return;
            // +1 for newline
            int lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (totalBytes + lineBytes > maxOutputBytes)
            {
                int remaining = maxOutputBytes - totalBytes;
                if (remaining > 0 && line.Length > 0)
                {
                    int take = Math.Min(remaining, line.Length);
                    sb.Append(line.AsSpan(0, take));
                    totalBytes += take;
                }
                outputTruncated = true;
                return;
            }
            sb.Append(line).Append('\n');
            totalBytes += lineBytes;
        }

        proc.OutputDataReceived += (_, e) => AppendLine(stdout, e.Data);
        proc.ErrorDataReceived += (_, e) => AppendLine(stderr, e.Data);

        try
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = $"Failed to attach output streams: {ex.Message}" };
        }

        bool killed = false;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                killed = true;
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }

        int exitCode = -1;
        try { exitCode = proc.ExitCode; } catch { }
        try { proc.Dispose(); } catch { }

        return new JObject
        {
            ["success"] = !killed,
            ["command"] = command,
            ["workingDirectory"] = Path.GetRelativePath(dataRoot, workingDir),
            ["exitCode"] = exitCode,
            ["killed"] = killed,
            ["timedOut"] = killed && !ct.IsCancellationRequested,
            ["truncated"] = outputTruncated,
            ["stdout"] = stdout.ToString(),
            ["stderr"] = stderr.ToString(),
            ["error"] = killed ? $"Process killed after {timeoutSeconds}s timeout" : null
        };
    }
}
