using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>The server's side of reading a PDF: each file is read by a <see cref="PdfWorker"/> process of its own, held to a time and a memory limit, so a file that crashes or exhausts the
/// parser ends that process and not the server. A file is never read inside the server: when no worker can be started, it is refused. How many workers run at once is bounded by the
/// <see cref="DocumentGate"/> every request goes through (<see cref="DocumentExtraction.ExtractAsync"/>).</summary>
internal sealed class PdfWorkerHost
{
    /// <summary>The one the server uses.</summary>
    public static readonly PdfWorkerHost Shared = new();

    /// <summary>The most a worker's managed heap may hold (the file's bytes included); the runtime in the worker enforces it.</summary>
    public const long DefaultHeapLimit = 1024L * 1024 * 1024;

    /// <summary>The most memory a worker process may occupy in all (its working set); one over it is stopped. This is the bound on what the heap limit does not count.</summary>
    public const long DefaultMemoryLimit = 1536L * 1024 * 1024;

    /// <summary>What a user is told when no worker can be started.</summary>
    public const string CannotStartMessage = "PDF files cannot be read on this server, because the separate process that reads them could not be started. The server log says why.";

    /// <summary>What a user is told when the worker ended without a result.</summary>
    public const string CrashedMessage = "That PDF could not be read: reading it crashed the PDF reader, which a damaged or hostile file can do. The server was not affected.";

    /// <summary>What a user is told when reading ran out of time.</summary>
    public const string TimeMessage = "Reading that file took too long. Split it into smaller parts.";

    /// <summary>What a user is told when reading ran out of memory.</summary>
    public const string MemoryMessage = "Reading that PDF needs more memory than is allowed. Split it into smaller parts.";

    /// <summary>What a user is told when the server stops while their file is being read.</summary>
    public const string ShutdownMessage = "The server is shutting down, so that PDF was not read.";

    /// <summary>What a user is told when the system killed the worker (signal 9, which is what Linux does to a process when memory runs out).</summary>
    public const string KilledMessage = "Reading that PDF was stopped by the system, which usually means the server was short of memory. Try again, or split it into smaller parts.";

    /// <summary>What a user is told when the worker was asked to stop from outside (signal 2 or 15: Ctrl+C in the server's terminal reaches the worker too).</summary>
    public const string InterruptedMessage = "Reading that PDF was interrupted before it finished. Try again.";

    /// <summary>How often a running worker's time, memory and cancellation are looked at, in milliseconds.</summary>
    private const int PollMs = 50;

    /// <summary>The most characters of a worker's error output kept for the log; a stack overflow writes megabytes.</summary>
    private const int MaxErrorChars = 2000;

    /// <summary>What the runtime writes first when a stack overflows.</summary>
    private const string StackOverflowNote = "Stack overflow.";

    /// <summary>The dotnet program that runs the worker.</summary>
    public string Dotnet = FindDotnet();

    /// <summary>The assembly the worker is started from: this extension's own.</summary>
    public string WorkerAssembly = typeof(PdfWorker).Assembly.Location;

    /// <summary>How long one worker may run.</summary>
    public TimeSpan TimeLimit = DocumentExtraction.Timeout;

