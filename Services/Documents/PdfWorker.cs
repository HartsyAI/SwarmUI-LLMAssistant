using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Hartsy.Extensions.LLMAssistant.Services.Documents;

/// <summary>The program that reads one PDF in a process of its own, so that a file which overflows the parser's stack or exhausts its memory ends that process and not SwarmUI. SwarmUI
/// loads this assembly as an extension; started with <see cref="Argument"/> and the server's process id it is this program instead. <see cref="PdfWorkerHost"/> starts it and talks to it:
/// <list type="number">
/// <item>It writes <see cref="Ready"/> to standard output.</item>
/// <item>It reads from standard input four 32-bit numbers (most pages, most characters, its time limit in milliseconds, the file's length) and then the file.</item>
/// <item>It writes the <see cref="ExtractedDocument"/> as JSON and ends with exit code 0. Anything else is a failure.</item>
/// </list>
/// <para>It ends itself when the server's process is gone, or when its time limit has passed and nobody has stopped it, so none is left running after a server that was killed. Nothing
/// here may use SwarmUI or a package SwarmUI provides, since none of them is beside the extension's assembly.</para></summary>
internal static class PdfWorker
{
    /// <summary>The argument that makes the assembly a PDF reader.</summary>
    public const string Argument = "--read-pdf";

    /// <summary>The file beside the assembly that tells <c>dotnet</c> how to run it, as the project file names it. The assembly's own name cannot be used to find it: core's build gives
    /// the assembly a name that carries the commit.</summary>
    public const string RuntimeConfigName = "SwarmExtensionLLMAssistant.runtimeconfig.json";

    /// <summary>How long past its time limit the worker waits for the server to stop it before ending itself.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>What the worker writes first, which tells a worker that failed apart from one that never started.</summary>
    public static ReadOnlySpan<byte> Ready => "LLMA-PDF 1\n"u8;

    /// <summary>The words on standard error that say memory ran out. The runtime writes the same when it cannot go on at all.</summary>
    public const string OutOfMemoryNote = "Out of memory.";

    /// <summary>Reads one PDF. Run only as a process of its own.</summary>
    public static int Main(string[] args)
    {
        if (args is not [Argument, string server] || !int.TryParse(server, out int serverId))
        {
            Console.Error.WriteLine("This is the LLM Assistant extension for SwarmUI. SwarmUI loads it; it is not a program to run.");
            return 2;
        }
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        Stream errors = Console.OpenStandardError();
        // Nothing else may write into the reply.
        Console.SetOut(TextWriter.Null);
        // Before the greeting: with a part of the parser missing or broken this is a reader that cannot start, and no file is blamed for it.
        LoadParser();
        output.Write(Ready);
        output.Flush();
        // Made beforehand: there may be no memory left to make it with.
        byte[] note = Encoding.ASCII.GetBytes(OutOfMemoryNote);
        // The parser may catch the failure itself and carry on with less than the file holds, so it is noted where it is thrown.
        bool outOfMemory = false;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => outOfMemory |= e.Exception is OutOfMemoryException;
        try
        {
            BinaryReader request = new(input);
            DocumentLimits limits = new(request.ReadInt32(), request.ReadInt32());
            TimeSpan timeLimit = TimeSpan.FromMilliseconds(request.ReadInt32());
            int length = request.ReadInt32();
            byte[] data = request.ReadBytes(length);
            if (data.Length != length)
            {
                return 1;
            }
            // The server is watched by its process, so nothing is left reading standard input once the request is in.
            EndAfter(() => WaitForServer(serverId));
            EndAfter(() => Thread.Sleep(timeLimit + Grace));
            ExtractedDocument result = new PdfDocumentExtractor().ReadInThisProcess(data, limits, CancellationToken.None);
            if (!outOfMemory)
            {
                output.Write(JsonSerializer.SerializeToUtf8Bytes(result));
                output.Flush();
                return 0;
            }
        }
        catch (EndOfStreamException)
        {
            // The server went before it had sent the request.
            return 1;
        }
        catch (OutOfMemoryException)
        {
        }
        errors.Write(note);
        errors.Flush();
        return 1;
    }

    /// <summary>Loads every assembly of the parser that this one refers to, directly or through another of them, and throws when one is missing or is not an assembly. Left to
    /// itself the runtime loads each only when reading first needs it.</summary>
    private static void LoadParser()
    {
        Queue<Assembly> loaded = new([typeof(PdfWorker).Assembly]);
        HashSet<string> named = [];
        while (loaded.TryDequeue(out Assembly assembly))
        {
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies().Where(r => r.Name.StartsWith("UglyToad.", StringComparison.Ordinal) && named.Add(r.Name)))
            {
                loaded.Enqueue(Assembly.Load(reference));
            }
        }
    }

    /// <summary>Returns once the server's process has ended.</summary>
    private static void WaitForServer(int id)
    {
        try
        {
            using Process server = Process.GetProcessById(id);
            server.WaitForExit();
        }
        catch (ArgumentException)
        {
            // It has ended already.
        }
    }

    /// <summary>Ends this process, whatever it is doing, once <paramref name="wait"/> returns. A wait that fails ends only itself, and leaves the others to end the process.</summary>
    private static void EndAfter(Action wait)
    {
        Thread watcher = new(() =>
        {
            try
            {
                wait();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return;
            }
            Environment.Exit(1);
        })
        {
            IsBackground = true,
        };
        watcher.Start();
    }
}
