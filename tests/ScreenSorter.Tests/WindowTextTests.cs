using System.Globalization;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Status;

namespace ScreenSorter.Tests;

public class HistoryViewTests
{
    private const string Review = "_Review";

    private static HistoryItem Item(string path, string category, DecisionStage stage, bool exists = true, string hash = "h")
    {
        var record = new SortRecord("C:/in/x.png", path, hash, new Classification(category, null, "x", 0.9, stage), null, DateTimeOffset.UnixEpoch);
        var folder = Path.GetDirectoryName(path) ?? "";
        return new HistoryItem(new FileDecisions(record, record), folder, "why", exists, CanUndo: true);
    }

    private static readonly HistoryItem InReview = Item("S:/_Review/a.png", Review, DecisionStage.Unmatched, hash: "a");
    private static readonly HistoryItem GoneFromReview = Item("S:/_Review/b.png", Review, DecisionStage.Vision, exists: false, hash: "b");
    private static readonly HistoryItem ByVision = Item("S:/Games/Doom/c.png", "Games", DecisionStage.Vision, hash: "c");
    private static readonly HistoryItem ByUser = Item("S:/Chat/Discord/d.png", "Chat", DecisionStage.UserCorrection, hash: "d");
    private static readonly HistoryItem Undone = Item("S:/e.png", "", DecisionStage.Undone, hash: "e");
    private static readonly HistoryItem[] All = [InReview, GoneFromReview, ByVision, ByUser, Undone];

    [Theory]
    [InlineData(HistoryFilter.All, "abcde")]
    [InlineData(HistoryFilter.NeedsReview, "a")]
    [InlineData(HistoryFilter.PlacedByApp, "abc")]
    [InlineData(HistoryFilter.PlacedByYou, "de")]
    public void Filter_SelectsByKind(HistoryFilter filter, string expected) =>
        Assert.Equal(expected, Hashes(HistoryView.Filter(All, filter, "", Review)));

    [Fact]
    public void Filter_NeedsReview_IgnoresCaseOfTheReviewFolder() =>
        Assert.Equal("a", Hashes(HistoryView.Filter(All, HistoryFilter.NeedsReview, "", "_review")));

    [Theory]
    [InlineData("DOOM", "c")]      // folder, any case
    [InlineData("d.png", "d")]     // file name
    [InlineData("  e.png  ", "e")] // surrounding spaces ignored
    [InlineData("nothing", "")]
    [InlineData(null, "abcde")]
    public void Filter_SearchMatchesFileNameOrFolder(string? search, string expected) =>
        Assert.Equal(expected, Hashes(HistoryView.Filter(All, HistoryFilter.All, search, Review)));

    [Fact]
    public void Filter_CombinesKindAndSearch() =>
        Assert.Equal("", Hashes(HistoryView.Filter(All, HistoryFilter.PlacedByYou, "Doom", Review)));

    [Fact]
    public void FindSameFile_MatchesTheLatestHash()
    {
        Assert.Same(ByUser, HistoryView.FindSameFile(All, "d"));
        Assert.Null(HistoryView.FindSameFile(All, "zzz"));
        Assert.Null(HistoryView.FindSameFile(All, null));
    }

    [Fact]
    public void Messages_CoverEveryResult()
    {
        foreach (var change in Enum.GetValues<HistoryChange>())
        {
            Assert.False(string.IsNullOrWhiteSpace(HistoryView.UndoMessage(change)));
            Assert.False(string.IsNullOrWhiteSpace(HistoryView.MoveMessage(change)));
        }
        Assert.StartsWith("Moved back", HistoryView.UndoMessage(HistoryChange.Done));
        Assert.StartsWith("Moved.", HistoryView.MoveMessage(HistoryChange.Done));
        Assert.Contains("inside the sorted folders", HistoryView.MoveMessage(HistoryChange.NotAllowed));
        Assert.Contains("cannot be undone", HistoryView.UndoMessage(HistoryChange.NotAllowed));
    }

    private static string Hashes(IEnumerable<HistoryItem> items) => string.Concat(items.Select(i => i.Decisions.Latest.FileHash));
}

public class AccuracyViewTests
{
    [Fact]
    public void Rows_NameStagesAndFormatTheKeptShare()
    {
        var report = new AccuracyReport(
            [new StageAccuracy(DecisionStage.Rule, Sorted: 4, Review: 1, Changed: 1), new StageAccuracy(DecisionStage.Vision, 0, 2, 0)],
            [],
            ReviewedByUser: 0);

        var rows = AccuracyView.Rows(report, CultureInfo.InvariantCulture);

        Assert.Equal([new AccuracyRow("App rules", 4, 1, "75 %", 1), new AccuracyRow("Vision model", 0, 0, "-", 2)], rows);
    }

    [Theory]
    [InlineData(DecisionStage.Rule, "App rules")]
    [InlineData(DecisionStage.Example, "Your examples")]
    [InlineData(DecisionStage.Vision, "Vision model")]
    [InlineData(DecisionStage.Burst, "Copied from a similar shot")]
    [InlineData(DecisionStage.ImageCheck, "Blank image check")]
    [InlineData(DecisionStage.Unmatched, "Unmatched")]
    public void StageName_IsReadable(DecisionStage stage, string expected) => Assert.Equal(expected, AccuracyView.StageName(stage));

    [Theory]
    [InlineData(0, "You placed 0 screenshots from the review folder.")]
    [InlineData(1, "You placed 1 screenshot from the review folder.")]
    [InlineData(7, "You placed 7 screenshots from the review folder.")]
    public void ReviewedSentence_UsesSingularForOne(int count, string expected) => Assert.Equal(expected, AccuracyView.ReviewedSentence(count));
}

public class TrayTextTests
{
    [Theory]
    [InlineData(true, true, "LM Studio", "ScreenSorter (paused)")]
    [InlineData(false, true, "LM Studio", "ScreenSorter (waiting for the guide)")]
    [InlineData(false, false, "LM Studio", "ScreenSorter (LM Studio needs attention)")]
    [InlineData(false, false, null, "ScreenSorter")]
    public void Tooltip_ShowsTheMostImportantState(bool paused, bool held, string? attention, string expected) =>
        Assert.Equal(expected, TrayText.Tooltip(paused, held, attention));

    [Fact]
    public void Tooltip_FitsTheNotificationAreaLimit()
    {
        var text = TrayText.Tooltip(false, false, new string('x', 300));

        Assert.True(text.Length <= TrayText.MaxTooltipLength);
        Assert.EndsWith("…", text);
    }

    [Theory]
    [InlineData(1, 0, "Sorted 1 screenshot")]
    [InlineData(5, 0, "Sorted 5 screenshots")]
    [InlineData(5, 1, "Sorted 5 screenshots, 1 needs review")]
    [InlineData(1, 3, "Sorted 1 screenshot, 3 need review")]
    [InlineData(0, 2, "Sorted 0 screenshots, 2 need review")]
    public void BatchSummary_CountsInWords(int sorted, int review, string expected) =>
        Assert.Equal(expected, TrayText.Summary(new BatchSummary(sorted, review)));
}