    /// <summary>The limit on a worker's managed heap, in bytes.</summary>
    public long HeapLimit = HeapLimitFor(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>The limit on a worker's working set, in bytes.</summary>
    public long MemoryLimit = DefaultMemoryLimit;

    /// <summary>The process whose end ends the workers: the server's own.</summary>
    public int ServerId = Environment.ProcessId;

    /// <summary>Called with each worker once it has started, so tests can watch it.</summary>
    public Action<Process> Started;

    /// <summary>The workers running now; also the lock for this and <see cref="Stopping"/>.</summary>
    private readonly HashSet<Process> Running = [];

    /// <summary>Whether <see cref="Shutdown"/> was called, after which no worker is started.</summary>
    private volatile bool Stopping;

    /// <summary>The dotnet of the runtime the server runs on (its folder is <c>shared/Microsoft.NETCore.App/version</c> under it), else the one on the PATH, which core builds extensions with.</summary>
    private static string FindDotnet()
    {
        string root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        string dotnet = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(dotnet) ? dotnet : "dotnet";
    }

    /// <summary>The heap limit where the runtime reports <paramref name="available"/> bytes: <see cref="DefaultHeapLimit"/>, or less where less is available. In a container the
    /// runtime reports its own share of the container's memory, and a fixed figure above that would replace it, leaving the system to kill the worker.</summary>
    public static long HeapLimitFor(long available) => available > 0 ? Math.Min(DefaultHeapLimit, available) : DefaultHeapLimit;

    /// <summary>The runtime settings the build writes beside the assembly.</summary>
    private string RuntimeConfig => Path.Combine(Path.GetDirectoryName(WorkerAssembly) ?? "", PdfWorker.RuntimeConfigName);

    /// <summary>How a worker is started. No deps file is named, because the build's lists the assembly by another name than core gives it; the runtime then loads from the assembly's folder.</summary>
    public ProcessStartInfo StartInfo()
    {
        ProcessStartInfo info = new()
        {
            FileName = Dotnet,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "exec", "--runtimeconfig", RuntimeConfig, WorkerAssembly, PdfWorker.Argument, ServerId.ToString() })
        {
            info.ArgumentList.Add(argument);
        }
        // Read by the worker's runtime as it starts. The number is hexadecimal; the server collector would reserve a heap for every core.
        info.Environment["DOTNET_GCHeapHardLimit"] = $"0x{HeapLimit:X}";
        info.Environment["DOTNET_gcServer"] = "0";
        info.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        return info;
    }

