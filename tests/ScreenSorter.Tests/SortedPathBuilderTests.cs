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
    [InlineData("Screenshot 2026-10-31 210412 boss fight.png", false)]
    [InlineData("Deadlock_2026-10-31_21-04-12.png", true)]
    [InlineData("Zenless Zone Zero_2026-10-31_21-04-12_2.png", true)]
    [InlineData("Deadlock_2026-10-31_21-04-12 - Copy.png", true)]
    [InlineData("Deadlock_2026-10-31_21-04-12 - Copy (2).png", true)]
    [InlineData("Screenshot 2026-10-31 210412 - Copy.png", true)]
    [InlineData("Deadlock_2026-10-31_21-04-12 final.png", false)]
    [InlineData("Deadlock_2026-13-31_21-04-12.png", false)]
    [InlineData("_2026-10-31_21-04-12.png", false)]
    public void CaptureTime_FromSnippingToolName(string name, bool parsed)
    {
        var t = CaptureTime.FromFileName(name);

        Assert.Equal(parsed, t.HasValue);
        if (parsed)
            Assert.Equal(Capture, t);
    }

    [Theory]
    [InlineData(". .", "Unknown")]
    [InlineData("a . ", "a")]
    [InlineData("Discord\u202Egnp.exe", "Discordgnp.exe")]
    [InlineData("Zero\u200Bwidth", "Zerowidth")]
    public void Sanitize_DropsWhatWindowsWouldChangeOrHide(string raw, string expected) =>
        Assert.Equal(expected, SortedPathBuilder.Sanitize(raw));

    [Theory]
    [InlineData(@"C:\Shots\Chat\x.png", true)]
    [InlineData(@"C:\Shots\Chat\Discord\x.png", true)]
    [InlineData(@"C:\Shots\x.png", false)]
    [InlineData(@"C:\Shots\..\x\y.png", false)]
    [InlineData(@"C:\ShotsElsewhere\Chat\x.png", false)]
    [InlineData(@"C:\Shots\Chat \x.png", false)]
    [InlineData(@"D:\Shots\Chat\x.png", false)]
    public void IsInCategoryFolder_OnlyBelowTheRoot(string path, bool expected) =>
        Assert.Equal(expected, SortedPathBuilder.IsInCategoryFolder(@"C:\Shots\", path));

    [Fact]
    public void Build_FallsBackToUnknownWhenTheResultWouldLeaveTheRoot()
    {
        // "@@" has an empty match key, like "..", so an existing ".." entry would be reused as the sub-folder.
        var path = SortedPathBuilder.Build(@"C:\Shots", new Classification("Chat", "@@", "Chat", 1, DecisionStage.Vision),
            Capture, ".png", [".."]);

        Assert.Equal(@"C:\Shots\Unknown\Chat_2026-10-31_21-04-12.png", path);
    }
}
