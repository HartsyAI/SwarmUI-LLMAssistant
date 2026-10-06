using Hartsy.Extensions.LLMAssistant.Backends;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>A Clef release is found by directory name under the LLM model folders, directly or in a clef subfolder.</summary>
public sealed class ClefReleaseResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clef-resolve-" + Guid.NewGuid().ToString("N"));

    public ClefReleaseResolutionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Release(params string[] parts)
    {
        string dir = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "joint_head.safetensors"), "x");
        return dir;
    }

    [Fact]
    public void FindsTheReleaseDirectlyUnderTheFolder() =>
        Assert.Equal(Release("clef-flash"), HartsyLocalLLMProvider.ResolveDecisionRelease("clef-flash", [_root]));

    [Fact]
    public void FindsTheReleaseInAClefSubfolder() =>
        Assert.Equal(Release("clef", "clef-flash"), HartsyLocalLLMProvider.ResolveDecisionRelease("clef-flash", [_root]));

    [Fact]
    public void ReturnsNullWhenMissingOrBlank()
    {
        Assert.Null(HartsyLocalLLMProvider.ResolveDecisionRelease("clef-flash", [_root]));
        Assert.Null(HartsyLocalLLMProvider.ResolveDecisionRelease("", [_root]));
    }
}
