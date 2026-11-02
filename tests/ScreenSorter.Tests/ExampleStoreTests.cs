using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Storage;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class SqliteExampleStoreTests : IDisposable
{
    private const string HashA = "aa6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40";
    private const string HashB = "bb6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40";
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private SqliteExampleStore Store(string collection = "shots") => new(
        Opt(new PathsOptions { DatabasePath = Path.Combine(_temp.Path, "db", "test.db") }),
        Opt(new ExamplesOptions { Collection = collection }),
        Opt(new ClipOptions { EmbeddingSize = 2 }));

    private static Example Label(string hash, string category, string? sub) => new(hash, category, sub, Example.UserSource, T0);

    [Fact]
    public async Task NewStore_IsEmpty()
    {
        var store = Store();

        Assert.Equal(0, await store.CountAsync(CancellationToken.None));
        Assert.Empty(await store.SearchAsync([1f, 0f], 5, CancellationToken.None));
        await store.DeleteAsync(HashA, CancellationToken.None);
    }

    [Fact]
    public async Task Search_ReturnsNearestFirst_WithinTheLimit()
    {
        var store = Store();
        await store.UpsertAsync(Label(HashA, "Games", "Hades"), [1f, 0f], CancellationToken.None);
        await store.UpsertAsync(Label(HashB, "Browser", null), [0f, 1f], CancellationToken.None);

        var hits = await store.SearchAsync([1f, 0f], 5, CancellationToken.None);

        Assert.Equal([new ExampleHit(1, "Games", "Hades"), new ExampleHit(0, "Browser", null)], hits);
        Assert.Single(await store.SearchAsync([1f, 0f], 1, CancellationToken.None));
    }

    [Fact]
    public async Task Search_ScoresByAngle_NotLength()
    {
        var store = Store();
        await store.UpsertAsync(Label(HashA, "Games", "Hades"), [3f, 4f], CancellationToken.None);

        var hit = Assert.Single(await store.SearchAsync([0.6f, 0.8f], 5, CancellationToken.None));

        Assert.Equal(1, hit.Score, 5);
    }

    [Fact]
    public async Task Upsert_SameFile_ReplacesTheLabel()
    {
        var store = Store();
        await store.UpsertAsync(Label(HashA, "Games", "Hades"), [1f, 0f], CancellationToken.None);
        await store.UpsertAsync(Label(HashA, "Games", "Deadlock"), [1f, 0f], CancellationToken.None);

        Assert.Equal(1, await store.CountAsync(CancellationToken.None));
        Assert.Equal("Deadlock", Assert.Single(await store.SearchAsync([1f, 0f], 5, CancellationToken.None)).SubFolder);
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatFile()
    {
        var store = Store();
        await store.UpsertAsync(Label(HashA, "Games", "Hades"), [1f, 0f], CancellationToken.None);
        await store.UpsertAsync(Label(HashB, "Browser", null), [0f, 1f], CancellationToken.None);

        await store.DeleteAsync(HashA, CancellationToken.None);

        Assert.Equal([HashB], await store.ListFileHashesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Collections_DoNotMix()
    {
        await Store("old").UpsertAsync(Label(HashA, "Games", "Hades"), [1f, 0f], CancellationToken.None);

        var current = Store("new");

        Assert.Equal(0, await current.CountAsync(CancellationToken.None));
        Assert.Empty(await current.SearchAsync([1f, 0f], 5, CancellationToken.None));
    }

    [Fact]
    public async Task Examples_SurviveANewInstance()
    {
        await Store().UpsertAsync(Label(HashA, "Games", "Hades"), [1f, 0f], CancellationToken.None);

        Assert.Equal(1, await Store().CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WrongVectorLength_IsRefused() =>
        await Assert.ThrowsAsync<ExamplesUnavailableException>(() => Store().UpsertAsync(Label(HashA, "Games", null), [1f, 0f, 0f], CancellationToken.None));

    [Fact]
    public async Task UnusableDatabase_IsUnavailable()
    {
        // A folder where the database file should be: the store cannot open it, whatever the machine.
        var path = Path.Combine(_temp.Path, "folder.db");
        Directory.CreateDirectory(path);
        var store = new SqliteExampleStore(Opt(new PathsOptions { DatabasePath = path }), Opt(new ExamplesOptions()), Opt(new ClipOptions { EmbeddingSize = 2 }));

        await Assert.ThrowsAsync<ExamplesUnavailableException>(() => store.CountAsync(CancellationToken.None));
    }
}

public class ExampleRebuilderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();
    private readonly FakeExampleStore _store = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly FileSystemScreenshotStore _files = new(Opt(new IntakeOptions()), TimeProvider.System);
    private readonly SqliteDecisionLog _log;
    private readonly ExampleRebuilder _rebuilder;

    public ExampleRebuilderTests()
    {
        var paths = new PathsOptions { SortedRoot = _temp.Path, ReviewFolder = "_Review", DatabasePath = Path.Combine(_temp.Path, "db", "test.db") };
        _log = new SqliteDecisionLog(Opt(paths), Opt(new PrivacyOptions()));
        _rebuilder = new ExampleRebuilder(_log, _store, _embedder, _files, Opt(paths), NullLogger<ExampleRebuilder>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private async Task<(string Path, string Hash)> FileWithDecision(string relative, string category, string? sub, DecisionStage stage, int seed)
    {
        var path = Path.Combine(_temp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _temp.File(relative, seed);
        var hash = await _files.ComputeHashAsync(path, CancellationToken.None);
        await Record(path, hash, category, sub, stage);
        return (path, hash);
    }

    private Task Record(string path, string hash, string category, string? sub, DecisionStage stage) => _log.RecordAsync(
        new SortRecord(path, path, hash, new Classification(category, sub, sub ?? category, 1, stage), null, T0), CancellationToken.None);

    [Fact]
    public async Task Correction_MissingFromStore_IsRestoredWithItsLabel()
    {
        var (_, hash) = await FileWithDecision(@"Games\Hades\a.png", "Games", "Hades", DecisionStage.UserCorrection, 10);

        Assert.Equal(1, await _rebuilder.RunAsync(CancellationToken.None));

        var example = _store.Stored[hash];
        Assert.Equal(("Games", "Hades", T0), (example.Category, example.SubFolder, example.CreatedAt));
    }

    [Fact]
    public async Task SecondRun_DoesNotEmbedAgain()
    {
        await FileWithDecision(@"Games\Hades\a.png", "Games", "Hades", DecisionStage.UserCorrection, 10);
        await _rebuilder.RunAsync(CancellationToken.None);

        Assert.Equal(0, await _rebuilder.RunAsync(CancellationToken.None));
        Assert.Equal(1, _embedder.Calls);
    }

    [Fact]
    public async Task AppsOwnDecisions_AreNotExamples()
    {
        await FileWithDecision(@"Games\Hades\a.png", "Games", "Hades", DecisionStage.Rule, 10);

        Assert.Equal(0, await _rebuilder.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ForgottenInReview_StaysForgotten()
    {
        var (path, hash) = await FileWithDecision(@"Games\Hades\a.png", "Games", "Hades", DecisionStage.UserCorrection, 10);
        await Record(path, hash, "_Review", null, DecisionStage.UserCorrection);

        Assert.Equal(0, await _rebuilder.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LatestLabel_Wins()
    {
        var (path, hash) = await FileWithDecision(@"Games\Hades\a.png", "Games", "Hades", DecisionStage.UserCorrection, 10);
        await Record(path, hash, "Games", "Deadlock", DecisionStage.UserCorrection);

        await _rebuilder.RunAsync(CancellationToken.None);

        Assert.Equal("Deadlock", _store.Stored[hash].SubFolder);
    }

    [Fact]
    public async Task FileGone_IsSkipped()
    {
        var (path, _) = await FileWithDecision(@"Games\Hades\a.png", "Games", "Hades", DecisionStage.UserCorrection, 10);
        File.Delete(path);

        Assert.Equal(0, await _rebuilder.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OtherFileAtThatPath_IsSkipped()
    {
        await FileWithDecision(@"Games\Hades\a.png", "Games", "Hades", DecisionStage.UserCorrection, 10);
        _temp.File(@"Games\Hades\a.png", 200);

        Assert.Equal(0, await _rebuilder.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StoreDown_TellsTheCallerToRetry()
    {
        await FileWithDecision(@"Games\Hades\a.png", "Games", "Hades", DecisionStage.UserCorrection, 10);
        _store.Down = true;

        await Assert.ThrowsAsync<ExamplesUnavailableException>(() => _rebuilder.RunAsync(CancellationToken.None));
    }
}
