using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Imaging;
using ScreenSorter.Infrastructure.Storage;
using ScreenSorter.Infrastructure.Vision;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public sealed class LmStudioEjectTests : IDisposable
{
    private const string Model = "google/gemma-4-12b-qat";
    private const string Answer = "{\"category\":\"Other\",\"game_title\":\"\",\"app_name\":\"\",\"confidence\":0.9}";
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Fake LM Studio: a chat request loads the model, an unload request removes it.</summary>
    private sealed class FakeServer(bool loadedAtStart = false) : HttpMessageHandler
    {
        public bool Loaded { get; set; } = loadedAtStart;
        public bool FailUnload { get; init; }
        public List<string> Requests { get; } = [];
        public List<string> Unloaded { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            string body;
            switch (path)
            {
                case "/api/v1/models":
                    var instances = Loaded ? new JsonArray(new JsonObject { ["id"] = Model }) : [];
                    body = new JsonObject
                    {
                        ["models"] = new JsonArray(
                            new JsonObject { ["key"] = "other/model", ["loaded_instances"] = new JsonArray(new JsonObject { ["id"] = "other/model" }) },
                            new JsonObject { ["key"] = Model, ["loaded_instances"] = instances }),
                    }.ToJsonString();
                    break;
                case "/api/v1/models/unload":
                    if (FailUnload)
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    var id = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["instance_id"]!.GetValue<string>();
                    Unloaded.Add(id);
                    Loaded = false;
                    body = new JsonObject { ["instance_id"] = id }.ToJsonString();
                    break;
                default:
                    Loaded = true;
                    body = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = Answer } }) }.ToJsonString();
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static LmStudioVisionClient Client(HttpMessageHandler handler, bool eject = true) =>
        new(new HttpClient(handler), new WicImageInspector(), new FixedCatalog(),
            Opt(new LmStudioOptions
            {
                BaseUrl = "http://localhost:1234/v1",
                ManagementUrl = "http://localhost:1234/api/v1/",
                Model = Model,
                EjectAfterBatch = eject,
                Categories = SortPipelineTests.LmStudio().Categories,
            }),
            NullLogger<LmStudioVisionClient>.Instance);

    [Fact]
    public async Task Release_EjectsModelTheBatchLoaded_OnlyThatModel()
    {
        var server = new FakeServer();
        var client = Client(server);

        await client.ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None);
        await client.ClassifyAsync(_temp.File("b.png"), null, CancellationToken.None);
        Assert.True(server.Loaded);
        await client.ReleaseModelAsync(CancellationToken.None);

        Assert.Equal([Model], server.Unloaded);
        Assert.False(server.Loaded);
    }

    [Fact]
    public async Task Release_LeavesModelThatWasAlreadyLoaded()
    {
        var server = new FakeServer(loadedAtStart: true);
        var client = Client(server);

        await client.ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None);
        await client.ReleaseModelAsync(CancellationToken.None);

        Assert.Empty(server.Unloaded);
        Assert.True(server.Loaded);
    }

    [Fact]
    public async Task Release_WithoutAClassification_SendsNothing()
    {
        var server = new FakeServer(loadedAtStart: true);

        await Client(server).ReleaseModelAsync(CancellationToken.None);

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Release_Twice_EjectsOnce_AndNextBatchEjectsAgain()
    {
        var server = new FakeServer();
        var client = Client(server);

        await client.ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None);
        await client.ReleaseModelAsync(CancellationToken.None);
        await client.ReleaseModelAsync(CancellationToken.None);
        Assert.Single(server.Unloaded);

        await client.ClassifyAsync(_temp.File("b.png"), null, CancellationToken.None);
        await client.ReleaseModelAsync(CancellationToken.None);
        Assert.Equal(2, server.Unloaded.Count);
    }

    [Fact]
    public async Task EjectOff_SendsNoManagementRequests()
    {
        var server = new FakeServer();
        var client = Client(server, eject: false);

        await client.ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None);
        await client.ReleaseModelAsync(CancellationToken.None);

        Assert.Equal(["POST /v1/chat/completions"], server.Requests);
    }

    [Fact]
    public async Task UnloadFailure_DoesNotThrow()
    {
        var server = new FakeServer { FailUnload = true };
        var client = Client(server);

        await client.ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None);
        await client.ReleaseModelAsync(CancellationToken.None);

        Assert.True(server.Loaded);
    }
}

