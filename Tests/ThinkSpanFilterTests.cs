using Hartsy.Extensions.LLMAssistant.Services;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary><see cref="ThinkSpanFilter"/>: inline think spans leave the speech, whatever the chunk boundaries.</summary>
public class ThinkSpanFilterTests
{
    private static string Run(params string[] deltas)
    {
        ThinkSpanFilter filter = new();
        string spoken = "";
        foreach (string delta in deltas)
        {
            spoken += filter.Feed(delta);
        }
        return spoken + filter.Flush();
    }

    [Fact]
    public void TextWithoutATagPassesThroughUnchanged()
    {
        Assert.Equal("a < b and 2 > 1", Run("a < b and 2 > 1"));
    }

    [Fact]
    public void AWholeSpanLeavesTheSpeech()
    {
        Assert.Equal("ab", Run("a<think>plan it</think>b"));
    }

    [Fact]
    public void ATagSplitAcrossDeltasStillLeavesTheSpeech()
    {
        Assert.Equal("ab", Run("a<thi", "nk>plan", "</th", "ink>b"));
    }

    [Fact]
    public void TheTagsMatchWhateverTheirCase()
    {
        Assert.Equal("b", Run("<THINK>plan</Think>b"));
    }

    [Fact]
    public void AnUnclosedSpanIsDroppedAtTheEnd()
    {
        Assert.Equal("x", Run("x<think>never", "ends"));
    }

    [Fact]
    public void APartialTagAtTheEndIsSpokenWhenNothingCompletesIt()
    {
        Assert.Equal("2 <", Run("2 <"));
    }

    [Fact]
    public void AHeldPartialTagThatIsNotATagIsSpoken()
    {
        Assert.Equal("x< y", Run("x<", " y"));
    }
}
