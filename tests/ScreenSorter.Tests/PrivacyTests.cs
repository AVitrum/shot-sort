using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Infrastructure.Storage;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public sealed class PrivacyTests : IDisposable
{
    private const string SecretTitle = "Private chat with someone-secret-title";
    private const string SecretExe = @"C:\Users\somebody-secret-exe\app.exe";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly PathsOptions _paths;

    public PrivacyTests() => _paths = new PathsOptions
    {
        DatabasePath = Path.Combine(_temp.Path, "db", "p.db"),
        LogFolder = Path.Combine(_temp.Path, "logs"),
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private SqliteDecisionLog Log(int titleDays = 30) => new(Opt(_paths), Opt(new PrivacyOptions { WindowTitleDays = titleDays }));

    private static SortRecord Record(string hash, DateTimeOffset at) => new("a.png", "Chat/a.png", hash,
        new Classification("Chat", null, "Chat", 1, DecisionStage.Rule),
        new FocusSnapshot(at, "Discord.exe", SecretExe, SecretTitle), at);

    private PrivacyMaintenance Maintenance(SqliteDecisionLog log, int titleDays = 30, int logDays = 14) => new(log,
        Opt(new PrivacyOptions { WindowTitleDays = titleDays, LogDays = logDays }), Opt(_paths), new FakeTime(Now), NullLogger<PrivacyMaintenance>.Instance);

    private string DatabaseBytes()
    {
        SqliteConnection.ClearAllPools();
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_paths.DatabasePath)!))
            text.Append(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        return text.ToString();
    }

    [Fact]
    public async Task Stored_ByDefault_SoHistoryCanExplainADecision()
    {
        var log = Log();
        await log.RecordAsync(Record("h", Now), CancellationToken.None);

        var focus = (await log.FindLatestByHashAsync("h", CancellationToken.None))?.Focus;

        Assert.Equal((SecretTitle, SecretExe), (focus?.WindowTitle, focus?.ExePath));
    }

    [Fact]
    public async Task Erase_ClearsOldDetails_KeepsNewOnes_AndKeepsTheRecords()
    {
        var log = Log();
        await log.RecordAsync(Record("old", Now.AddDays(-40)), CancellationToken.None);
        await log.RecordAsync(Record("new", Now.AddDays(-1)), CancellationToken.None);

        var changed = await log.EraseFocusDetailsAsync(Now.AddDays(-30), CancellationToken.None);

        Assert.Equal(1, changed);
        var old = await log.FindLatestByHashAsync("old", CancellationToken.None);
        Assert.NotNull(old);
        Assert.Equal("Discord.exe", old.Focus?.ProcessName);
        Assert.Equal(("", ""), (old.Focus?.WindowTitle, old.Focus?.ExePath));
        Assert.Equal(SecretTitle, (await log.FindLatestByHashAsync("new", CancellationToken.None))?.Focus?.WindowTitle);
    }

    [Fact]
    public async Task Erase_GoesByWhenTheWindowWasSeen_NotWhenTheRecordWasWritten()
    {
        var log = Log();
        // A correction copies the focus of the decision it follows, so its own date says nothing about the title's age.
        await log.RecordAsync(Record("h", Now.AddDays(-40)) with { DecidedAt = Now }, CancellationToken.None);

        Assert.Equal(1, await log.EraseFocusDetailsAsync(Now.AddDays(-30), CancellationToken.None));
    }

    [Fact]
    public async Task WaitingFocus_FollowsTheRetentionSetting()
    {
        var log = Log(titleDays: 0);

        await log.SaveWaitingFocusAsync("h", Record("h", Now).Focus!, CancellationToken.None);

        var focus = await log.FindWaitingFocusAsync("h", CancellationToken.None);
        Assert.Equal(("Discord.exe", "", ""), (focus?.ProcessName, focus?.ExePath, focus?.WindowTitle));
    }

    [Fact]
    public async Task Erase_RemovesOldWaitingFocus_KeepsNewOne()
    {
        var log = Log();
        await log.SaveWaitingFocusAsync("old", Record("old", Now.AddDays(-40)).Focus!, CancellationToken.None);
        await log.SaveWaitingFocusAsync("new", Record("new", Now.AddDays(-1)).Focus!, CancellationToken.None);

        await log.EraseFocusDetailsAsync(Now.AddDays(-30), CancellationToken.None);

        Assert.Null(await log.FindWaitingFocusAsync("old", CancellationToken.None));
        Assert.NotNull(await log.FindWaitingFocusAsync("new", CancellationToken.None));
    }

    [Fact]
    public async Task WaitingFocus_LeavesNoTrace_OnceDecidedAndErased()
    {
        var log = Log();
        await log.SaveWaitingFocusAsync("h", Record("h", Now.AddDays(-40)).Focus!, CancellationToken.None);
        await log.RecordAsync(Record("h", Now.AddDays(-40)), CancellationToken.None);

        await log.EraseFocusDetailsAsync(Now, CancellationToken.None);

        var bytes = DatabaseBytes();
        Assert.DoesNotContain("someone-secret-title", bytes);
        Assert.DoesNotContain("somebody-secret-exe", bytes);
    }

    [Fact]
    public async Task Erase_LeavesNoTraceInTheDatabaseFiles()
    {
        var log = Log();
        await log.RecordAsync(Record("h", Now.AddDays(-40)), CancellationToken.None);
        Assert.Contains("someone-secret-title", DatabaseBytes());

        await log.EraseFocusDetailsAsync(Now, CancellationToken.None);

        var bytes = DatabaseBytes();
        Assert.DoesNotContain("someone-secret-title", bytes);
        Assert.DoesNotContain("somebody-secret-exe", bytes);
    }

    [Fact]
    public async Task Erase_Everything_WorksWithTheLatestPossibleTime()
    {
        var log = Log();
        await log.RecordAsync(Record("h", Now), CancellationToken.None);

        Assert.Equal(1, await log.EraseFocusDetailsAsync(DateTimeOffset.MaxValue, CancellationToken.None));
        Assert.Equal(0, await log.EraseFocusDetailsAsync(DateTimeOffset.MaxValue, CancellationToken.None));
    }

    [Fact]
    public async Task ZeroDays_NeverWritesDetails()
    {
        var log = Log(titleDays: 0);
        await log.RecordAsync(Record("h", Now), CancellationToken.None);

        var focus = (await log.FindLatestByHashAsync("h", CancellationToken.None))?.Focus;

        Assert.Equal(("Discord.exe", "", ""), (focus?.ProcessName, focus?.WindowTitle, focus?.ExePath));
        Assert.DoesNotContain("someone-secret-title", DatabaseBytes());
    }

    [Fact]
    public async Task ErasedRecord_StillListsInHistory()
    {
        var log = Log();
        await log.RecordAsync(Record("h", Now.AddDays(-40)), CancellationToken.None);
        await log.EraseFocusDetailsAsync(Now, CancellationToken.None);

        var entry = Assert.Single(await log.ListFilesAsync(10, CancellationToken.None));

        Assert.Equal("Chat/a.png", entry.Latest.NewPath);
    }

    [Fact]
    public async Task Maintenance_AppliesTheTitleRetention()
    {
        var log = Log();
        await log.RecordAsync(Record("old", Now.AddDays(-31)), CancellationToken.None);
        await log.RecordAsync(Record("new", Now.AddDays(-29)), CancellationToken.None);

        await Maintenance(log).RunOnceAsync(CancellationToken.None);

        Assert.Equal("", (await log.FindLatestByHashAsync("old", CancellationToken.None))?.Focus?.WindowTitle);
        Assert.Equal(SecretTitle, (await log.FindLatestByHashAsync("new", CancellationToken.None))?.Focus?.WindowTitle);
    }

    [Fact]
    public async Task Maintenance_ZeroDays_ErasesWhatWasStoredEarlier()
    {
        await Log().RecordAsync(Record("h", Now.AddDays(-1)), CancellationToken.None);
        var log = Log(titleDays: 0);

        await Maintenance(log, titleDays: 0).RunOnceAsync(CancellationToken.None);

        Assert.Equal("", (await log.FindLatestByHashAsync("h", CancellationToken.None))?.Focus?.WindowTitle);
    }

    [Fact]
    public async Task Maintenance_DeletesOnlyOldLogFiles()
    {
        Directory.CreateDirectory(_paths.LogFolder);
        var old = LogFile("screensorter-2026-10-01.log", Now.AddDays(-15));
        var recent = LogFile("screensorter-2026-10-30.log", Now.AddDays(-13));
        var other = LogFile("notes.log", Now.AddDays(-100));

        await Maintenance(Log()).RunOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task Maintenance_MissingLogFolder_IsFine() =>
        await Maintenance(Log()).RunOnceAsync(CancellationToken.None);

    private string LogFile(string name, DateTimeOffset written)
    {
        var path = Path.Combine(_paths.LogFolder, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }
}
