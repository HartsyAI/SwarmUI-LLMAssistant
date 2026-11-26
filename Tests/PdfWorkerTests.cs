using System.Diagnostics;
using System.IO;
using System.Text;
using Hartsy.Extensions.LLMAssistant.Services.Documents;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The separate process PDF files are read in: what it reads comes back whole, and a file that crashes it, outlasts it or outgrows it costs that process alone. Every test runs
/// real worker processes.</summary>
public sealed class PdfWorkerTests : IDisposable
{
    /// <summary>The limits most reads here are given.</summary>
    private static readonly DocumentLimits Limits = new(200, 200_000);

    /// <summary>Limits with room for all of a <see cref="Slow"/> file's characters, so reading it is not cut short.</summary>
    internal static readonly DocumentLimits Unhurried = new(200, 100_000_000);

    /// <summary>A folder of this test's own, removed after it.</summary>
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"llma-pdf-worker-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    /// <summary>A PDF that takes the parser seconds to read: pages of 190,000 characters each.</summary>
    internal static byte[] Slow(int pages = 20)
    {
        byte[] page = Encoding.ASCII.GetBytes($"BT /F1 1 Tf 10 10 Td ({new string('a', 190_000)}) Tj ET");
        return DocumentFixtures.RawPdf([.. Enumerable.Repeat(page, pages)], true);
    }

    /// <summary>A host that remembers the process id of each worker it starts.</summary>
    private static (PdfWorkerHost Host, List<int> Ids) Watched()
    {
        List<int> ids = [];
        PdfWorkerHost host = new() { Started = process => ids.Add(process.Id) };
        return (host, ids);
    }

