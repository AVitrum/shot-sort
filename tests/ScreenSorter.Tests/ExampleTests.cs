using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Infrastructure.Clip;
using ScreenSorter.Infrastructure.Corrections;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Imaging;
using ScreenSorter.Infrastructure.Qdrant;
using ScreenSorter.Infrastructure.Storage;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class ExampleMatchTests
{
    private static Classification? Match(params ExampleHit[] hits) => ExampleClassifier.Match(hits, 0.9, 0.8);

    [Fact]
    public void NoHits_NoMatch() => Assert.Null(Match());

    [Fact]
    public void AllBelowSimilarity_NoMatch() => Assert.Null(Match(new ExampleHit(0.89, "Games", "A")));

    [Fact]
    public void SingleStrongHit_Matches()
    {
        var c = Match(new ExampleHit(0.95, "Games", "A"));

        Assert.Equal(("Games", "A", "A", 0.95, DecisionStage.Example), (c?.Category, c?.SubFolder, c?.Name, c?.Confidence, c?.Stage));
    }

    [Fact]
    public void WeakHitsDoNotCountAgainstAgreement() =>
        Assert.Equal("A", Match(new ExampleHit(0.95, "Games", "A"), new ExampleHit(0.5, "Games", "B"))?.SubFolder);

    [Fact]
    public void Disagreement_NoMatch() =>
        Assert.Null(Match(new ExampleHit(0.95, "Games", "A"), new ExampleHit(0.94, "Games", "B")));

    [Fact]
    public void FourOfFiveAgree_Matches() =>
        Assert.Equal("A", Match(
            new ExampleHit(0.95, "Games", "A"), new ExampleHit(0.95, "Games", "a"), new ExampleHit(0.94, "Games", "A"),
            new ExampleHit(0.93, "Games", "A"), new ExampleHit(0.92, "Games", "B"))?.SubFolder);

    [Fact]
    public void NoSubFolder_NameIsCategory() => Assert.Equal("Browser", Match(new ExampleHit(0.95, "Browser", null))?.Name);
}