    /// <summary>Reads a PDF in a worker. Every failure is a result with words for the user; only cancelling throws. The worker has ended by the time this returns or throws.</summary>
    public ExtractedDocument Read(byte[] data, DocumentLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(WorkerAssembly) || !File.Exists(RuntimeConfig))
        {
            return CannotStart($"'{WorkerAssembly}' or '{RuntimeConfig}' is missing");
        }
        using Process process = new() { StartInfo = StartInfo() };
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            lock (Running)
            {
                if (Stopping)
                {
                    return Failed(DocumentStatus.Unsupported, ShutdownMessage);
                }
                process.Start();
                Running.Add(process);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return CannotStart($"'{Dotnet}' did not start: {ex.Message}");
        }
        MemoryStream reply = new();
        StringBuilder errors = new();
        List<Task> pipes = [];
        string overLimit = null;
        try
        {
            // JSON writes a character as six bytes at most; whatever comes past that is not a result.
            long mostReply = 64 * 1024 + limits.MaxChars * 6L + limits.MaxPages * 8L;
            pipes.Add(OnItsOwnThread(() => Receive(process.StandardOutput.BaseStream, reply, mostReply)));
            pipes.Add(OnItsOwnThread(() => Drain(process.StandardError, errors)));
            pipes.Add(OnItsOwnThread(() => Send(process.StandardInput.BaseStream, data, limits)));
            Started?.Invoke(process);
            while (!process.WaitForExit(PollMs))
            {
                ct.ThrowIfCancellationRequested();
                // The runtime writes this and then every frame of the stack, which can take minutes; how this ends is settled by the first line.
                if (Said(errors).Contains(StackOverflowNote))
                {
                    break;
                }
                if (clock.Elapsed >= TimeLimit)
                {
                    overLimit = TimeMessage;
                    break;
                }
                if (WorkingSet(process) > MemoryLimit)
                {
                    overLimit = MemoryMessage;
                    break;
                }
            }
        }
        finally
        {
            ProcessRunner.KillTree(process);
            process.WaitForExit(TimeSpan.FromSeconds(10));
            Task.WaitAll([.. pipes], TimeSpan.FromSeconds(10));
            lock (Running)
            {
                Running.Remove(process);
            }
        }
        if (overLimit is not null)
        {
            Logs.Warning($"[LLMAssistant] Stopped reading a PDF: {overLimit}");
            return Failed(DocumentStatus.TooLarge, overLimit);
        }
        ReadOnlySpan<byte> received = reply.ToArray();
        bool ready = received.StartsWith(PdfWorker.Ready);
        if (ready && process.HasExited && process.ExitCode == 0 && Parse(received[PdfWorker.Ready.Length..], limits) is ExtractedDocument result)
        {
            return result;
        }
        if (Stopping)
        {
            return Failed(DocumentStatus.Unsupported, ShutdownMessage);
        }
        string said = Said(errors);
        string how = $"exit code {(process.HasExited ? process.ExitCode : "none")}: {said}";
        // On Linux and macOS a process ended by a signal reports 128 and the signal's number. Unless this host sent it (after a stack overflow, above all), it says nothing about the file.
        if (!OperatingSystem.IsWindows() && !said.Contains(StackOverflowNote) && process.HasExited && process.ExitCode is 128 + 2 or 128 + 9 or 128 + 15)
        {
            Logs.Warning($"[LLMAssistant] The PDF reader process was stopped from outside, {how}");
            return process.ExitCode == 128 + 9 ? Failed(DocumentStatus.TooLarge, KilledMessage) : Failed(DocumentStatus.Unsupported, InterruptedMessage);
        }
        if (!ready)
        {
            return CannotStart(how);
        }
        if (said.Contains(PdfWorker.OutOfMemoryNote.TrimEnd('.')) || said.Contains(nameof(OutOfMemoryException)))
        {
            Logs.Warning("[LLMAssistant] Reading a PDF ran out of memory.");
            return Failed(DocumentStatus.TooLarge, MemoryMessage);
        }
        Logs.Warning($"[LLMAssistant] The PDF reader process failed, {how}");
        return Failed(DocumentStatus.Malformed, CrashedMessage);
    }

    /// <summary>Stops every worker and starts no more: the server is shutting down.</summary>
    public void Shutdown()
    {
        lock (Running)
        {
            Stopping = true;
            foreach (Process process in Running)
            {
                ProcessRunner.KillTree(process);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
    }

    /// <summary>A failed result for a PDF.</summary>
    private static ExtractedDocument Failed(DocumentStatus status, string message) => ExtractedDocument.Failed(status, "pdf", message);

    /// <summary>Refuses a PDF because no worker could read it, and logs why.</summary>
    private static ExtractedDocument CannotStart(string why)
    {
        Logs.Error($"[LLMAssistant] The PDF reader process could not be started, so PDF files are refused: {why}");
        return Failed(DocumentStatus.Unsupported, CannotStartMessage);
    }

    /// <summary>The result a worker wrote, or null when it is not one: it has to be what reading a PDF can give, within the limits the worker was asked to keep.</summary>
    public static ExtractedDocument Parse(ReadOnlySpan<byte> json, DocumentLimits limits)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtractedDocument>(json) is { Kind: "pdf", Pages: not null, TotalPages: >= 0, Warnings: not null, Error: not null } result
                && Enum.IsDefined(result.Status)
                && result.Pages.Count <= limits.MaxPages && result.Pages.All(p => p is not null) && result.Pages.Sum(p => (long)p.Length) <= limits.MaxChars
                && result.Warnings.All(w => w is not null)
                ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A process's working set in bytes, or 0 when it cannot be told (it has just ended).</summary>
    private static long WorkingSet(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            return 0;
        }
    }

    /// <summary>Runs work that blocks on a worker's pipe on a thread of its own, so a busy thread pool never holds a worker up. A pipe that breaks ends the work: how the worker ended
    /// says what happened.</summary>
    private static Task OnItsOwnThread(Action work) => Task.Factory.StartNew(() =>
    {
        try
        {
            work();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>What has been kept of a worker's error output.</summary>
    private static string Said(StringBuilder errors)
    {
        lock (errors)
        {
            return errors.ToString();
        }
    }

    /// <summary>Writes the request (see <see cref="PdfWorker"/>) and closes the pipe.</summary>
    private void Send(Stream input, byte[] data, DocumentLimits limits)
    {
        using BinaryWriter request = new(input);
        request.Write(limits.MaxPages);
        request.Write(limits.MaxChars);
        request.Write((int)Math.Min(TimeLimit.TotalMilliseconds, int.MaxValue / 2));
        request.Write(data.Length);
        request.Write(data);
    }

    /// <summary>Reads the worker's reply to its end, keeping no more than <paramref name="most"/> bytes.</summary>
    private static void Receive(Stream output, MemoryStream reply, long most)
    {
        byte[] block = new byte[64 * 1024];
        int read;
        while ((read = output.Read(block)) > 0)
        {
            reply.Write(block, 0, (int)Math.Clamp(most - reply.Length, 0, read));
        }
    }

    /// <summary>Reads the worker's error output to its end, so it never waits on a full pipe, and keeps its start.</summary>
    private static void Drain(StreamReader error, StringBuilder kept)
    {
        char[] block = new char[4096];
        int read;
        while ((read = error.Read(block, 0, block.Length)) > 0)
        {
            lock (kept)
            {
                kept.Append(block, 0, Math.Min(read, MaxErrorChars - kept.Length));
            }
        }
    }
}
