using System.Runtime.InteropServices;
using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Live tool output: the process runner reports each line as it is written, and the pump carries lines to the chat in order, in few frames, before the result.</summary>
public sealed class ToolOutputTests
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>A sink that keeps the output frames it is given, and can be slowed down or made to fail.</summary>
    private sealed class RecordingSink : IToolTurnSink
    {
        public readonly List<JObject> Frames = [];
        public TimeSpan Delay = TimeSpan.Zero;
        public bool Throw;
        public bool Open = true;
        public bool IsOpen => Open;
        public bool StreamsToolOutput => true;
        public Task Chunk(string text) => Task.CompletedTask;
        public Task Iteration(int round) => Task.CompletedTask;
        public Task Reasoning(string text) => Task.CompletedTask;
        public Task ReasoningEnd() => Task.CompletedTask;
        public Task Status(string phase) => Task.CompletedTask;
        public Task ToolCall(JObject call) => Task.CompletedTask;
        public Task ToolResult(JObject result) => Task.CompletedTask;
        public Task ToolNotRun(JObject result) => Task.CompletedTask;
        public Task Done(ToolTurnOutcome outcome) => Task.CompletedTask;

        public async Task ToolOutput(JObject output)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay);
            }
            if (Throw)
            {
                throw new InvalidOperationException("the socket is gone");
            }
            lock (Frames)
            {
                Frames.Add(output);
            }
        }
    }

    [Fact]
    public async Task TheRunnerReportsEveryLineWithItsStreamAsItIsWritten()
    {
        List<(string Stream, string Line)> seen = [];
        string command = IsWindows ? "echo one& echo two 1>&2& echo three" : "echo one; echo two 1>&2; echo three";

        ProcessResult result = IsWindows
            ? await ProcessRunner.RunAsync("cmd.exe", [], null, null, TimeSpan.FromSeconds(20), 1000, CancellationToken.None, rawArguments: $"/d /s /c \"{command}\"", onOutput: (s, l) => { lock (seen) { seen.Add((s, l)); } })
            : await ProcessRunner.RunAsync("/bin/sh", ["-c", command], null, null, TimeSpan.FromSeconds(20), 1000, CancellationToken.None, onOutput: (s, l) => { lock (seen) { seen.Add((s, l)); } });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(("stdout", "one"), seen.Select(x => (x.Stream, x.Line.TrimEnd())));
        Assert.Contains(("stderr", "two"), seen.Select(x => (x.Stream, x.Line.TrimEnd())));
        Assert.Contains(("stdout", "three"), seen.Select(x => (x.Stream, x.Line.TrimEnd())));
    }

    [Fact]
    public async Task TheRunnerStillReportsLinesPastTheSizeItKeeps()
    {
        int seen = 0;
        string command = IsWindows ? "for /l %i in (1,1,200) do @echo line%i" : "i=0; while [ $i -lt 200 ]; do echo line$i; i=$((i+1)); done";

        ProcessResult result = IsWindows
            ? await ProcessRunner.RunAsync("cmd.exe", [], null, null, TimeSpan.FromSeconds(30), 50, CancellationToken.None, rawArguments: $"/d /s /c \"{command}\"", onOutput: (_, _) => Interlocked.Increment(ref seen))
            : await ProcessRunner.RunAsync("/bin/sh", ["-c", command], null, null, TimeSpan.FromSeconds(30), 50, CancellationToken.None, onOutput: (_, _) => Interlocked.Increment(ref seen));

        Assert.True(result.Truncated);
        Assert.Equal(200, seen);
    }

    [Fact]
    public async Task ThePumpSendsLinesInOrderAndAllOfThemBeforeItCompletes()
    {
        RecordingSink sink = new();
        ToolOutputPump pump = new(sink, "call1", "code_bash");

        for (int i = 0; i < 50; i++)
        {
            pump.Enqueue("stdout", $"line {i}");
        }
        await pump.CompleteAsync();

        string all = string.Concat(sink.Frames.Select(f => f.Value<string>("text")));
        Assert.Equal(string.Concat(Enumerable.Range(0, 50).Select(i => $"line {i}\n")), all);
        Assert.All(sink.Frames, f => Assert.Equal("call1", f.Value<string>("id")));
    }

    [Fact]
    public async Task LinesThatPileUpWhileAFrameIsBeingSentShareOneFrame()
    {
        RecordingSink sink = new() { Delay = TimeSpan.FromMilliseconds(100) };
        ToolOutputPump pump = new(sink, "call1", "code_bash");

        for (int i = 0; i < 100; i++)
        {
            pump.Enqueue("stdout", $"line {i}");
        }
        await pump.CompleteAsync();

        Assert.True(sink.Frames.Count < 10, $"{sink.Frames.Count} frames for 100 lines");
    }

    [Fact]
    public async Task AChangeOfStreamStartsANewFrame()
    {
        RecordingSink sink = new() { Delay = TimeSpan.FromMilliseconds(50) };
        ToolOutputPump pump = new(sink, "call1", "code_bash");

        pump.Enqueue("stdout", "a");
        pump.Enqueue("stderr", "b");
        pump.Enqueue("stdout", "c");
        await pump.CompleteAsync();

        Assert.Equal(["stdout", "stderr", "stdout"], sink.Frames.Select(f => f.Value<string>("stream")).ToArray());
    }

    [Fact]
    public async Task OutputPastTheLimitIsDroppedWithOneNote()
    {
        RecordingSink sink = new();
        ToolOutputPump pump = new(sink, "call1", "code_bash");
        string line = new('z', 1000);

        for (int i = 0; i < (ToolOutputPump.MaxStreamedChars / 1000) + 50; i++)
        {
            pump.Enqueue("stdout", line);
        }
        await pump.CompleteAsync();

        string all = string.Concat(sink.Frames.Select(f => f.Value<string>("text")));
        Assert.True(all.Length <= ToolOutputPump.MaxStreamedChars + 200, all.Length.ToString());
        Assert.Single(sink.Frames.Where(f => f.Value<string>("text")!.Contains("live output stopped")));
    }

    [Fact]
    public async Task AVeryLongLineIsCut()
    {
        RecordingSink sink = new();
        ToolOutputPump pump = new(sink, "call1", "code_bash");

        pump.Enqueue("stdout", new string('q', ToolOutputPump.MaxLineChars * 3));
        await pump.CompleteAsync();

        Assert.True(sink.Frames[0].Value<string>("text")!.Length <= ToolOutputPump.MaxLineChars + 3);
    }

    [Fact]
    public async Task ASinkThatFailsNeverBreaksThePump()
    {
        RecordingSink sink = new() { Throw = true };
        ToolOutputPump pump = new(sink, "call1", "code_bash");

        pump.Enqueue("stdout", "hello");
        pump.Enqueue("stderr", "world");
        await pump.CompleteAsync();

        Assert.Empty(sink.Frames);
    }

    [Fact]
    public async Task NothingIsSentOnceTheSinkIsClosed()
    {
        RecordingSink sink = new() { Open = false };
        ToolOutputPump pump = new(sink, "call1", "code_bash");

        pump.Enqueue("stdout", "hello");
        await pump.CompleteAsync();

        Assert.Empty(sink.Frames);
    }

    [Fact]
    public async Task TheChatSocketSinkSendsAToolOutputFrame()
    {
        List<JObject> sent = [];
        ChatWsSink sink = new(frame => { sent.Add(frame); return Task.CompletedTask; }, () => true);

        Assert.True(sink.StreamsToolOutput);
        await sink.ToolOutput(new JObject { ["id"] = "c1", ["text"] = "hi\n" });

        Assert.Equal("c1", sent[0]["tool_output"]!["id"]!.ToString());
    }

    [Fact]
    public void ASinkMadeWithoutLiveOutputDoesNotAskForIt()
    {
        Assert.False(new ChatWsSink(_ => Task.CompletedTask, () => true, streamsToolOutput: false).StreamsToolOutput);
    }

    [Fact]
    public void OtherSinksDoNotAskForLiveOutput()
    {
        Assert.False(((IToolTurnSink)new CollectingSink()).StreamsToolOutput);
    }
}
