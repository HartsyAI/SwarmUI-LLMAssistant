using System.Diagnostics;
using System.IO;
using Hartsy.Extensions.LLMAssistant.Services;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Keeping a model-supplied path inside a folder: text tricks, host-reserved names, and links that lead out.</summary>
public sealed class PathSandboxTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "llma-sandbox-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string _outside;

    public PathSandboxTests()
    {
        _root = Path.Combine(_base, "workspace");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "inside");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
    }

    public void Dispose()
    {
        // A link is removed as a link first: deleting a folder tree that holds one can be refused, and must never reach what the link points at.
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(_base, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
            {
                Directory.Delete(directory);
            }
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the temp folder cleanup.
        }
    }

    private string Resolve(string requested)
    {
        Assert.True(PathSandbox.TryResolve(_root, requested, out string full, out string error), error);
        return full;
    }

    private string Refused(string requested)
    {
        Assert.False(PathSandbox.TryResolve(_root, requested, out string full, out string error), $"'{requested}' resolved to {full}");
        Assert.NotNull(error);
        Assert.Null(full);
        Assert.DoesNotContain(_base, error);
        return error;
    }

    [Theory]
    [InlineData("src/a.txt")]
    [InlineData("src\\a.txt")]
    [InlineData("./src/a.txt")]
    [InlineData("src//a.txt")]
    public void ARelativePathInsideTheRootResolves(string requested)
        => Assert.Equal(Path.Combine(_root, "src", "a.txt"), Resolve(requested), ignoreCase: OperatingSystem.IsWindows());

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("  ")]
    public void NothingOrADotIsTheRootItself(string requested)
        => Assert.Equal(_root, Resolve(requested), ignoreCase: OperatingSystem.IsWindows());

    [Fact]
    public void APathThatDoesNotExistYetStillResolvesForAWrite()
        => Assert.Equal(Path.Combine(_root, "new", "deeper", "file.txt"), Resolve("new/deeper/file.txt"), ignoreCase: OperatingSystem.IsWindows());

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("src/../../outside/secret.txt")]
    [InlineData("..")]
    [InlineData("src/..")]
    [InlineData("..\\outside")]
    [InlineData("a/b/../../../x")]
    public void ClimbingOutWithDotDotIsRefusedOutright(string requested) => Assert.Contains("..", Refused(requested));

    [Fact]
    public void AnAbsolutePathIsAcceptedOnlyInsideTheRoot()
    {
        Assert.Equal(Path.Combine(_root, "src", "a.txt"), Resolve(Path.Combine(_root, "src", "a.txt")), ignoreCase: OperatingSystem.IsWindows());
        Assert.Contains("outside", Refused(Path.Combine(_outside, "secret.txt")));
        Assert.Contains("outside", Refused(OperatingSystem.IsWindows() ? "C:/Windows/System32/drivers/etc/hosts" : "/etc/passwd"));
    }

    [Fact]
    public void ARootPrefixSharedWithASiblingFolderIsNotInside()
    {
        string sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        Assert.Contains("outside", Refused(Path.Combine(sibling, "x.txt")));
        Assert.False(PathSandbox.IsInside(_root, sibling));
        Assert.True(PathSandbox.IsInside(_root, Path.Combine(_root, "src")));
        Assert.True(PathSandbox.IsInside(_root, _root));
    }

    [Fact]
    public void NetworkPathsAndNulBytesAreRefused()
    {
        Refused("//server/share/x");
        Refused("\\\\server\\share\\x");
        Refused("src/a\0.txt");
    }

    [Fact]
    public void WindowsReservedNamesAndStreamsAreRefusedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Refused("src/CON");
        Refused("nul.txt");
        Refused("src/com1.log");
        Refused("src/LPT9");
        Refused("src/a.txt:hidden");
        Refused("src/trailing.");
        Refused("src/trailing /x");
    }

    [Fact]
    public void ANameThatMerelyStartsLikeAReservedOneIsFine() => Resolve("src/console.txt");

    [Fact]
    public void RelativeNamesAreForwardSlashedAndTheRootIsEmpty()
    {
        Assert.Equal("src/a.txt", PathSandbox.Relative(_root, Path.Combine(_root, "src", "a.txt")));
        Assert.Equal("", PathSandbox.Relative(_root, _root));
    }

    /// <summary>Makes <paramref name="link"/> a link to <paramref name="target"/>, as a symbolic link when the host lets this user and as a junction
    /// on Windows when not. Returns false when neither could be made, so the test says nothing rather than failing for a host's reasons.</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void ALinkInsideTheRootThatPointsOutsideCannotBeUsedToReachWhatIsBeyond()
    {
        string link = Path.Combine(_root, "escape");
        if (!TryLink(link, _outside))
        {
            return;
        }
        Assert.Contains("link", Refused("escape/secret.txt"));
        Assert.Contains("link", Refused("escape"));
        // Writing a new file through it is refused too, not just reading one that is there.
        Assert.Contains("link", Refused("escape/new-file.txt"));
    }

    [Fact]
    public void ALinkThatStaysInsideTheRootIsFollowedAndAllowed()
    {
        string link = Path.Combine(_root, "alias");
        if (!TryLink(link, Path.Combine(_root, "src")))
        {
            return;
        }
        Assert.Equal(Path.Combine(_root, "src", "a.txt"), Resolve("alias/a.txt"), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void ALinkWhoseTargetDoesNotExistYetDoesNotLetAWriteCreateItOutside()
    {
        if (OperatingSystem.IsWindows())
        {
            // A symbolic link to a missing target needs privileges Windows does not give by default; the escape through a junction above covers the rest.
            return;
        }
        string link = Path.Combine(_root, "dangling");
        File.CreateSymbolicLink(link, Path.Combine(_outside, "not-there-yet.txt"));
        Assert.Contains("link", Refused("dangling"));
    }

    [Fact]
    public void TheRootItselfBeingBehindALinkIsFine()
    {
        string realRoot = Path.Combine(_base, "real-root");
        Directory.CreateDirectory(realRoot);
        File.WriteAllText(Path.Combine(realRoot, "f.txt"), "x");
        string linkedRoot = Path.Combine(_base, "linked-root");
        if (!TryLink(linkedRoot, realRoot))
        {
            return;
        }
        Assert.True(PathSandbox.TryResolve(linkedRoot, "f.txt", out string full, out string error), error);
        Assert.Equal(Path.Combine(realRoot, "f.txt"), full, ignoreCase: OperatingSystem.IsWindows());
    }
}
