using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Imaging;
using ScreenSorter.Infrastructure.Intake;
using ScreenSorter.Infrastructure.Storage;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

/// <summary>The real service on a temp folder: real watcher, file store, WIC imaging and SQLite log.</summary>
public sealed class IntakeServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly TempFolder _temp = new();
    private readonly FocusHistory _focus = new(Opt(new FocusOptions()), new FakeTime(DateTimeOffset.UtcNow.AddMinutes(-5)));
    private readonly SortActivity _activity = new();
    private readonly VisionQueue _queue = new();
    private readonly List<BatchSummary> _batches = [];
    private readonly IntakeService _service;

    public IntakeServiceTests()
    {
        var paths = new PathsOptions
        {
            WatchFolders = [_temp.Path],
            SortedRoot = _temp.Path,
            ReviewFolder = "_Review",
            DatabasePath = Path.Combine(_temp.Path, "db", "test.db"),
        };
        _service = Service(paths);
        _activity.BatchCompleted += (_, summary) =>
        {
            lock (_batches)
                _batches.Add(summary);
        };
    }

    private IntakeService Service(PathsOptions paths)
    {
        var options = Opt(paths);
        var rules = Opt(DefaultRules());
        var thresholds = Opt(new ThresholdsOptions());
        var lmStudio = Opt(SortPipelineTests.LmStudio());
        var store = new FileSystemScreenshotStore(Opt(new IntakeOptions { StabilityPollMilliseconds = 50, StabilityTimeoutSeconds = 5 }), TimeProvider.System);
        var embedder = new FakeEmbedder();
        var pipeline = new SortPipeline(store, _focus,
            new RulesEngine(rules, new GameDetector(rules, new NoTitles(), new FixedCatalog())),
            new ExampleClassifier(embedder, new FakeExampleStore(), Opt(new ExamplesOptions()), thresholds, NullLogger<ExampleClassifier>.Instance),
            new WicImageInspector(), new NoVision(), new VisionMapper(lmStudio, rules, thresholds, options, new FixedCatalog()),
            new SqliteDecisionLog(options, Opt(new PrivacyOptions())), options, thresholds, Opt(new BacklogOptions()),
            new BurstMemory(Opt(new BurstOptions())), embedder, Opt(new BurstOptions { Enabled = false }), lmStudio,
            TimeProvider.System, NullLogger<SortPipeline>.Instance);
        return new IntakeService(pipeline, _activity, _queue, options,
            Opt(new IntakeOptions { Extensions = [".png"], RescanIntervalSeconds = 3600, BatchQuietSeconds = 1 }), NullLogger<IntakeService>.Instance);
    }

    public void Dispose()
    {
        _service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _service.Dispose();
        _temp.Dispose();
    }

    private void FocusOn(string process) => _focus.Record(Focus(process, at: DateTimeOffset.UtcNow.AddMinutes(-1)));

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private string Sorted(params string[] parts) => Path.Combine([_temp.Path, .. parts]);

    private async Task<bool> SortedIntoDiscord(string fileName) => await Eventually(() => File.Exists(Sorted("Chat", "Discord", fileName)));

    [Fact]
    public async Task NewFile_IsSeenByTheWatcherAndSorted()
    {
        FocusOn("Discord.exe");
        await _service.StartAsync(CancellationToken.None);

        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        Assert.True(await SortedIntoDiscord("Discord_2026-10-31_21-04-12.png"));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task RenamedIntoPlace_IsSeen()
    {
        FocusOn("Discord.exe");
        await _service.StartAsync(CancellationToken.None);

        var outside = _temp.File("Screenshot 2026-10-31 210412.tmp");
        File.Move(outside, Sorted("Screenshot 2026-10-31 210412.png"));

        Assert.True(await SortedIntoDiscord("Discord_2026-10-31_21-04-12.png"));
    }

    [Fact]
    public async Task FileThereBeforeStart_IsFoundByTheFirstRescan()
    {
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        await _service.StartAsync(CancellationToken.None);

        Assert.True(await Eventually(() => _queue.Count == 1));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task OtherExtensions_AreLeftAlone()
    {
        var other = _temp.Raw("notes.txt", [1, 2, 3]);
        await _service.StartAsync(CancellationToken.None);
        FocusOn("Discord.exe");
        _temp.File("Screenshot 2026-10-31 210412.png");

        Assert.True(await SortedIntoDiscord("Discord_2026-10-31_21-04-12.png"));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task FileInACategoryFolder_IsNeverPickedUp()
    {
        var folder = Directory.CreateDirectory(Sorted("Code")).FullName;
        var placed = Path.Combine(folder, "mine.png");
        File.Move(_temp.File("mine.png"), placed);

        await _service.StartAsync(CancellationToken.None);
        _activity.RequestSortNow();
        await Task.Delay(500);

        Assert.True(File.Exists(placed));
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task QueuedFile_IsNotQueuedAgainByARescan()
    {
        _temp.File("Screenshot 2026-10-31 210412.png");
        await _service.StartAsync(CancellationToken.None);
        Assert.True(await Eventually(() => _queue.Count == 1));

        _activity.RequestSortNow();
        await Task.Delay(500);

        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task QueuedFile_ComesBackIfTheWorkerFinishedWithoutMovingIt()
    {
        _temp.File("Screenshot 2026-10-31 210412.png");
        await _service.StartAsync(CancellationToken.None);
        Assert.True(await Eventually(() => _queue.Count == 1));
        var item = await _queue.DequeueAsync(CancellationToken.None);

        _queue.MarkCompleted(item);
        _activity.RequestSortNow();

        Assert.True(await Eventually(() => _queue.Count == 1));
    }

    [Fact]
    public async Task Paused_NothingMoves_UntilResumed()
    {
        FocusOn("Discord.exe");
        _activity.Pause();
        var file = _temp.File("Screenshot 2026-10-31 210412.png");
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        Assert.True(File.Exists(file));

        _activity.Resume();

        Assert.True(await SortedIntoDiscord("Discord_2026-10-31_21-04-12.png"));
    }

    [Fact]
    public async Task ShotsInQuickSuccession_GiveOneSummary()
    {
        FocusOn("Discord.exe");
        _activity.Pause();
        _temp.File("Screenshot 2026-10-31 210412.png", seed: 1);
        _temp.File("Screenshot 2026-10-31 210413.png", seed: 2);
        _temp.File("Screenshot 2026-10-31 210414.png", seed: 3);
        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        _activity.Resume();

        Assert.True(await Eventually(() => { lock (_batches) return _batches.Count > 0; }));
        await Task.Delay(1500);
        lock (_batches)
            Assert.Equal(new BatchSummary(3, 0), Assert.Single(_batches));
    }

    [Fact]
    public async Task MissingWatchFolder_IsCreated_SoTheFirstSnipIsSeen()
    {
        var folder = Sorted("created-later");
        using var service = Service(new PathsOptions
        {
            WatchFolders = [folder],
            SortedRoot = _temp.Path,
            ReviewFolder = "_Review",
            DatabasePath = Path.Combine(_temp.Path, "db", "other.db"),
        });

        await service.StartAsync(CancellationToken.None);

        Assert.True(await Eventually(() => Directory.Exists(folder)));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnusableWatchFolder_DoesNotStopTheOthers()
    {
        var blocker = _temp.Raw("not-a-folder.bin", [0]);
        var good = Directory.CreateDirectory(Sorted("good")).FullName;
        using var service = Service(new PathsOptions
        {
            WatchFolders = [Path.Combine(blocker, "cannot-exist"), good],
            SortedRoot = _temp.Path,
            ReviewFolder = "_Review",
            DatabasePath = Path.Combine(_temp.Path, "db", "other.db"),
        });
        FocusOn("Discord.exe");
        await service.StartAsync(CancellationToken.None);

        File.Move(_temp.File("Screenshot 2026-10-31 210412.png"), Path.Combine(good, "Screenshot 2026-10-31 210412.png"));

        Assert.True(await SortedIntoDiscord("Discord_2026-10-31_21-04-12.png"));
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class NoTitles : IGameTitleProvider
    {
        public string? TryGetTitle(string exePath) => null;
    }

    private sealed class NoVision : IVisionClassifier
    {
        public Task<VisionResult?> ClassifyAsync(string imagePath, FocusSnapshot? hint, CancellationToken ct) => Task.FromResult<VisionResult?>(null);

        public Task ReleaseModelAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
