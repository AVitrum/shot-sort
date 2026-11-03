using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class BurstMemoryTests
{
    private static readonly DateTime Noon = new(2026, 10, 31, 12, 0, 0);

    private static BurstAnchor Anchor(float[] vector, DateTime captured, string? process = "a.exe", string category = "Browser") =>
        new(vector, captured, process, "hash", new Classification(category, null, category, 0.9, DecisionStage.Vision));

    private static BurstMemory Memory(int maxAnchors = 256) =>
        new(Opt(new BurstOptions { WindowSeconds = 300, Similarity = 0.94, MaxAnchors = maxAnchors }));

    [Fact]
    public void Find_ReturnsTheMostSimilarAnchor()
    {
        var memory = Memory();
        memory.Remember(Anchor([0.96f, 0.28f], Noon, category: "Far"));
        memory.Remember(Anchor([1, 0], Noon, category: "Near"));

        var match = memory.Find([1, 0], Noon, "a.exe");

        Assert.Equal("Near", match?.Anchor.Classification.Category);
        Assert.Equal(1, match!.Similarity, 3);
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(-300, true)]
    [InlineData(301, false)]
    [InlineData(-301, false)]
    public void Find_WindowCountsBothWays(int seconds, bool found)
    {
        var memory = Memory();
        memory.Remember(Anchor([1, 0], Noon));

        Assert.Equal(found, memory.Find([1, 0], Noon.AddSeconds(seconds), "a.exe") is not null);
    }

    [Fact]
    public void Find_BelowTheThreshold_IsNoMatch()
    {
        var memory = Memory();
        memory.Remember(Anchor([0.9f, 0.436f], Noon));

        Assert.Null(memory.Find([1, 0], Noon, "a.exe"));
    }

    [Fact]
    public void Find_ComparesTheAppIgnoringCase_AndBacklogOnlyMatchesBacklog()
    {
        var memory = Memory();
        memory.Remember(Anchor([1, 0], Noon));

        Assert.NotNull(memory.Find([1, 0], Noon, "A.EXE"));
        Assert.Null(memory.Find([1, 0], Noon, null));
    }

    [Fact]
    public void Find_VectorOfAnotherLength_IsSkipped()
    {
        var memory = Memory();
        memory.Remember(Anchor([1, 0], Noon));

        Assert.Null(memory.Find([1, 0, 0], Noon, "a.exe"));
    }

    [Fact]
    public void Remember_DropsTheOldestBeyondTheLimit()
    {
        var memory = Memory(maxAnchors: 2);
        memory.Remember(Anchor([1, 0], Noon, category: "Oldest"));
        memory.Remember(Anchor([0, 1], Noon, category: "Second"));
        memory.Remember(Anchor([-1, 0], Noon, category: "Third"));

        Assert.Null(memory.Find([1, 0], Noon, "a.exe"));
        Assert.NotNull(memory.Find([0, 1], Noon, "a.exe"));
    }

    [Fact]
    public void Forget_RemovesTheAnchor()
    {
        var memory = Memory();
        var anchor = Anchor([1, 0], Noon);
        memory.Remember(anchor);

        memory.Forget(anchor);

        Assert.Null(memory.Find([1, 0], Noon, "a.exe"));
    }
}