public sealed class CorrectionProcessorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeEmbedder _embedder = new();
    private readonly FakeExampleStore _examples = new();
    private readonly SqliteDecisionLog _log;
    private readonly FileSystemScreenshotStore _files;
    private readonly CorrectionsOptions _corrections = new();
    private readonly CorrectionProcessor _processor;

    public CorrectionProcessorTests()
    {
        var paths = Opt(new PathsOptions
        {
            SortedRoot = _temp.Path,
            ReviewFolder = "_Review",
            DatabasePath = Path.Combine(_temp.Path, "db", "test.db"),
        });
        _log = new SqliteDecisionLog(paths, Opt(new PrivacyOptions()));
        _files = new FileSystemScreenshotStore(Opt(new IntakeOptions { StabilityPollMilliseconds = 50, StabilityTimeoutSeconds = 5 }), TimeProvider.System);
        _processor = new CorrectionProcessor(_files, _log, _embedder, _examples, paths, Opt(_corrections), TimeProvider.System, NullLogger<CorrectionProcessor>.Instance);
    }

    [Theory]
    [InlineData(@"Games\Hades", "Screenshot 2026-10-10 121110.png", @"Games\Deadlock", "Deadlock_2026-10-10_12-11-10.png")]
    [InlineData(@"Games\Hades", "Screenshot 2026-10-10 121110 (2).png", @"Games\Deadlock", "Deadlock_2026-10-10_12-11-10.png")]
    [InlineData(@"Games\Hades", "Hades_2026-10-10_12-11-10.PNG", @"Games\Deadlock", "Deadlock_2026-10-10_12-11-10.png")]
    [InlineData(@"Games\Hades", "Hades_2026-10-10_12-11-10_2.png", "Browser", "Browser_2026-10-10_12-11-10.png")]
    [InlineData(@"Games\Hades", "Hades_2026-10-10_12-11-10.png", "_Review", "Screenshot_2026-10-10_12-11-10.png")]
    [InlineData(@"_Review", "notepad_2026-10-10_12-11-10.png", @"Chat\My App", "My App_2026-10-10_12-11-10.png")]
    public async Task MovedFile_IsRenamedToMatchItsFolder_ThenIgnored(string from, string name, string to, string expected)
    {
        var path = InFolder(from, name);
        var hash = await PlacedByApp(path, from.Split('\\')[0], from.Contains('\\') ? from.Split('\\')[1] : null);
        var moved = Move(path, to, _temp.Path);
        var renamed = Path.Combine(_temp.Path, to, expected);

        await _processor.ProcessAsync(moved, CancellationToken.None);

        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(moved) && !string.Equals(moved, renamed, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(renamed, (await _log.FindLatestByHashAsync(hash, CancellationToken.None))?.NewPath);
        Assert.Contains(renamed, await _log.LatestPathsAsync(CancellationToken.None));

        // The rename raises a watcher event for the new name; it must not count as another correction.
        var examplesBefore = _examples.Stored.Count;
        Assert.Equal(CorrectionOutcome.Ignored, await _processor.ProcessAsync(renamed, CancellationToken.None));
        Assert.Equal(examplesBefore, _examples.Stored.Count);
    }

    [Theory]
    [InlineData("Boss fight.png")]
    [InlineData("Screenshot 2026-10-10 121110 boss fight.png")]
    [InlineData("IMG_0001.png")]
    public async Task NameTheUserChose_IsKept(string name)
    {
        var moved = InFolder(@"Games\Deadlock", name);

        Assert.Equal(CorrectionOutcome.Learned, await _processor.ProcessAsync(moved, CancellationToken.None));
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public async Task RenameCollision_AddsSuffix()
    {
        InFolder("Browser", "Browser_2026-10-10_12-11-10.png", seed: 1);
        var moved = InFolder("Browser", "Screenshot 2026-10-10 121110.png", seed: 2);

        await _processor.ProcessAsync(moved, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_temp.Path, "Browser", "Browser_2026-10-10_12-11-10_2.png")));
    }

    [Fact]
    public async Task RenameOff_KeepsName()
    {
        _corrections.RenameMovedFiles = false;
        var moved = InFolder(@"Games\Deadlock", "Screenshot 2026-10-10 121110.png");

        Assert.Equal(CorrectionOutcome.Learned, await _processor.ProcessAsync(moved, CancellationToken.None));
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public async Task HandSortedDeeperFolder_StaysThere()
    {
        var moved = InFolder(@"Games\Deadlock\Bosses", "Screenshot 2026-10-10 121110.png");

        await _processor.ProcessAsync(moved, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_temp.Path, "Games", "Deadlock", "Bosses", "Deadlock_2026-10-10_12-11-10.png")));
    }

    [Fact]
    public async Task StoreDown_DoesNotRenameYet()
    {
        var moved = InFolder(@"Games\Deadlock", "Screenshot 2026-10-10 121110.png");
        _examples.Down = true;

        Assert.Equal(CorrectionOutcome.RetryLater, await _processor.ProcessAsync(moved, CancellationToken.None));
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public async Task FileMovedAwayDuringCheck_DoesNotRecreateItsFolder()
    {
        var moved = InFolder(@"Games\Deadlock", "Screenshot 2026-10-10 121110.png");
        var away = Path.Combine(_temp.Path, "away.png");
        var folder = Path.GetDirectoryName(moved)!;
        var store = new MovesAwayWhileHashingStore(_files, () =>
        {
            File.Move(moved, away);
            Directory.Delete(folder);
        });
        var processor = new CorrectionProcessor(store, _log, _embedder, _examples,
            Opt(new PathsOptions { SortedRoot = _temp.Path, ReviewFolder = "_Review" }), Opt(_corrections), TimeProvider.System, NullLogger<CorrectionProcessor>.Instance);

        await processor.ProcessAsync(moved, CancellationToken.None);

        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(away));
    }

    private sealed class MovesAwayWhileHashingStore(IScreenshotStore inner, Action moveAway) : IScreenshotStore
    {
        public Task<bool> WaitUntilReadyAsync(string path, CancellationToken ct) => inner.WaitUntilReadyAsync(path, ct);
        public DateTimeOffset GetCreationTime(string path) => inner.GetCreationTime(path);
        public IReadOnlyList<string> ListSubfolders(string folder) => inner.ListSubfolders(folder);
        public string Move(string source, string desiredTarget) => inner.Move(source, desiredTarget);

        public async Task<string> ComputeHashAsync(string path, CancellationToken ct)
        {
            var hash = await inner.ComputeHashAsync(path, ct);
            moveAway();
            return hash;
        }
    }

    [Fact]
    public async Task LockedFile_StillLearns_KeepsNameAndCurrentPath()
    {
        var moved = InFolder(@"Games\Deadlock", "Screenshot 2026-10-10 121110.png");
        var store = new RenameFailingStore(_files);
        var processor = new CorrectionProcessor(store, _log, _embedder, _examples,
            Opt(new PathsOptions { SortedRoot = _temp.Path, ReviewFolder = "_Review" }), Opt(_corrections), TimeProvider.System, NullLogger<CorrectionProcessor>.Instance);

        Assert.Equal(CorrectionOutcome.Learned, await processor.ProcessAsync(moved, CancellationToken.None));

        Assert.True(File.Exists(moved));
        Assert.Contains(moved, await _log.LatestPathsAsync(CancellationToken.None));
    }

    private sealed class RenameFailingStore(IScreenshotStore inner) : IScreenshotStore
    {
        public Task<bool> WaitUntilReadyAsync(string path, CancellationToken ct) => inner.WaitUntilReadyAsync(path, ct);
        public DateTimeOffset GetCreationTime(string path) => inner.GetCreationTime(path);
        public Task<string> ComputeHashAsync(string path, CancellationToken ct) => inner.ComputeHashAsync(path, ct);
        public IReadOnlyList<string> ListSubfolders(string folder) => inner.ListSubfolders(folder);
        public string Move(string source, string desiredTarget) => throw new IOException("locked");
    }

    public void Dispose() => _temp.Dispose();

    private string InFolder(string relativeFolder, string name = "a.png", int seed = 1)
    {
        var folder = Path.Combine(_temp.Path, relativeFolder);
        Directory.CreateDirectory(folder);
        var source = _temp.File(Guid.NewGuid().ToString("N") + ".png", seed);
        var target = Path.Combine(folder, name);
        File.Move(source, target);
        return target;
    }

    private async Task<string> PlacedByApp(string path, string category, string? sub)
    {
        var hash = await _files.ComputeHashAsync(path, CancellationToken.None);
        await _log.RecordAsync(new SortRecord(@"C:\old.png", path, hash,
            new Classification(category, sub, sub ?? category, 1, DecisionStage.Rule), Focus("x.exe"), T0), CancellationToken.None);
        return hash;
    }

    private static string Move(string path, string relativeFolder, string root)
    {
        var folder = Path.Combine(root, relativeFolder);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(path));
        File.Move(path, target);
        return target;
    }

    [Theory]
    [InlineData(@"C:\Root\a.png", null, null)]
    [InlineData(@"C:\Root\Code\a.png", "Code", null)]
    [InlineData(@"C:\Root\Games\Hades\a.png", "Games", "Hades")]
    [InlineData(@"C:\Root\Games\Hades\Bosses\a.png", "Games", "Hades")]
    [InlineData(@"C:\Other\Games\a.png", null, null)]
    [InlineData(@"D:\Root\Games\a.png", null, null)]
    public void LabelOf_ReadsFolders(string path, string? category, string? sub) =>
        Assert.Equal(category is null ? null : (category, sub), CorrectionProcessor.LabelOf(@"C:\Root", path));

    [Fact]
    public async Task AppPlacedFile_InItsFolder_IsIgnored()
    {
        var path = InFolder(@"Games\Hades");
        await PlacedByApp(path, "Games", "Hades");

        Assert.Equal(CorrectionOutcome.Ignored, await _processor.ProcessAsync(path, CancellationToken.None));
        Assert.Empty(_examples.Stored);
    }

    [Fact]
    public async Task MovedToAnotherFolder_Learns_ThenIsIgnored()
    {
        var path = InFolder(@"Games\Hades");
        var hash = await PlacedByApp(path, "Games", "Hades");
        var moved = Move(path, @"Games\Deadlock", _temp.Path);

        Assert.Equal(CorrectionOutcome.Learned, await _processor.ProcessAsync(moved, CancellationToken.None));
        Assert.Equal(("Games", "Deadlock", Example.UserSource), (_examples.Stored[hash].Category, _examples.Stored[hash].SubFolder, _examples.Stored[hash].Source));
        var record = await _log.FindLatestByHashAsync(hash, CancellationToken.None);
        Assert.Equal((DecisionStage.UserCorrection, moved), (record?.Classification.Stage, record?.NewPath));

        Assert.Equal(CorrectionOutcome.Ignored, await _processor.ProcessAsync(moved, CancellationToken.None));
    }

    [Fact]
    public async Task MovedOutOfReview_Learns()
    {
        var path = InFolder("_Review");
        var hash = await PlacedByApp(path, "_Review", null);
        var moved = Move(path, "Browser", _temp.Path);

        Assert.Equal(CorrectionOutcome.Learned, await _processor.ProcessAsync(moved, CancellationToken.None));
        Assert.Equal("Browser", _examples.Stored[hash].Category);
    }

    [Fact]
    public async Task MovedIntoReview_Forgets()
    {
        var path = InFolder(@"Games\Hades");
        var hash = await PlacedByApp(path, "Games", "Hades");
        var moved = Move(path, "_Review", _temp.Path);

        Assert.Equal(CorrectionOutcome.Forgotten, await _processor.ProcessAsync(moved, CancellationToken.None));
        Assert.Equal([hash], _examples.Deleted);
    }

    [Fact]
    public async Task UnknownFile_InCategoryFolder_Learns()
    {
        Assert.Equal(CorrectionOutcome.Learned, await _processor.ProcessAsync(InFolder(@"Games\Hades"), CancellationToken.None));
        Assert.Single(_examples.Stored);
    }

    [Fact]
    public async Task UnknownFile_InReview_IsIgnored() =>
        Assert.Equal(CorrectionOutcome.Ignored, await _processor.ProcessAsync(InFolder("_Review"), CancellationToken.None));

    [Fact]
    public async Task FileInRoot_IsIgnored() =>
        Assert.Equal(CorrectionOutcome.Ignored, await _processor.ProcessAsync(_temp.File("a.png"), CancellationToken.None));

    [Fact]
    public async Task StoreDown_RetriesLater_WithoutLogging_ThenLearns()
    {
        var path = InFolder(@"Games\Hades");
        var hash = await PlacedByApp(path, "Games", "Hades");
        var moved = Move(path, @"Games\Deadlock", _temp.Path);
        _examples.Down = true;

        Assert.Equal(CorrectionOutcome.RetryLater, await _processor.ProcessAsync(moved, CancellationToken.None));
        Assert.Equal("Hades", (await _log.FindLatestByHashAsync(hash, CancellationToken.None))?.Classification.SubFolder);

        _examples.Down = false;
        Assert.Equal(CorrectionOutcome.Learned, await _processor.ProcessAsync(moved, CancellationToken.None));
    }

    [Fact]
    public async Task LatestPaths_HasOnlyTheNewestPathPerFile()
    {
        var path = InFolder(@"Games\Hades");
        await PlacedByApp(path, "Games", "Hades");
        var moved = Move(path, @"Games\Deadlock", _temp.Path);
        await _processor.ProcessAsync(moved, CancellationToken.None);

        var latest = await _log.LatestPathsAsync(CancellationToken.None);

        Assert.Contains(moved.ToUpperInvariant(), latest);
        Assert.DoesNotContain(path, latest);
    }

    [Fact]
    public async Task Watcher_SeesAMoveAndFolderRename()
    {
        var path = InFolder(@"Games\Hades", seed: 1);
        await PlacedByApp(path, "Games", "Hades");
        var activity = new SortActivity();
        var watcher = new CorrectionWatcher(_processor, _log, activity,
            Opt(new PathsOptions { SortedRoot = _temp.Path }),
            Opt(new IntakeOptions()), Opt(new CorrectionsOptions { SettleSeconds = 0, RescanMinutes = 60 }),
            TimeProvider.System, NullLogger<CorrectionWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            // Lets the first tick's rescan finish, so what follows can only be seen through events.
            await Task.Delay(1500);
            Assert.Empty(_examples.Stored);

            Move(path, @"Games\Deadlock", _temp.Path);
            await WaitForAsync(() => _examples.Stored.Count == 1);

            Directory.Move(Path.Combine(_temp.Path, "Games", "Deadlock"), Path.Combine(_temp.Path, "Games", "Dota"));
            await WaitForAsync(() => _examples.Stored.Values.Single().SubFolder == "Dota");
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(100);
        }
    }
}

