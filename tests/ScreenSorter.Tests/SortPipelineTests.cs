using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Storage;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

/// <summary>End-to-end pipeline on a temp folder with the real file store and SQLite log.</summary>
public sealed class SortPipelineTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FocusHistory _focus;
    private readonly SqliteDecisionLog _log;
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
        var store = new FileSystemScreenshotStore(Opt(new IntakeOptions { StabilityPollMilliseconds = 50, StabilityTimeoutSeconds = 5 }), TimeProvider.System);
        _log = new SqliteDecisionLog(paths);
        _pipeline = new SortPipeline(store, _focus,
            new RulesEngine(rules, new GameDetector(rules, new NoTitles())), _log, paths, TimeProvider.System,
            NullLogger<SortPipeline>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Process_MovesRenamesAndLogs()
    {
        _focus.Record(Focus("Discord.exe", at: DateTimeOffset.UtcNow.AddMinutes(-1)));
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
        _focus.Record(Focus("Code.exe", at: DateTimeOffset.UtcNow.AddMinutes(-1)));
        var first = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412.png"), CancellationToken.None);
        var second = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412 (2).png", 32), CancellationToken.None);

        Assert.EndsWith("Code_2026-10-31_21-04-12.png", first.TargetPath);
        Assert.EndsWith("Code_2026-10-31_21-04-12_2.png", second.TargetPath);
    }

    [Fact]
    public async Task Process_UnknownApp_GoesToReview()
    {
        _focus.Record(Focus("notepad.exe", at: DateTimeOffset.UtcNow.AddMinutes(-1)));

        var outcome = await _pipeline.ProcessAsync(_temp.File("Screenshot 2026-10-31 210412.png"), CancellationToken.None);

        Assert.Equal(SortOutcomeKind.Review, outcome.Kind);
        Assert.Equal(Path.Combine(_temp.Path, "_Review", "notepad_2026-10-31_21-04-12.png"), outcome.TargetPath);
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

    private sealed class NoTitles : IGameTitleProvider
    {
        public string? TryGetTitle(string exePath) => null;
    }
}