/// <summary>A real <see cref="VisionWorker"/> over a real pipeline on a temp folder; only the model and idle state are fake.</summary>
public sealed class VisionWorkerEjectTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeActivity _activity = new();
    private readonly CountingVision _vision = new();
    private readonly VisionQueue _queue = new();
    private readonly SortActivity _sort = new();
    private readonly VisionWorker _worker;

    public VisionWorkerEjectTests()
    {
        var paths = Opt(new PathsOptions { SortedRoot = _temp.Path, ReviewFolder = "_Review", DatabasePath = Path.Combine(_temp.Path, "db", "t.db") });
        var rules = Opt(DefaultRules());
        var thresholds = Opt(new ThresholdsOptions());
        var lmStudio = Opt(new LmStudioOptions { Categories = SortPipelineTests.LmStudio().Categories, RetryMinutes = 1 });
        var store = new FileSystemScreenshotStore(Opt(new IntakeOptions { StabilityPollMilliseconds = 50, StabilityTimeoutSeconds = 5 }), TimeProvider.System);
        var pipeline = new SortPipeline(store, new FocusHistory(Opt(new FocusOptions()), new FakeTime(T0)),
            new RulesEngine(rules, new GameDetector(rules, new FixedTitles())),
            new ExampleClassifier(new FakeEmbedder(), new FakeExampleStore(), Opt(new QdrantOptions()), thresholds, NullLogger<ExampleClassifier>.Instance),
            new WicImageInspector(), _vision, new VisionMapper(lmStudio, rules, thresholds, paths, new FixedCatalog()),
            new SqliteDecisionLog(paths), paths, thresholds, TimeProvider.System, NullLogger<SortPipeline>.Instance);
        _worker = new VisionWorker(_queue, pipeline, _vision,
            new IdleDetector(_activity, Opt(new IdleOptions { NoInputMinutes = 5, MaxGpuUtilPercent = 20 })),
            _sort, Opt(new IdleOptions { CheckIntervalSeconds = 5 }), lmStudio, NullLogger<VisionWorker>.Instance);
    }

    public void Dispose()
    {
        _worker.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task QueueDrained_EjectsOnceAfterTheLastFile()
    {
        _queue.Enqueue(new VisionWorkItem(_temp.File("a.png", 1), null));
        _queue.Enqueue(new VisionWorkItem(_temp.File("b.png", 2), null));

        await _worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => _vision.Releases > 0);

        Assert.Equal(2, _vision.Classified);
        Assert.Equal(1, _vision.Releases);
    }

    [Fact]
    public async Task UserReturnsMidBatch_EjectsAndLeavesTheRestQueued()
    {
        _vision.OnClassified = () => _activity.SinceInput = TimeSpan.Zero;
        _queue.Enqueue(new VisionWorkItem(_temp.File("a.png", 1), null));
        _queue.Enqueue(new VisionWorkItem(_temp.File("b.png", 2), null));

        await _worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => _vision.Releases > 0);

        Assert.Equal(1, _vision.Classified);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task ModelUnavailable_EjectsAndKeepsTheFile()
    {
        _vision.Fail = true;
        _queue.Enqueue(new VisionWorkItem(_temp.File("a.png"), null));

        await _worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => _vision.Releases > 0);

        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Shutdown_Ejects()
    {
        _activity.SinceInput = TimeSpan.Zero;
        await _worker.StartAsync(CancellationToken.None);
        var before = _vision.Releases;

        await _worker.StopAsync(CancellationToken.None);

        Assert.Equal(before + 1, _vision.Releases);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    private sealed class FakeActivity : ISystemActivity
    {
        public TimeSpan SinceInput { get; set; } = TimeSpan.FromHours(1);

        public TimeSpan TimeSinceLastInput() => SinceInput;
        public bool IsFullscreenAppFocused() => false;
        public int? GpuUtilizationPercent() => 0;
    }

    private sealed class FixedTitles : IGameTitleProvider
    {
        public string? TryGetTitle(string exePath) => null;
    }

    private sealed class CountingVision : IVisionClassifier
    {
        public int Classified { get; private set; }
        public int Releases { get; private set; }
        public bool Fail { get; set; }
        public Action? OnClassified { get; set; }

        public Task<VisionResult?> ClassifyAsync(string imagePath, FocusSnapshot? hint, CancellationToken ct)
        {
            if (Fail)
                throw new VisionUnavailableException("down");
            Classified++;
            OnClassified?.Invoke();
            return Task.FromResult<VisionResult?>(new VisionResult("Other", null, null, 0.9));
        }

        public Task ReleaseModelAsync(CancellationToken ct)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }
}
