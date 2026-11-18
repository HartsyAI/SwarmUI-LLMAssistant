using System.Runtime.InteropServices;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Hooks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>The process runner that command hooks use: input, output, limits and killing, with programs every machine has.</summary>
public class ProcessRunnerTests
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>A program that does not return for a while: the shell's own pause.</summary>
    private static (string File, string[] Args) Sleeper(int seconds) => IsWindows ? ("ping", ["-n", $"{seconds + 1}", "127.0.0.1"]) : ("sleep", [$"{seconds}"]);

    [Fact]
    public async Task ItPassesTextOnStandardInputAndReadsStandardOutput()
    {
        // `sort` reads standard input and writes it back in order, on every platform.
        ProcessResult result = await ProcessRunner.RunAsync("sort", [], null, "pear\napple\nfig\n", TimeSpan.FromSeconds(20), 1000, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["apple", "fig", "pear"], result.Stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task ItReportsAProgramThatCannotBeStartedWithoutThrowing()
    {
        ProcessResult result = await ProcessRunner.RunAsync("this-program-does-not-exist-12345", [], null, null, TimeSpan.FromSeconds(5), 1000, CancellationToken.None);

        Assert.NotNull(result.StartError);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task ItReturnsANonZeroExitCode()
    {
        // The dotnet host exits with an error for an unknown command.
        ProcessResult result = await ProcessRunner.RunAsync("dotnet", ["not-a-real-command-xyz"], null, null, TimeSpan.FromSeconds(30), 5000, CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(-1, result.ExitCode);
    }

    [Fact]
    public async Task ItKillsAProcessThatRunsPastItsTimeLimit()
    {
        (string file, string[] args) = Sleeper(30);
        DateTime start = DateTime.UtcNow;

        ProcessResult result = await ProcessRunner.RunAsync(file, args, null, null, TimeSpan.FromMilliseconds(500), 1000, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(15), "It should have been killed long before the program finished.");
    }

    [Fact]
    public async Task CancellingKillsTheProcessToo()
    {
        (string file, string[] args) = Sleeper(30);
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(500));
        DateTime start = DateTime.UtcNow;

        ProcessResult result = await ProcessRunner.RunAsync(file, args, null, null, TimeSpan.FromSeconds(60), 1000, cancel.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ItKeepsOnlyTheOutputItIsAllowed()
    {
        string many = string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"line {i:D5}"));

        ProcessResult result = await ProcessRunner.RunAsync("sort", [], null, many, TimeSpan.FromSeconds(30), 200, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.True(result.Stdout.Length <= 200);
    }

    [Fact]
    public async Task ACommandHookAnswersThroughTheRealRunner_ExitCodeTwoIsADeny()
    {
        // `dotnet` with an unknown command exits with a non-zero code that is not 2, which a hook treats as a failure.
        ToolHook hook = new() { Id = "c", Name = "c", Kind = "command", Command = "dotnet", Args = ["not-a-real-command-xyz"], TimeoutSeconds = 30 };

        HookCommandResult result = await new ProcessHookCommandRunner().RunAsync(hook, new JObject { ["tool"] = "file_write" }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("exited with code", result.Error);
    }

    [Fact]
    public async Task ACommandHookThatCannotStartIsAFailureNotACrash()
    {
        ToolHook hook = new() { Id = "c", Name = "c", Kind = "command", Command = "this-program-does-not-exist-12345", TimeoutSeconds = 5 };

        HookCommandResult result = await new ProcessHookCommandRunner().RunAsync(hook, new JObject(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("could not start", result.Error);
    }

    [Fact]
    public async Task AProgramThatNeverReadsItsInputIsStillStoppedByTheTimeLimit()
    {
        // The input is far larger than a pipe holds, and the program never reads it, so the write blocks; the limit must still apply.
        (string file, string[] args) = Sleeper(30);
        DateTime start = DateTime.UtcNow;

        ProcessResult result = await ProcessRunner.RunAsync(file, args, null, new string('x', 4_000_000), TimeSpan.FromMilliseconds(800), 1000, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(15), "A blocked write must not outlast the time limit.");
    }

    [Fact]
    public async Task StopCancelsAProgramThatNeverReadsItsInput()
    {
        (string file, string[] args) = Sleeper(30);
        using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(500));
        DateTime start = DateTime.UtcNow;

        ProcessResult result = await ProcessRunner.RunAsync(file, args, null, new string('x', 4_000_000), TimeSpan.FromSeconds(60), 1000, stop.Token);

        Assert.True(result.Cancelled);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(15));
    }
}
