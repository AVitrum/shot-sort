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
using ScreenSorter.Infrastructure.Storage;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

/// <summary>End-to-end pipeline on a temp folder with the real file store, WIC imaging and SQLite log.</summary>
public sealed class SortPipelineTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FocusHistory _focus;
    private readonly SqliteDecisionLog _log;
    private readonly FakeVision _vision = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly FakeExampleStore _examples = new();
    private readonly SortPipeline _pipeline;

    public SortPipelineTests()
    {
        // Tracking starts in the past so freshly created test files are covered.
        var clock = new FakeTime(DateTimeOffset.UtcNow.AddMinutes(-5));
        _focus = new FocusHistory(Opt(new FocusOptions()), clock);

        var paths = Opt(new PathsOptions
        {
            SortedRoot = _temp.Path,
            ReviewFolder = "_Review",
            DatabasePath = Path.Combine(_temp.Path, "db", "test.db"),
        });
        var rules = Opt(DefaultRules());
        var thresholds = Opt(new ThresholdsOptions());
        var store = new FileSystemScreenshotStore(Opt(new IntakeOptions { StabilityPollMilliseconds = 50, StabilityTimeoutSeconds = 5 }), TimeProvider.System);
        _log = new SqliteDecisionLog(paths);
        var examples = new ExampleClassifier(_embedder, _examples, Opt(new QdrantOptions()), thresholds, NullLogger<ExampleClassifier>.Instance);
        _pipeline = new SortPipeline(store, _focus,
            new RulesEngine(rules, new GameDetector(rules, new NoTitles())), examples, new WicImageInspector(), _vision,
            new VisionMapper(Opt(LmStudio()), rules, thresholds, paths, new FixedCatalog("Hades", "Deadlock")), _log, paths, thresholds, TimeProvider.System,
            NullLogger<SortPipeline>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Process_MovesRenamesAndLogs()
    {
        FocusOn("Discord.exe");
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        var expected = Path.Combine(_temp.Path, "Chat", "Discord", "Discord_2026-10-31_21-04-12.png");
        Assert.Equal(SortOutcomeKind.Sorted, outcome.Kind);
        Assert.Equal(expected, outcome.TargetPath);
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(file));

        var hash = await new FileSystemScreenshotStore(Opt(new IntakeOptions()), TimeProvider.System)
            .ComputeHashAsync(expected, CancellationToken.None);
        var record = await _log.FindLatestByHashAsync(hash, CancellationToken.None);
        Assert.Equal(("Chat", "Discord", DecisionStage.Rule, "Discord.exe"),
            (record?.Classification.Category, record?.Classification.SubFolder, record?.Classification.Stage, record?.Focus?.ProcessName));
    }

    [Fact]
    public async Task Process_NameCollision_AddsSuffix()
    {
        FocusOn("Code.exe");
        var first = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412.png", seed: 1), CancellationToken.None);
        var second = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412 (2).png", seed: 2), CancellationToken.None);

        Assert.EndsWith("Code_2026-10-31_21-04-12.png", first.TargetPath);
        Assert.EndsWith("Code_2026-10-31_21-04-12_2.png", second.TargetPath);
    }

    [Fact]
    public async Task Process_UnknownApp_IsQueuedAndLeftInPlace()
    {
        FocusOn("notepad.exe");
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Queued, outcome.Kind);
        Assert.Equal("notepad.exe", outcome.Focus?.ProcessName);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Process_NoExamplesStored_DoesNotLoadTheModel()
    {
        FocusOn("Code.exe");

        await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412.png"), CancellationToken.None);

        Assert.Equal(0, _embedder.Calls);
    }

    [Fact]
    public async Task Process_NoRule_ConfidentExample_SortsByExample()
    {
        FocusOn("notepad.exe");
        _examples.Hits.Add(new ExampleHit(0.97, "Games", "Deadlock"));

        var outcome = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412.png"), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Sorted, outcome.Kind);
        Assert.Equal(Path.Combine(_temp.Path, "Games", "Deadlock", "Deadlock_2026-10-31_21-04-12.png"), outcome.TargetPath);
    }

    [Fact]
    public async Task Process_RuleAgreesWithExample_SortsByRule()
    {
        FocusOn("Discord.exe");
        _examples.Hits.Add(new ExampleHit(0.97, "chat", "DISCORD"));

        var outcome = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412.png"), CancellationToken.None);

        Assert.Equal(Path.Combine(_temp.Path, "Chat", "Discord", "Discord_2026-10-31_21-04-12.png"), outcome.TargetPath);
    }

    [Fact]
    public async Task Process_RuleConflictsWithExample_GoesToVisionQueue()
    {
        // A region snip of Discord while another app has focus.
        FocusOn("Code.exe");
        _examples.Hits.Add(new ExampleHit(0.97, "Chat", "Discord"));
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Queued, outcome.Kind);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Process_WeakExample_DoesNotConflictWithRule()
    {
        FocusOn("Code.exe");
        _examples.Hits.Add(new ExampleHit(0.5, "Chat", "Discord"));

        var outcome = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412.png"), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Sorted, outcome.Kind);
    }

    [Fact]
    public async Task Process_ExampleStoreDown_RulesStillWork()
    {
        FocusOn("Code.exe");
        _examples.Down = true;

        var outcome = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412.png"), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Sorted, outcome.Kind);
    }

    [Fact]
    public async Task Process_BlackImage_GoesToReviewEvenWhenARuleMatches()
    {
        FocusOn("Code.exe");

        var outcome = await _pipeline.ProcessAsync(_temp.Solid("Screenshot 2026-10-31 210412.png", 0), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Review, outcome.Kind);
        Assert.Equal(Path.Combine(_temp.Path, "_Review", "Code_2026-10-31_21-04-12.png"), outcome.TargetPath);
    }

    [Fact]
    public async Task Process_CorruptImage_GoesToReview()
    {
        FocusOn("Code.exe");

        var outcome = await _pipeline.ProcessAsync(_temp.Raw("Screenshot 2026-10-31 210412.png", [1, 2, 3, 4, 5]), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Review, outcome.Kind);
    }

    [Fact]
    public async Task Process_BacklogFile_IsDeferredAndLeftInPlace()
    {
        var file = _temp.File("Screenshot 2026-10-10 121110.png");
        File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-20));

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Deferred, outcome.Kind);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Process_MissingFile_IsSkipped()
    {
        var outcome = await _pipeline.ProcessAsync(Path.Combine(_temp.Path, "gone.png"), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Skipped, outcome.Kind);
    }

    [Fact]
    public async Task ProcessQueued_GameAnswer_SortsIntoGameFolder()
    {
        _vision.Result = new VisionResult("Game", "Hades", null, 0.95);
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        var outcome = await _pipeline.ProcessQueuedAsync(new VisionWorkItem(file, Focus("Hades.exe")), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Sorted, outcome.Kind);
        Assert.Equal(Path.Combine(_temp.Path, "Games", "Hades", "Hades_2026-10-31_21-04-12.png"), outcome.TargetPath);
        Assert.Equal("Hades.exe", _vision.LastHint?.ProcessName);
    }

    [Fact]
    public async Task ProcessQueued_LowConfidence_GoesToReview()
    {
        _vision.Result = new VisionResult("Browser", null, null, 0.4);

        var outcome = await _pipeline.ProcessQueuedAsync(
            new VisionWorkItem(_temp.File("Screenshot 2026-10-31 210412.png"), Focus("notepad.exe")), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Review, outcome.Kind);
        Assert.EndsWith(Path.Combine("_Review", "notepad_2026-10-31_21-04-12.png"), outcome.TargetPath);
    }

    [Fact]
    public async Task ProcessQueued_ModelUnavailable_Throws_AndLeavesFile()
    {
        _vision.Throw = true;
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        await Assert.ThrowsAsync<VisionUnavailableException>(() =>
            _pipeline.ProcessQueuedAsync(new VisionWorkItem(file, null), CancellationToken.None));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task ProcessQueued_FileGone_IsSkipped()
    {
        var outcome = await _pipeline.ProcessQueuedAsync(
            new VisionWorkItem(Path.Combine(_temp.Path, "gone.png"), null), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Skipped, outcome.Kind);
    }

    internal static LmStudioOptions LmStudio() => new()
    {
        Categories = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Game"] = "Games", ["Chat"] = "Chat", ["Calls"] = "Calls", ["Browser"] = "Browser", ["Other"] = "Other",
        },
    };

    private void FocusOn(string process) => _focus.Record(Focus(process, at: DateTimeOffset.UtcNow.AddMinutes(-1)));

    private sealed class NoTitles : IGameTitleProvider
    {
        public string? TryGetTitle(string exePath) => null;
    }

    private sealed class FakeVision : IVisionClassifier
    {
        public VisionResult? Result { get; set; }
        public bool Throw { get; set; }
        public FocusSnapshot? LastHint { get; private set; }

        public Task<VisionResult?> ClassifyAsync(string imagePath, FocusSnapshot? hint, CancellationToken ct)
        {
            LastHint = hint;
            return Throw ? throw new VisionUnavailableException("down") : Task.FromResult(Result);
        }
    }
}