public class QdrantExampleStoreTests
{
    private const string Hash = "fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40";

    private sealed class Stub(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private int _next;
        public Exception? Error { get; init; }
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Error is not null)
                throw Error;
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var (status, body) = replies[Math.Min(_next++, replies.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static QdrantExampleStore Store(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Opt(new QdrantOptions { Url = "http://localhost:6333/", Collection = "shots" }), Opt(new ClipOptions { EmbeddingSize = 2 }));

    [Fact]
    public async Task Count_ReadsResult_AndMissingCollectionIsZero()
    {
        Assert.Equal(7, await Store(new Stub((HttpStatusCode.OK, "{\"result\":{\"count\":7},\"status\":\"ok\"}"))).CountAsync(CancellationToken.None));
        Assert.Equal(0, await Store(new Stub((HttpStatusCode.NotFound, "{}"))).CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_CreatesMissingCollectionOnce_ThenWritesPoint()
    {
        var stub = new Stub((HttpStatusCode.NotFound, "{}"), (HttpStatusCode.OK, "{}"), (HttpStatusCode.OK, "{}"), (HttpStatusCode.OK, "{}"));
        var store = Store(stub);
        var example = new Example(Hash, "Games", "Hades", Example.UserSource, T0);

        await store.UpsertAsync(example, [1f, 0f], CancellationToken.None);
        await store.UpsertAsync(example, [1f, 0f], CancellationToken.None);

        Assert.Equal(["/collections/shots", "/collections/shots", "/collections/shots/points?wait=true", "/collections/shots/points?wait=true"],
            stub.Requests.Select(r => r.Url));
        Assert.Equal(HttpMethod.Put, stub.Requests[1].Method);
        Assert.Equal(2, JsonNode.Parse(stub.Requests[1].Body)!["vectors"]!["size"]!.GetValue<int>());
        var point = JsonNode.Parse(stub.Requests[2].Body)!["points"]![0]!;
        Assert.Equal(QdrantExampleStore.PointId(Hash), point["id"]!.GetValue<string>());
        Assert.Equal("Hades", point["payload"]!["sub_folder"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_ParsesPoints_IncludingNullSubFolder()
    {
        var store = Store(new Stub((HttpStatusCode.OK,
            "{\"result\":{\"points\":[{\"id\":\"x\",\"version\":1,\"score\":0.99,\"payload\":{\"category\":\"Games\",\"sub_folder\":\"Hades\"}}," +
            "{\"id\":\"y\",\"version\":1,\"score\":0.5,\"payload\":{\"category\":\"Browser\",\"sub_folder\":null}}]},\"status\":\"ok\"}")));

        var hits = await store.SearchAsync([1f, 0f], 5, CancellationToken.None);

        Assert.Equal([new ExampleHit(0.99, "Games", "Hades"), new ExampleHit(0.5, "Browser", null)], hits);
    }

    [Fact]
    public async Task Delete_MissingCollection_IsFine() =>
        await Store(new Stub((HttpStatusCode.NotFound, "{}"))).DeleteAsync(Hash, CancellationToken.None);

    [Fact]
    public async Task ServerError_IsUnavailable() =>
        await Assert.ThrowsAsync<ExamplesUnavailableException>(() => Store(new Stub((HttpStatusCode.InternalServerError, "boom"))).CountAsync(CancellationToken.None));

    [Fact]
    public async Task ConnectionRefused_IsUnavailable() =>
        await Assert.ThrowsAsync<ExamplesUnavailableException>(() => Store(new Stub { Error = new HttpRequestException("refused") }).CountAsync(CancellationToken.None));

    [Fact]
    public async Task MalformedResponse_IsUnavailable() =>
        await Assert.ThrowsAsync<ExamplesUnavailableException>(() => Store(new Stub((HttpStatusCode.OK, "{\"result\":{}}"))).CountAsync(CancellationToken.None));

    [Fact]
    public void PointId_IsAStableUuid()
    {
        Assert.True(Guid.TryParse(QdrantExampleStore.PointId(Hash), out _));
        Assert.Equal(QdrantExampleStore.PointId(Hash), QdrantExampleStore.PointId(Hash));
    }
}

public class ClipPreprocessingTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ToTensorData_IsPlanarAndNormalized()
    {
        byte[] rgb = [255, 0, 51, 0, 255, 102];

        var data = ClipImageEmbedder.ToTensorData(rgb, 1, [0f, 0.5f, 0f], [1f, 0.5f, 0.2f]);
        Assert.Equal([1f, -1f, 1f], data.Select(v => MathF.Round(v, 3)));

        var two = ClipImageEmbedder.ToTensorData([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], 2, [0f, 0f, 0f], [1f, 1f, 1f]);
        // All reds first, then greens, then blues.
        Assert.Equal([1, 4, 7, 10, 2, 5, 8, 11, 3, 6, 9, 12], two.Select(v => (int)MathF.Round(v * 255)));
    }

    [Fact]
    public void Normalize_GivesUnitLength()
    {
        Assert.Equal([0.6f, 0.8f], ClipImageEmbedder.Normalize([3f, 4f]));
        Assert.Equal([0f, 0f], ClipImageEmbedder.Normalize([0f, 0f]));
    }

    [Theory]
    [InlineData(400, 200)]
    [InlineData(200, 400)]
    [InlineData(32, 32)]
    [InlineData(1000, 3)]
    public void ToCenterCropRgb_ReturnsSquareOfRequestedSize(int width, int height) =>
        Assert.Equal(224 * 224 * 3, new WicImageInspector().ToCenterCropRgb(_temp.File("a.png", width: width, height: height), 224).Length);

    [Fact]
    public void ToCenterCropRgb_KeepsTheCenter()
    {
        // 300x100: gray 50 everywhere except a brighter middle third, which is exactly the crop.
        var pixels = new byte[300 * 100];
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 300; x++)
                pixels[y * 300 + x] = (byte)(x is >= 100 and < 200 ? 200 : 50);
        }

        var rgb = new WicImageInspector().ToCenterCropRgb(_temp.Gray("a.png", 300, 100, pixels), 100);

        Assert.True(rgb[1 * 100 * 3 + 50 * 3] > 150, "center of the crop should be bright");
    }

    [Fact]
    public void ToCenterCropRgb_CorruptFile_ThrowsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => new WicImageInspector().ToCenterCropRgb(_temp.Raw("bad.png", [1, 2, 3]), 224));
}