    internal static void AssertEnded(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            Assert.True(process.HasExited, $"worker {id} is still running");
        }
        catch (ArgumentException)
        {
            // No process has that id any more.
        }
    }

    internal static void AssertRefused(ExtractedDocument result, DocumentStatus status, string message)
    {
        Assert.True(result.Status == status, $"{result.Status}: {result.Error}");
        Assert.Equal(message, result.Error);
        Assert.Equal("pdf", result.Kind);
        Assert.Empty(result.Pages);
    }

    private static void AssertReads(PdfWorkerHost host)
    {
        ExtractedDocument result = host.Read(DocumentFixtures.Pdf("still reading"), Limits, CancellationToken.None);
        Assert.True(result.Status == DocumentStatus.Ok, $"{result.Status}: {result.Error}");
        Assert.Contains("still reading", result.Text);
    }

    [Fact]
    public void What_the_worker_reads_is_what_reading_here_gives()
    {
        byte[] accented = [.. Encoding.ASCII.GetBytes("BT /F1 12 Tf 40 780 Td (Caf"), 0xE9, .. Encoding.ASCII.GetBytes(" \"quoted\" <tag> \\\\ done) Tj ET")];
        foreach (byte[] pdf in new[] { DocumentFixtures.Pdf("First page words", "", "Third page words"), DocumentFixtures.RawPdf([accented]), DocumentFixtures.Pdf(""),
            Encoding.ASCII.GetBytes("%PDF-1.4\nnothing here") })
        {
            ExtractedDocument here = new PdfDocumentExtractor().ReadInThisProcess(pdf, Limits, CancellationToken.None);
            ExtractedDocument there = new PdfWorkerHost().Read(pdf, Limits, CancellationToken.None);
            Assert.Equal((here.Status, here.Kind, here.TotalPages, here.Truncated, here.Error), (there.Status, there.Kind, there.TotalPages, there.Truncated, there.Error));
            Assert.Equal(here.Pages, there.Pages);
            Assert.Equal(here.Warnings, there.Warnings);
        }
    }

    [Fact]
    public void The_page_and_character_limits_reach_the_worker()
    {
        ExtractedDocument result = new PdfWorkerHost().Read(DocumentFixtures.Pdf("abcdefgh", "second"), new DocumentLimits(1, 5), CancellationToken.None);
        Assert.Equal(["abcde"], result.Pages);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void A_pdf_that_overflows_the_stack_ends_the_worker_and_nothing_else()
    {
        (PdfWorkerHost host, List<int> ids) = Watched();
        AssertRefused(host.Read(DocumentFixtures.StackOverflowAtOpen(), Limits, CancellationToken.None), DocumentStatus.Malformed, PdfWorkerHost.CrashedMessage);
        AssertEnded(Assert.Single(ids));
        AssertReads(host);
    }

    [Fact]
    public async Task The_way_attachments_and_the_library_read_a_file_goes_through_the_worker()
    {
        // Were this read inside this process, the test run would end here.
        ExtractedDocument result = await DocumentExtraction.ExtractAsync("crash.pdf", DocumentFixtures.StackOverflowAtOpen(), Limits, CancellationToken.None, "user");
        AssertRefused(result, DocumentStatus.Malformed, PdfWorkerHost.CrashedMessage);
        AssertRefused(DocumentExtraction.Extract("renamed.txt", DocumentFixtures.StackOverflowAtOpen(), Limits), DocumentStatus.Malformed, PdfWorkerHost.CrashedMessage);
        ExtractedDocument next = await DocumentExtraction.ExtractAsync("fine.pdf", DocumentFixtures.Pdf("next file"), Limits, CancellationToken.None, "user");
        Assert.Contains("next file", next.Text);
    }

    [Fact]
    public void A_worker_over_its_time_limit_is_stopped()
    {
        (PdfWorkerHost host, List<int> ids) = Watched();
        host.TimeLimit = TimeSpan.FromMilliseconds(500);
        Stopwatch clock = Stopwatch.StartNew();
        AssertRefused(host.Read(Slow(), Unhurried, CancellationToken.None), DocumentStatus.TooLarge, PdfWorkerHost.TimeMessage);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"{clock.Elapsed}");
        AssertEnded(Assert.Single(ids));
    }

    [Fact]
    public void A_worker_whose_heap_cannot_hold_the_file_says_it_needs_too_much_memory()
    {
        (PdfWorkerHost host, List<int> ids) = Watched();
        host.HeapLimit = 48 * 1024 * 1024;
        byte[] large = [.. DocumentFixtures.Pdf("large"), .. new byte[64 * 1024 * 1024]];
        AssertRefused(host.Read(large, Limits, CancellationToken.None), DocumentStatus.TooLarge, PdfWorkerHost.MemoryMessage);
        AssertEnded(Assert.Single(ids));
    }

    [Fact]
    public void A_worker_whose_parser_runs_out_of_heap_says_it_needs_too_much_memory()
    {
        (PdfWorkerHost host, List<int> ids) = Watched();
        host.HeapLimit = 48 * 1024 * 1024;
        // The file is small; the characters the parser builds from it are what does not fit.
        AssertRefused(host.Read(Slow(1), Unhurried, CancellationToken.None), DocumentStatus.TooLarge, PdfWorkerHost.MemoryMessage);
        AssertEnded(Assert.Single(ids));
    }

    [Fact]
    public void A_worker_over_its_memory_limit_in_all_is_stopped()
    {
        (PdfWorkerHost host, List<int> ids) = Watched();
        host.MemoryLimit = 1024 * 1024;
        AssertRefused(host.Read(Slow(), Unhurried, CancellationToken.None), DocumentStatus.TooLarge, PdfWorkerHost.MemoryMessage);
        AssertEnded(Assert.Single(ids));
    }

    [Fact]
    public void The_largest_page_that_is_accepted_fits_the_limits_the_server_uses()
    {
        StringBuilder content = new("BT /F1 1 Tf 10 10 Td ");
        content.Append('(').Append('a', 190_000).Append(") Tj ET ");
        while (content.Length < 7_900_000)
        {
            content.Append("q Q ");
        }
        byte[] pdf = [.. DocumentFixtures.RawPdf([Encoding.ASCII.GetBytes(content.ToString())], true)];
        ExtractedDocument result = new PdfWorkerHost().Read(pdf, Limits, CancellationToken.None);
        Assert.True(result.Status == DocumentStatus.Ok, $"{result.Status}: {result.Error}");
    }

    [Theory]
    [InlineData("no dotnet")]
    [InlineData("no assembly")]
    [InlineData("no runtime settings")]
    [InlineData("not an assembly")]
    [InlineData("a program that is not the worker")]
    public void A_pdf_is_refused_when_no_worker_can_start(string how)
    {
        PdfWorkerHost host = new();
        Directory.CreateDirectory(_folder);
        switch (how)
        {
            case "no dotnet":
                host.Dotnet = Path.Combine(_folder, "nothing-here");
                break;
            case "no assembly":
                host.WorkerAssembly = Path.Combine(_folder, "missing.dll");
                break;
            case "no runtime settings":
                host.WorkerAssembly = Path.Combine(_folder, "alone.dll");
                File.Copy(typeof(PdfWorkerHost).Assembly.Location, host.WorkerAssembly);
                break;
            case "not an assembly":
                host.WorkerAssembly = Copy("broken.dll");
                File.WriteAllText(host.WorkerAssembly, "not a program");
                break;
            default:
                // It runs, and says something, but not the worker's greeting.
                host.Dotnet = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "whoami.exe") : "/bin/echo";
                break;
        }
        // A good file: were there a way to read it here instead, it would be read.
        AssertRefused(host.Read(DocumentFixtures.Pdf("good file"), Limits, CancellationToken.None), DocumentStatus.Unsupported, PdfWorkerHost.CannotStartMessage);
    }

    /// <summary>What a worker needs from the folder core builds an extension into: the assembly under another file name, the runtime settings under their fixed name, and the parser.
    /// Neither SwarmUI nor a package SwarmUI provides is there. Core's build also changes the name inside the assembly, which only a SwarmUI build shows (the documents browser spec).
    /// <paramref name="within"/> names a folder of its own to put it in, and <paramref name="without"/> a file of the parser to leave out.</summary>
    private string Copy(string name, string within = "", string without = null)
    {
        string folder = Path.Combine(_folder, within);
        Directory.CreateDirectory(folder);
        string built = Path.GetDirectoryName(typeof(PdfWorkerHost).Assembly.Location);
        File.Copy(Path.Combine(built, PdfWorker.RuntimeConfigName), Path.Combine(folder, PdfWorker.RuntimeConfigName), true);
        Assert.True(without is null || File.Exists(Path.Combine(built, without)), $"{without} is not part of the build");
        foreach (string parser in Directory.EnumerateFiles(built, "UglyToad.*.dll").Where(p => Path.GetFileName(p) != without))
        {
            File.Copy(parser, Path.Combine(folder, Path.GetFileName(parser)), true);
        }
        string assembly = Path.Combine(folder, name);
        File.Copy(typeof(PdfWorkerHost).Assembly.Location, assembly, true);
        return assembly;
    }

    [Theory]
    [InlineData("UglyToad.PdfPig.dll")]
    [InlineData("UglyToad.PdfPig.Core.dll")]
    [InlineData("UglyToad.PdfPig.DocumentLayoutAnalysis.dll")]
    [InlineData("UglyToad.PdfPig.Fonts.dll")]
    [InlineData("UglyToad.PdfPig.Tokenization.dll")]
    [InlineData("UglyToad.PdfPig.Tokens.dll")]
    public void A_build_missing_part_of_the_parser_is_a_reader_that_cannot_start(string missing)
    {
        PdfWorkerHost host = new() { WorkerAssembly = Copy("SwarmExtensionLLMAssistant-partial.dll", without: missing) };
        AssertRefused(host.Read(DocumentFixtures.Pdf("good file"), Limits, CancellationToken.None), DocumentStatus.Unsupported, PdfWorkerHost.CannotStartMessage);
    }

    [Fact]
    public void The_worker_starts_from_a_path_with_spaces_and_other_alphabets()
        => AssertReads(new PdfWorkerHost { WorkerAssembly = Copy("SwarmExtension with spaces Über 試験-0123abcd.dll", within: "a folder, with spaces é 試験") });

    [Theory]
    [InlineData("KILL", DocumentStatus.TooLarge, PdfWorkerHost.KilledMessage)]
    [InlineData("INT", DocumentStatus.Unsupported, PdfWorkerHost.InterruptedMessage)]
    [InlineData("TERM", DocumentStatus.Unsupported, PdfWorkerHost.InterruptedMessage)]
    public void A_worker_stopped_from_outside_is_not_blamed_on_the_file(string signal, DocumentStatus status, string message)
    {
        // Signals are how Linux and macOS end a process; Windows has none to send.
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        PdfWorkerHost host = new()
        {
            Started = process =>
            {
                Thread.Sleep(1000);
                using Process kill = Process.Start("kill", [$"-{signal}", process.Id.ToString()]);
                kill.WaitForExit();
            },
        };
        AssertRefused(host.Read(Slow(), Unhurried, CancellationToken.None), status, message);
    }

    [Theory]
    [InlineData(0L, PdfWorkerHost.DefaultHeapLimit)]
    [InlineData(64L * 1024 * 1024 * 1024, PdfWorkerHost.DefaultHeapLimit)]
    [InlineData(135L * 1024 * 1024, 135L * 1024 * 1024)]
    public void The_heap_limit_is_no_more_than_the_memory_there_is(long available, long limit)
        => Assert.Equal(limit, PdfWorkerHost.HeapLimitFor(available));

    [Theory]
    [InlineData("\"Status\":0", "\"Status\":99")]
    [InlineData("\"Kind\":\"pdf\"", "\"Kind\":null")]
    [InlineData("\"Kind\":\"pdf\"", "\"Kind\":\"docx\"")]
    [InlineData("[\"abc\",\"defg\"]", "null")]
    [InlineData("\"abc\"", "null")]
    [InlineData("\"defg\"", "\"defghijk\"")]
    [InlineData("[\"abc\",\"defg\"]", "[\"\",\"\",\"\"]")]
    [InlineData("\"TotalPages\":2", "\"TotalPages\":-1")]
    [InlineData("[\"w\"]", "null")]
    [InlineData("[\"w\"]", "[null]")]
    [InlineData("\"Error\":\"\"", "\"Error\":null")]
    [InlineData("\"Kind\":\"pdf\"", "\"Kind\":pdf")]
    public void A_reply_that_could_not_be_a_pdfs_result_is_not_taken(string was, string now)
    {
        const string reply = """{"Status":0,"Kind":"pdf","Pages":["abc","defg"],"TotalPages":2,"Truncated":false,"Warnings":["w"],"Error":""}""";
        DocumentLimits limits = new(2, 10);
        Assert.NotNull(PdfWorkerHost.Parse(Encoding.UTF8.GetBytes(reply), limits));
        Assert.Contains(was, reply);
        Assert.Null(PdfWorkerHost.Parse(Encoding.UTF8.GetBytes(reply.Replace(was, now)), limits));
    }

    [Fact]
    public void The_worker_starts_from_a_folder_laid_out_as_core_builds_it()
    {
        PdfWorkerHost host = new() { WorkerAssembly = Copy("SwarmExtensionSwarmUI-LLMAssistant-0123abcd.dll") };
        AssertReads(host);
        AssertRefused(host.Read(DocumentFixtures.StackOverflowAtOpen(), Limits, CancellationToken.None), DocumentStatus.Malformed, PdfWorkerHost.CrashedMessage);
    }

    [Fact]
    public void Cancelling_ends_the_worker_before_the_call_returns()
    {
        using CancellationTokenSource cancel = new();
        int id = 0;
        PdfWorkerHost host = new()
        {
            Started = process =>
            {
                id = process.Id;
                cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
            },
        };
        Assert.ThrowsAny<OperationCanceledException>(() => host.Read(Slow(), Unhurried, cancel.Token));
        Assert.NotEqual(0, id);
        AssertEnded(id);
    }

    [Fact]
    public async Task Shutting_down_ends_running_workers_and_starts_no_more()
    {
        TaskCompletionSource<int> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        PdfWorkerHost host = new()
        {
            Started = process =>
            {
                starts++;
                started.TrySetResult(process.Id);
            },
        };
        Task<ExtractedDocument> reading = Task.Run(() => host.Read(Slow(), Unhurried, CancellationToken.None));
        int id = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        host.Shutdown();
        // Gone when Shutdown returns, not some time after.
        AssertEnded(id);
        AssertRefused(await reading.WaitAsync(TimeSpan.FromSeconds(30)), DocumentStatus.Unsupported, PdfWorkerHost.ShutdownMessage);
        AssertRefused(host.Read(DocumentFixtures.Pdf("too late"), Limits, CancellationToken.None), DocumentStatus.Unsupported, PdfWorkerHost.ShutdownMessage);
        Assert.Equal(1, starts);
    }

    /// <summary>Starts a worker as a host does and sends it a request, with nobody then watching over it.</summary>
    private static Process StartByHand(PdfWorkerHost host, byte[] pdf, int timeLimitMs, out Task<string> errors)
    {
        Process process = Process.Start(host.StartInfo());
        errors = process.StandardError.ReadToEndAsync();
        using BinaryWriter request = new(process.StandardInput.BaseStream);
        request.Write(200);
        request.Write(100_000_000);
        request.Write(timeLimitMs);
        request.Write(pdf.Length);
        request.Write(pdf);
        return process;
    }

    /// <summary>Waits for a worker that has to end itself, and checks it did so the way it means to: exit code 1, nothing on standard error (so it did not crash) and no result.</summary>
    private static async Task AssertEndsItself(Process process, Task<string> reply, Task<string> errors, int withinSeconds)
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(withinSeconds));
        Assert.Equal("", await errors);
        Assert.Equal(1, process.ExitCode);
        Assert.DoesNotContain("\"Status\"", await reply);
    }

    [Fact]
    public async Task A_worker_whose_server_is_killed_ends_by_itself()
    {
        // Standing in for the server: another worker, which waits for a request that never comes.
        using Process server = Process.Start(new PdfWorkerHost().StartInfo());
        using Process process = StartByHand(new PdfWorkerHost { ServerId = server.Id }, Slow(60), 600_000, out Task<string> errors);
        try
        {
            Task<string> reply = process.StandardOutput.ReadToEndAsync();
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(process.HasExited, "the worker ended while its server was running");
            server.Kill();
            await AssertEndsItself(process, reply, errors, 30);
        }
        finally
        {
            server.Kill();
            process.Kill();
        }
    }

    [Fact]
    public async Task A_worker_whose_server_is_already_gone_ends_at_once()
    {
        // No process has this id.
        using Process process = StartByHand(new PdfWorkerHost { ServerId = int.MaxValue - 1 }, Slow(60), 600_000, out Task<string> errors);
        try
        {
            await AssertEndsItself(process, process.StandardOutput.ReadToEndAsync(), errors, 30);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task A_worker_nobody_stops_ends_itself_after_its_time_limit()
    {
        using Process process = StartByHand(new PdfWorkerHost(), Slow(60), 100, out Task<string> errors);
        try
        {
            await AssertEndsItself(process, process.StandardOutput.ReadToEndAsync(), errors, 30);
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task A_worker_sent_only_part_of_a_request_ends_when_the_pipe_closes()
    {
        using Process process = Process.Start(new PdfWorkerHost().StartInfo());
        try
        {
            Task<string> reply = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            process.StandardInput.BaseStream.Write([1, 0, 0, 0, 1, 0]);
            process.StandardInput.Close();
            await AssertEndsItself(process, reply, errors, 30);
        }
        finally
        {
            process.Kill();
        }
    }
}

/// <summary>Tests that change the host every request uses, so they run beside no other test.</summary>
[CollectionDefinition(nameof(SharedPdfWorkerHostTests), DisableParallelization = true)]
[Collection(nameof(SharedPdfWorkerHostTests))]
public sealed class SharedPdfWorkerHostTests
{
    [Fact]
    public async Task A_request_over_the_time_limit_is_answered_by_the_host_once_its_worker_has_ended()
    {
        PdfWorkerHost host = PdfWorkerHost.Shared;
        // A request's limit and the host's are the same one; the request waits longer than that for the answer.
        Assert.Equal(DocumentExtraction.Timeout, host.TimeLimit);
        TimeSpan limit = TimeSpan.FromSeconds(1);
        int id = 0;
        try
        {
            host.TimeLimit = limit;
            host.Started = process => id = process.Id;
            ExtractedDocument result = await DocumentExtraction.ExtractAsync("slow.pdf", PdfWorkerTests.Slow(), PdfWorkerTests.Unhurried, CancellationToken.None, "user", limit);
            // The host's answer, which names the kind, and not the request giving up on it; and by then the worker is gone.
            PdfWorkerTests.AssertRefused(result, DocumentStatus.TooLarge, PdfWorkerHost.TimeMessage);
            Assert.NotEqual(0, id);
            PdfWorkerTests.AssertEnded(id);
        }
        finally
        {
            host.TimeLimit = DocumentExtraction.Timeout;
            host.Started = null;
        }
    }
}
