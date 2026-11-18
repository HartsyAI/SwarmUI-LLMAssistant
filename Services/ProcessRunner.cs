using System.Diagnostics;
using System.IO;
using System.Text;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>What a finished process left behind.</summary>
/// <param name="ExitCode">The exit code, or -1 when the process could not be started, timed out or was cancelled.</param>
/// <param name="Stdout">What it wrote to standard output, cut at the size limit.</param>
/// <param name="Stderr">What it wrote to standard error, cut at the size limit.</param>
/// <param name="TimedOut">The time limit ran out and the process was killed.</param>
/// <param name="Cancelled">The caller cancelled and the process was killed.</param>
/// <param name="Truncated">Output was cut at the size limit.</param>
/// <param name="StartError">Why it could not be started, or null.</param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Cancelled, bool Truncated, string StartError = null);

/// <summary>Runs one process to completion with what a tool or hook needs and the framework's plain helpers do not give: arguments passed as a
/// list (never joined into a shell string), text on standard input, a time limit, a cap on the output kept, and the whole process tree
/// killed when the time runs out or the caller cancels.</summary>
public static class ProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> and returns what it did. Never throws for a process that
    /// fails to start or runs badly; that is described in the result.</summary>
    /// <param name="fileName">The program to run (looked up on the PATH when it has no folder).</param>
    /// <param name="arguments">One entry per argument; no quoting is needed or applied.</param>
    /// <param name="workingDirectory">The folder to run in, or null for the server's own.</param>
    /// <param name="stdin">Text to write to the process's standard input, which is then closed; null writes nothing.</param>
    /// <param name="timeout">How long it may run before it is killed.</param>
    /// <param name="maxOutputChars">The most of each of standard output and standard error to keep.</param>
    /// <param name="ct">Cancelling kills the process and its children.</param>
    /// <param name="rawArguments">The whole command line as one string, used instead of <paramref name="arguments"/> when set. Only for a program that does its own parsing
    /// (<c>cmd.exe</c>, which does not understand the quoting that an argument list produces).</param>
    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string stdin, TimeSpan timeout, int maxOutputChars, CancellationToken ct,
        string rawArguments = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (rawArguments is not null)
        {
            startInfo.Arguments = rawArguments;
        }
        else
        {
            foreach (string argument in arguments ?? [])
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        using Process process = new() { StartInfo = startInfo };
        StringBuilder stdout = new();
        StringBuilder stderr = new();
        bool truncated = false;
        object gate = new();
        void Collect(StringBuilder target, string line)
        {
            if (line is null)
            {
                return;
            }
            lock (gate)
            {
                if (target.Length >= maxOutputChars)
                {
                    truncated = true;
                    return;
                }
                target.Append(line).Append('\n');
                if (target.Length > maxOutputChars)
                {
                    target.Length = maxOutputChars;
                    truncated = true;
                }
            }
        }
        process.OutputDataReceived += (_, e) => Collect(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Collect(stderr, e.Data);
        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, "", "", false, false, false, "The process did not start.");
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", "", false, false, false, ex.Message);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Written beside the wait below, not before it: a program that never reads its input leaves a large write blocked on a full pipe, and
        // that must not outlast the time limit or a Stop. Killing the process breaks the pipe, which ends the write.
        Task writeInput = Task.Run(async () =>
        {
            try
            {
                if (stdin is not null)
                {
                    await process.StandardInput.WriteAsync(stdin);
                }
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process ended or closed its input before reading it all; its exit code says what happened.
            }
            catch (ObjectDisposedException)
            {
                // The process was torn down while the write was still going.
            }
        }, CancellationToken.None);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        bool timedOut = false;
        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = ct.IsCancellationRequested;
            timedOut = !cancelled;
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        // Standard output and error are read on their own threads; a final wait with no token lets them finish.
        process.WaitForExit();
        await writeInput;
        lock (gate)
        {
            return new ProcessResult(timedOut || cancelled ? -1 : process.ExitCode, stdout.ToString().TrimEnd('\n'), stderr.ToString().TrimEnd('\n'), timedOut, cancelled, truncated);
        }
    }

    /// <summary>Kills a process and everything it started, ignoring one that already ended.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Could not be killed from here; the wait that follows will report whatever state it is in.
        }
    }
}
