using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Tests;

public class SortedPathBuilderTests
{
    private static readonly DateTime Capture = new(2026, 10, 31, 21, 4, 12);

    [Fact]
    public void Build_CategoryAndSub()
    {
        var path = SortedPathBuilder.Build(@"C:\Shots", new Classification("Chat", "Discord", "Discord", 1, DecisionStage.Rule),
            Capture, ".PNG", []);

        Assert.Equal(@"C:\Shots\Chat\Discord\Discord_2026-10-31_21-04-12.png", path);
    }

    [Fact]
    public void Build_ReusesExistingFolderSpelling()
    {
        var path = SortedPathBuilder.Build(@"C:\Shots", new Classification("Games", "ELDEN RING", "ELDEN RING", 1, DecisionStage.Rule),
            Capture, ".png", ["Elden Ring", "Hades"]);

        Assert.Equal(@"C:\Shots\Games\Elden Ring\ELDEN RING_2026-10-31_21-04-12.png", path);
    }

    [Theory]
    [InlineData("Half-Life: Alyx", "Half-Life Alyx")]
    [InlineData("ELDEN RING™", "ELDEN RING")]
    [InlineData("  a / b \\ c?  ", "a b c")]
    [InlineData("trailing...", "trailing")]
    [InlineData("CON", "CON_")]
    [InlineData("???", "Unknown")]
    public void Sanitize(string input, string expected) =>
        Assert.Equal(expected, SortedPathBuilder.Sanitize(input));

    [Fact]
    public void WithSuffix_InsertsBeforeExtension() =>
        Assert.Equal(@"C:\a\x_3.png", SortedPathBuilder.WithSuffix(@"C:\a\x.png", 3));

    [Theory]
    [InlineData("Screenshot 2026-10-31 210412.png", true)]
    [InlineData("Screenshot 2026-10-31 210412 (2).png", true)]
    [InlineData("IMG_0001.png", false)]
    public void CaptureTime_FromSnippingToolName(string name, bool parsed)
    {
        var t = CaptureTime.FromFileName(name);

        Assert.Equal(parsed, t.HasValue);
        if (parsed)
            Assert.Equal(Capture, t);
    }
}
