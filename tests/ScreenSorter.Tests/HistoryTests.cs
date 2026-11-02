using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Storage;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public sealed class DecisionHistoryTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly SqliteDecisionLog _log;
    private readonly FileSystemScreenshotStore _store;
    private readonly DecisionHistory _history;

    public DecisionHistoryTests()
    {
        var paths = Opt(new PathsOptions { SortedRoot = _temp.Path, ReviewFolder = "_Review", DatabasePath = Path.Combine(_temp.Path, "db", "h.db") });
        _log = new SqliteDecisionLog(paths, Opt(new PrivacyOptions()));
        _store = new FileSystemScreenshotStore(Opt(new IntakeOptions { StabilityPollMilliseconds = 50 }), TimeProvider.System);
        _history = new DecisionHistory(_store, _log, paths, TimeProvider.System, NullLogger<DecisionHistory>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private async Task<(string Original, string Sorted, string Hash)> SortedFile(string name = "shot.png", int seed = 1, DecisionStage stage = DecisionStage.Vision)
    {
        var original = _temp.File(name, seed);
        var hash = await _store.ComputeHashAsync(original, CancellationToken.None);
        var sorted = _store.Move(original, Path.Combine(_temp.Path, "Games", "Hades", name));
        await _log.RecordAsync(new SortRecord(original, sorted, hash, new Classification("Games", "Hades", "Hades", 0.9, stage), null, DateTimeOffset.UtcNow), CancellationToken.None);
        return (original, sorted, hash);
    }

    private async Task<HistoryItem> Single() => Assert.Single(await _history.ListAsync(10, CancellationToken.None));

    [Fact]
    public async Task List_ShowsOneRowPerFile_NewestFirst_WithBothEndsOfItsPast()
    {
        var (original, sorted, hash) = await SortedFile();
        await SortedFile("other.png", seed: 2);
        await _log.RecordAsync(new SortRecord(sorted, sorted, hash, new Classification("Other", null, "Other", 1, DecisionStage.UserCorrection), null, DateTimeOffset.UtcNow), CancellationToken.None);

        var items = await _history.ListAsync(10, CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(DecisionStage.UserCorrection, items[0].Stage);
        Assert.Equal(original, items[0].Decisions.First.OriginalPath);
        Assert.Equal("You moved it here. The app had chosen Games/Hades.", items[0].Why);
        Assert.Equal("other.png", items[1].FileName);
    }

    [Fact]
    public async Task List_RespectsTheLimit()
    {
        await SortedFile("a.png", seed: 1);
        await SortedFile("b.png", seed: 2);

        var item = Assert.Single(await _history.ListAsync(1, CancellationToken.None));

        Assert.Equal("b.png", item.FileName);
    }

    [Fact]
    public async Task Item_ShowsFolderRelativeToTheRoot()
    {
        await SortedFile();

        Assert.Equal(Path.Combine("Games", "Hades"), (await Single()).Folder);
    }

    [Fact]
    public async Task Undo_PutsTheFileBackAndRecordsIt()
    {
        var (original, sorted, hash) = await SortedFile();

        var result = await _history.UndoAsync(await Single(), CancellationToken.None);

        Assert.Equal(HistoryChange.Done, result);
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(sorted));
        var record = await _log.FindLatestByHashAsync(hash, CancellationToken.None);
        Assert.Equal(DecisionStage.Undone, record?.Classification.Stage);
        Assert.Equal(original, record?.NewPath);
    }

    [Fact]
    public async Task Undo_WhenTheNameIsTaken_RecordsWhereTheFileWent()
    {
        var (original, _, hash) = await SortedFile();
        _temp.File("shot.png", seed: 9);

        Assert.Equal(HistoryChange.Done, await _history.UndoAsync(await Single(), CancellationToken.None));

        var actual = Path.Combine(_temp.Path, "shot_2.png");
        Assert.True(File.Exists(actual));
        Assert.True(File.Exists(original));
        Assert.Equal(actual, (await _log.FindLatestByHashAsync(hash, CancellationToken.None))?.NewPath);
    }

    [Fact]
    public async Task Undo_Twice_IsRefused()
    {
        await SortedFile();
        await _history.UndoAsync(await Single(), CancellationToken.None);

        var item = await Single();

        Assert.False(item.CanUndo);
        Assert.Equal(HistoryChange.NotAllowed, await _history.UndoAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task Undo_NotOfferedForAMoveTheUserMade()
    {
        var (_, sorted, hash) = await SortedFile();
        await _log.RecordAsync(new SortRecord(sorted, sorted, hash, new Classification("Other", null, "Other", 1, DecisionStage.UserCorrection), null, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False((await Single()).CanUndo);
    }

    [Fact]
    public async Task Undo_NotOfferedWhenTheFileIsGone()
    {
        var (_, sorted, _) = await SortedFile();
        File.Delete(sorted);

        var item = await Single();

        Assert.False(item.FileExists);
        Assert.False(item.CanUndo);
    }

    [Fact]
    public async Task Undo_NotOfferedWhenTheFileCameFromACategoryFolder()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "Games", "A"));
        var file = _temp.File(Path.Combine("Games", "A", "shot.png"));
        var hash = await _store.ComputeHashAsync(file, CancellationToken.None);
        await _log.RecordAsync(new SortRecord(file, file, hash, new Classification("Games", "A", "A", 1, DecisionStage.Vision), null, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False((await Single()).CanUndo);
    }

    [Fact]
    public async Task Undo_ReplacedFile_IsLeftAlone()
    {
        var (original, sorted, hash) = await SortedFile();
        var item = await Single();
        File.WriteAllBytes(sorted, [1, 2, 3]);

        Assert.Equal(HistoryChange.Changed, await _history.UndoAsync(item, CancellationToken.None));

        Assert.True(File.Exists(sorted));
        Assert.False(File.Exists(original));
        Assert.Equal(DecisionStage.Vision, (await _log.FindLatestByHashAsync(hash, CancellationToken.None))?.Classification.Stage);
    }

    [Fact]
    public async Task Undo_MoveFails_RecordSaysTheFileStayed()
    {
        var (_, sorted, hash) = await SortedFile();
        var item = await Single();

        HistoryChange result;
        // Reading is shared with the hash, but a move needs the file to be free.
        using (new FileStream(sorted, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await _history.UndoAsync(item, CancellationToken.None);

        Assert.Equal(HistoryChange.Failed, result);
        Assert.True(File.Exists(sorted));
        var record = await _log.FindLatestByHashAsync(hash, CancellationToken.None);
        Assert.Equal(DecisionStage.Vision, record?.Classification.Stage);
        Assert.Equal(sorted, record?.NewPath);
    }

    [Fact]
    public async Task MoveTo_MovesIntoTheFolder()
    {
        var (_, sorted, _) = await SortedFile();
        var folder = Path.Combine(_temp.Path, "Other");

        Assert.Equal(HistoryChange.Done, _history.MoveTo(await Single(), folder));

        Assert.False(File.Exists(sorted));
        Assert.True(File.Exists(Path.Combine(folder, "shot.png")));
    }

    [Fact]
    public async Task MoveTo_RefusesTheRoot_BecauseIntakeWouldSortItAgain()
    {
        var (_, sorted, _) = await SortedFile();

        Assert.Equal(HistoryChange.NotAllowed, _history.MoveTo(await Single(), _temp.Path));
        Assert.True(File.Exists(sorted));
    }

    [Fact]
    public async Task MoveTo_RefusesAFolderOutsideTheRoot()
    {
        using var elsewhere = new TempFolder();
        var (_, sorted, _) = await SortedFile();

        Assert.Equal(HistoryChange.NotAllowed, _history.MoveTo(await Single(), elsewhere.Path));
        Assert.True(File.Exists(sorted));
    }

    [Theory]
    [InlineData(DecisionStage.Rule, "A rule sends the focused app to this folder.")]
    [InlineData(DecisionStage.Example, "It looks like examples you taught it (0.90 similar).")]
    [InlineData(DecisionStage.Vision, "The vision model chose this folder.")]
    public async Task Why_NamesTheStage(DecisionStage stage, string why)
    {
        await SortedFile(stage: stage);

        Assert.Equal(why, (await Single()).Why);
    }

    [Fact]
    public async Task Why_NamesTheFocusedApp()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "Code"));
        var file = _temp.File(Path.Combine("Code", "a.png"));
        var hash = await _store.ComputeHashAsync(file, CancellationToken.None);
        await _log.RecordAsync(new SortRecord(file, file, hash, new Classification("Code", null, "Code", 1, DecisionStage.Rule), Focus("Code.exe"), DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.StartsWith("Code.exe had focus", (await Single()).Why);
    }
}

public class AccuracyReportTests
{
    private static DecisionFact Fact(string hash, DecisionStage stage, string category, string? sub = null) => new(hash, stage, category, sub);

    private static StageAccuracy Stage(AccuracyReport report, DecisionStage stage) => report.Stages.Single(s => s.Stage == stage);

    [Fact]
    public void CountsFirstDecisions_AndMovesAsMisses()
    {
        var report = AccuracyReport.Build(
        [
            Fact("a", DecisionStage.Vision, "Games", "Hades"),
            Fact("b", DecisionStage.Vision, "Games", "Hades"),
            Fact("b", DecisionStage.UserCorrection, "Games", "Dota 2"),
            Fact("c", DecisionStage.Vision, "_Review"),
            Fact("d", DecisionStage.Rule, "Code"),
        ], "_Review");

        var vision = Stage(report, DecisionStage.Vision);
        Assert.Equal((2, 1, 1), (vision.Sorted, vision.Review, vision.Changed));
        Assert.Equal(0.5, vision.KeptShare);
        Assert.Equal(1.0, Stage(report, DecisionStage.Rule).KeptShare);
        Assert.Equal(new FolderCorrection("Games/Hades", "Games/Dota 2", 1), Assert.Single(report.Corrections));
    }

    [Fact]
    public void FilesTheUserPlacedFirst_AreNotCounted()
    {
        var report = AccuracyReport.Build([Fact("a", DecisionStage.UserCorrection, "Games", "Hades")], "_Review");

        Assert.Empty(report.Stages);
    }

    [Fact]
    public void ARenameInTheSameFolder_IsNotAMiss()
    {
        var report = AccuracyReport.Build(
        [
            Fact("a", DecisionStage.Vision, "Games", "ELDEN RING"),
            Fact("a", DecisionStage.UserCorrection, "Games", "Elden Ring"),
        ], "_Review");

        Assert.Equal(0, Stage(report, DecisionStage.Vision).Changed);
    }

    [Fact]
    public void AnUndoIsAMiss_ButHasNoTargetFolder()
    {
        var report = AccuracyReport.Build(
        [
            Fact("a", DecisionStage.Vision, "Games", "Hades"),
            Fact("a", DecisionStage.Undone, ""),
        ], "_Review");

        Assert.Equal(1, Stage(report, DecisionStage.Vision).Changed);
        Assert.Empty(report.Corrections);
    }

    [Fact]
    public void PlacingAReviewedFile_IsCountedAsReviewNotAsAMiss()
    {
        var report = AccuracyReport.Build(
        [
            Fact("a", DecisionStage.Vision, "_Review"),
            Fact("a", DecisionStage.UserCorrection, "Other"),
        ], "_Review");

        var vision = Stage(report, DecisionStage.Vision);
        Assert.Equal((0, 1, 0), (vision.Sorted, vision.Review, vision.Changed));
        Assert.Null(vision.KeptShare);
        Assert.Equal(1, report.ReviewedByUser);
    }

    [Fact]
    public void OnlyTheLastCorrectionCounts()
    {
        var report = AccuracyReport.Build(
        [
            Fact("a", DecisionStage.Vision, "Games", "Hades"),
            Fact("a", DecisionStage.UserCorrection, "Other"),
            Fact("a", DecisionStage.UserCorrection, "Games", "Hades"),
        ], "_Review");

        Assert.Equal(0, Stage(report, DecisionStage.Vision).Changed);
    }
}
