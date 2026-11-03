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
    private readonly BacklogOptions _backlog = new();
    private readonly BurstOptions _burst = new() { Enabled = false };
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
        _log = new SqliteDecisionLog(paths, Opt(new PrivacyOptions()));
        var examples = new ExampleClassifier(_embedder, _examples, Opt(new ExamplesOptions()), thresholds, NullLogger<ExampleClassifier>.Instance);
        _pipeline = new SortPipeline(store, _focus,
            new RulesEngine(rules, new GameDetector(rules, new NoTitles(), new FixedCatalog())), examples, new WicImageInspector(), _vision,
            new VisionMapper(Opt(LmStudio()), rules, thresholds, paths, new FixedCatalog("Hades", "Deadlock")), _log, paths, thresholds, Opt(_backlog),
            new BurstMemory(Opt(_burst)), _embedder, Opt(_burst), TimeProvider.System,
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
    public async Task Process_LeavesAFileAloneAfterAnUndo()
    {
        FocusOn("Discord.exe");
        var file = _temp.File("Screenshot 2026-10-31 210412.png");
        var hash = await new FileSystemScreenshotStore(Opt(new IntakeOptions()), TimeProvider.System).ComputeHashAsync(file, CancellationToken.None);
        await _log.RecordAsync(new SortRecord("was", file, hash, new Classification("", null, "", 0, DecisionStage.Undone), null, DateTimeOffset.UtcNow), CancellationToken.None);

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Deferred, outcome.Kind);
        Assert.True(File.Exists(file));
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
    public async Task Process_ExampleGoodEnoughAlone_DoesNotOutvoteARule()
    {
        FocusOn("Code.exe");
        _examples.Hits.Add(new ExampleHit(0.86, "Chat", "Discord"));
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Sorted, outcome.Kind);
        Assert.Equal(Path.Combine(_temp.Path, "Code", "Code_2026-10-31_21-04-12.png"), outcome.TargetPath);
    }

    [Fact]
    public async Task Process_ExampleGoodEnoughAlone_PlacesAFileNoRuleCovers()
    {
        _examples.Hits.Add(new ExampleHit(0.86, "Games", "Deadlock"));
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal(Path.Combine(_temp.Path, "Games", "Deadlock", "Deadlock_2026-10-31_21-04-12.png"), outcome.TargetPath);
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
    public async Task Process_BacklogDisabled_IsDeferredAndLeftInPlace()
    {
        _backlog.Enabled = false;
        var file = BacklogFile();

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Deferred, outcome.Kind);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Process_Backlog_NoExample_GoesToVisionQueueWithoutHint()
    {
        var file = BacklogFile();

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal((SortOutcomeKind.Queued, (FocusSnapshot?)null), (outcome.Kind, outcome.Focus));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Process_Backlog_ConfidentExample_SortsWithoutTheModel()
    {
        _examples.Hits.Add(new ExampleHit(0.97, "Games", "Deadlock"));

        var outcome = await _pipeline.ProcessAsync(BacklogFile(), CancellationToken.None);

        Assert.Equal(Path.Combine(_temp.Path, "Games", "Deadlock", "Deadlock_2026-10-10_12-11-10.png"), outcome.TargetPath);
        Assert.Null(_vision.LastHint);
    }

    [Fact]
    public async Task Process_Backlog_BlackImage_GoesToReview()
    {
        var file = _temp.Solid("Screenshot 2026-10-10 121110.png", 0);
        File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-20));

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal(Path.Combine(_temp.Path, "_Review", "Screenshot_2026-10-10_12-11-10.png"), outcome.TargetPath);
    }

    [Fact]
    public async Task ProcessQueued_Backlog_SortsByTheModelsAnswer()
    {
        _vision.Result = new VisionResult("Game", "Hades", null, 0.95);

        var outcome = await _pipeline.ProcessQueuedAsync(new VisionWorkItem(BacklogFile(), null), CancellationToken.None);

        Assert.Equal(Path.Combine(_temp.Path, "Games", "Hades", "Hades_2026-10-10_12-11-10.png"), outcome.TargetPath);
    }

    private static Task<string> HashOf(string file) =>
        new FileSystemScreenshotStore(Opt(new IntakeOptions()), TimeProvider.System).ComputeHashAsync(file, CancellationToken.None);

    [Fact]
    public async Task Process_Queued_SavesTheHintForARestart()
    {
        FocusOn("Code.exe");
        _examples.Hits.Add(new ExampleHit(0.97, "Chat", "Discord"));
        var file = _temp.File("Screenshot 2026-10-31 210412.png");

        await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal("Code.exe", (await _log.FindWaitingFocusAsync(await HashOf(file), CancellationToken.None))?.ProcessName);
    }

    [Fact]
    public async Task Process_AfterARestart_WaitingFileKeepsItsHint()
    {
        // Older than the focus history, as every file is after a restart: only the saved hint can supply the window.
        var file = BacklogFile();
        var saved = Focus("Slack.exe", at: DateTimeOffset.UtcNow.AddDays(-20));
        await _log.SaveWaitingFocusAsync(await HashOf(file), saved, CancellationToken.None);

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.Equal("Slack.exe", outcome.Focus?.ProcessName);
    }

    [Fact]
    public async Task Process_AfterARestart_SavedHintCountsWithBacklogOff()
    {
        _backlog.Enabled = false;
        var file = BacklogFile();
        await _log.SaveWaitingFocusAsync(await HashOf(file), Focus("Slack.exe", at: DateTimeOffset.UtcNow.AddDays(-20)), CancellationToken.None);

        var outcome = await _pipeline.ProcessAsync(file, CancellationToken.None);

        Assert.NotEqual(SortOutcomeKind.Deferred, outcome.Kind);
    }

    [Fact]
    public async Task ProcessQueued_Decided_RemovesTheSavedHint()
    {
        var file = BacklogFile();
        var hash = await HashOf(file);
        var focus = Focus("Slack.exe", at: DateTimeOffset.UtcNow.AddDays(-20));
        await _log.SaveWaitingFocusAsync(hash, focus, CancellationToken.None);
        _vision.Result = new VisionResult("Game", "Hades", "", 0.95);

        await _pipeline.ProcessQueuedAsync(new VisionWorkItem(file, focus), CancellationToken.None);

        Assert.Null(await _log.FindWaitingFocusAsync(hash, CancellationToken.None));
    }

    private string BacklogFile()
    {
        var file = _temp.File("Screenshot 2026-10-10 121110.png");
        File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-20));
        return file;
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

    private async Task<SortOutcome> Queued(string name, int seed, FocusSnapshot? focus = null) =>
        await _pipeline.ProcessQueuedAsync(new VisionWorkItem(_temp.File(name, seed), focus), CancellationToken.None);

    private async Task<SortOutcome> PlacedByModel(string name, int seed, FocusSnapshot? focus = null)
    {
        _burst.Enabled = true;
        _vision.Result = new VisionResult("Browser", "", "", 0.9);
        return await Queued(name, seed, focus);
    }

    [Fact]
    public async Task Burst_NearIdenticalShot_CopiesTheAnswerWithoutTheModel()
    {
        await PlacedByModel("Screenshot 2026-10-31 210412.png", 1);

        var outcome = await Queued("Screenshot 2026-10-31 210500.png", 2);

        Assert.Equal(1, _vision.Calls);
        Assert.Equal(SortOutcomeKind.Sorted, outcome.Kind);
        Assert.StartsWith(Path.Combine(_temp.Path, "Browser"), outcome.TargetPath);
        var hash = await new FileSystemScreenshotStore(Opt(new IntakeOptions()), TimeProvider.System).ComputeHashAsync(outcome.TargetPath!, CancellationToken.None);
        Assert.Equal(DecisionStage.Burst, (await _log.FindLatestByHashAsync(hash, CancellationToken.None))?.Classification.Stage);
    }

    [Fact]
    public async Task Burst_CopiesAreNotCopied_SoOneAnswerCannotCarryForever()
    {
        await PlacedByModel("Screenshot 2026-10-31 210000.png", 1);
        await Queued("Screenshot 2026-10-31 210400.png", 2);

        // Within the window of the copy, but not of the model's answer.
        await Queued("Screenshot 2026-10-31 210800.png", 3);

        Assert.Equal(2, _vision.Calls);
    }

    [Fact]
    public async Task Burst_OutsideTheWindow_AsksTheModel()
    {
        await PlacedByModel("Screenshot 2026-10-31 210000.png", 1);

        await Queued("Screenshot 2026-10-31 211000.png", 2);

        Assert.Equal(2, _vision.Calls);
    }

    [Fact]
    public async Task Burst_AnotherAppInFocus_AsksTheModel()
    {
        await PlacedByModel("Screenshot 2026-10-31 210000.png", 1, Focus("notepad.exe"));

        await Queued("Screenshot 2026-10-31 210010.png", 2, Focus("mspaint.exe"));

        Assert.Equal(2, _vision.Calls);
    }

    [Fact]
    public async Task Burst_DifferentPicture_AsksTheModel()
    {
        await PlacedByModel("Screenshot 2026-10-31 210000.png", 1);
        _embedder.Answer = _ => [0, 1];

        await Queued("Screenshot 2026-10-31 210010.png", 2);

        Assert.Equal(2, _vision.Calls);
    }

    [Fact]
    public async Task Burst_ReviewAnswer_IsNotCopied()
    {
        _burst.Enabled = true;
        _vision.Result = null;
        await Queued("Screenshot 2026-10-31 210000.png", 1);

        await Queued("Screenshot 2026-10-31 210010.png", 2);

        Assert.Equal(2, _vision.Calls);
    }

    [Fact]
    public async Task Burst_AnswerTheUserCorrected_IsNotCopied()
    {
        var first = await PlacedByModel("Screenshot 2026-10-31 210000.png", 1);
        var hash = await new FileSystemScreenshotStore(Opt(new IntakeOptions()), TimeProvider.System).ComputeHashAsync(first.TargetPath!, CancellationToken.None);
        await _log.RecordAsync(new SortRecord(first.TargetPath!, first.TargetPath!, hash, new Classification("Chat", null, "Chat", 1, DecisionStage.UserCorrection), null, DateTimeOffset.UtcNow), CancellationToken.None);

        await Queued("Screenshot 2026-10-31 210010.png", 2);

        Assert.Equal(2, _vision.Calls);
    }

    [Fact]
    public async Task Burst_NoEmbedding_AsksTheModel()
    {
        await PlacedByModel("Screenshot 2026-10-31 210000.png", 1);
        _embedder.Answer = _ => throw new ExamplesUnavailableException("no model");

        var outcome = await Queued("Screenshot 2026-10-31 210010.png", 2);

        Assert.Equal(2, _vision.Calls);
        Assert.Equal(SortOutcomeKind.Sorted, outcome.Kind);
    }

    [Fact]
    public async Task Burst_Off_NeverEmbeds()
    {
        _vision.Result = new VisionResult("Browser", "", "", 0.9);
        await Queued("Screenshot 2026-10-31 210000.png", 1);
        await Queued("Screenshot 2026-10-31 210010.png", 2);

        Assert.Equal((2, 0), (_vision.Calls, _embedder.Calls));
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
        public int Calls { get; private set; }

        public Task<VisionResult?> ClassifyAsync(string imagePath, FocusSnapshot? hint, CancellationToken ct)
        {
            Calls++;
            LastHint = hint;
            return Throw ? throw new VisionUnavailableException("down") : Task.FromResult(Result);
        }

        public Task ReleaseModelAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
