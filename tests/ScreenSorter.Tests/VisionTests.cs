using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Imaging;
using ScreenSorter.Infrastructure.Vision;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class VisionMapperTests
{
    private static VisionMapper Mapper(params string[] knownGames) =>
        new(Opt(SortPipelineTests.LmStudio()), Opt(DefaultRules()), Opt(new ThresholdsOptions { VisionConfidence = 0.7 }),
            Opt(new PathsOptions { ReviewFolder = "_Review" }), new FixedCatalog(knownGames));

    [Fact]
    public void Map_Game_UsesTitleAndAlias()
    {
        var c = Mapper().Map(new VisionResult("game", "cs2", null, 0.9));

        Assert.Equal(("Games", "Counter-Strike 2", DecisionStage.Vision), (c.Category, c.SubFolder, c.Stage));
    }

    [Fact]
    public void Map_Game_UsesCatalogSpelling() =>
        Assert.Equal("Arknights Endfield", Mapper("Arknights Endfield", "Deadlock").Map(new VisionResult("Game", "Arknights: Endfield", null, 1)).SubFolder);

    [Fact]
    public void Map_Game_NotInCatalog_GoesToReview() =>
        Assert.Equal("_Review", Mapper("Deadlock").Map(new VisionResult("Game", "Mobile Legends: Bang Bang", null, 1)).Category);

    [Fact]
    public void Map_Chat_UsesAppName() =>
        Assert.Equal(("Chat", "Telegram"), Pair(Mapper().Map(new VisionResult("Chat", null, "Telegram", 0.8))));

    [Fact]
    public void Map_Browser_HasNoSubFolder() =>
        Assert.Equal(("Browser", null), Pair(Mapper().Map(new VisionResult("Browser", null, null, 0.8))));

    [Theory]
    [InlineData("Game", null, 0.9)]      // game without title
    [InlineData("Browser", null, 0.5)]   // below threshold
    [InlineData("Spreadsheet", null, 0.99)] // unknown category
    public void Map_Unusable_GoesToReview(string category, string? title, double confidence) =>
        Assert.Equal("_Review", Mapper().Map(new VisionResult(category, title, null, confidence)).Category);

    [Fact]
    public void Map_Null_GoesToReview() => Assert.Equal("_Review", Mapper().Map(null).Category);

    private static (string, string?) Pair(Classification c) => (c.Category, c.SubFolder);
}

public class LmStudioVisionClientTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static LmStudioVisionClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new WicImageInspector(), new FixedCatalog("Deadlock"),
            Opt(new LmStudioOptions
            {
                BaseUrl = "http://localhost:1234/v1/",
                Model = "google/gemma-4-12b-qat",
                TtlSeconds = 300,
                TimeoutSeconds = 5,
                Categories = SortPipelineTests.LmStudio().Categories,
            }),
            NullLogger<LmStudioVisionClient>.Instance);

    [Fact]
    public void BuildRequest_HasImageSchemaTtlAndHint()
    {
        var body = Client(new StubHandler(HttpStatusCode.OK, "")).BuildRequest("QUJD", Focus("Hades.exe", "Hades"), []);

        Assert.Equal("google/gemma-4-12b-qat", body["model"]?.GetValue<string>());
        Assert.Equal(300, body["ttl"]?.GetValue<int>());
        Assert.Equal("json_schema", body["response_format"]?["type"]?.GetValue<string>());
        var enumValues = body["response_format"]!["json_schema"]!["schema"]!["properties"]!["category"]!["enum"]!.AsArray()
            .Select(n => n!.GetValue<string>());
        Assert.Contains("Game", enumValues);
        var content = body["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("data:image/jpeg;base64,QUJD", content[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Contains("Hades.exe", content[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void BuildRequest_KnownGames_RestrictTitleAndAppearInPrompt()
    {
        var body = Client(new StubHandler(HttpStatusCode.OK, "")).BuildRequest("QUJD", null, ["Deadlock", "Dota 2"]);

        var titles = body["response_format"]!["json_schema"]!["schema"]!["properties"]!["game_title"]!["enum"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Deadlock", "Dota 2", ""], titles);
        Assert.Contains("Deadlock; Dota 2", body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void BuildRequest_NoKnownGames_FreeTitle() =>
        Assert.Null(Client(new StubHandler(HttpStatusCode.OK, "")).BuildRequest("QUJD", null, [])
            ["response_format"]!["json_schema"]!["schema"]!["properties"]!["game_title"]!["enum"]);

    [Theory]
    [InlineData("{\"category\":\"Game\",\"game_title\":\"Hades\",\"app_name\":\"\",\"confidence\":0.93}", "Game", "Hades", null, 0.93)]
    [InlineData("```json\n{\"category\":\"Chat\",\"game_title\":\"\",\"app_name\":\"Discord\",\"confidence\":\"0.8\"}\n```", "Chat", null, "Discord", 0.8)]
    [InlineData("{\"category\":\"Other\",\"game_title\":\"null\",\"app_name\":\"\",\"confidence\":7}", "Other", null, null, 1.0)]
    public void Parse_ValidAnswers(string content, string category, string? game, string? app, double confidence)
    {
        var r = Client(new StubHandler(HttpStatusCode.OK, "")).Parse(Completion(content));

        Assert.Equal(new VisionResult(category, game, app, confidence), r);
    }

    [Theory]
    [InlineData("I think this is a game.")]
    [InlineData("{\"game_title\":\"Hades\"}")]
    public void Parse_Garbage_ReturnsNull(string content) =>
        Assert.Null(Client(new StubHandler(HttpStatusCode.OK, "")).Parse(Completion(content)));

    [Fact]
    public async Task Classify_PostsToChatCompletions_AndParses()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            Completion("{\"category\":\"Browser\",\"game_title\":\"\",\"app_name\":\"\",\"confidence\":0.9}"));

        var r = await Client(handler).ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None);

        Assert.Equal("Browser", r?.Category);
        Assert.Equal("http://localhost:1234/v1/chat/completions", handler.LastUri?.ToString());
    }

    [Fact]
    public async Task Classify_ServerError_IsUnavailable() =>
        await Assert.ThrowsAsync<VisionUnavailableException>(() =>
            Client(new StubHandler(HttpStatusCode.NotFound, "{\"error\":\"model not found\"}"))
                .ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None));

    [Fact]
    public async Task Classify_ConnectionRefused_IsUnavailable() =>
        await Assert.ThrowsAsync<VisionUnavailableException>(() =>
            Client(new StubHandler(new HttpRequestException("refused")))
                .ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None));

    private static string Completion(string content) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) }
            .ToJsonString();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body = "";
        private readonly Exception? _error;

        public StubHandler(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        public StubHandler(Exception error) => _error = error;

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            if (_error is not null)
                throw _error;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }
}

public class WicImageInspectorTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void MeasureStdDev_BlackIsZero_NoiseIsHigh()
    {
        var inspector = new WicImageInspector();

        Assert.True(inspector.MeasureStdDev(_temp.Solid("black.png", 0)) < 0.5);
        Assert.True(inspector.MeasureStdDev(_temp.Solid("white.png", 255)) < 0.5);
        Assert.True(inspector.MeasureStdDev(_temp.File("noise.png")) > 20);
    }

    [Fact]
    public void MeasureStdDev_CorruptFile_ThrowsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => new WicImageInspector().MeasureStdDev(_temp.Raw("bad.png", [1, 2, 3])));

    [Fact]
    public void ToJpeg_DownscalesLongSide()
    {
        var jpeg = new WicImageInspector().ToJpeg(_temp.File("big.png", width: 400, height: 200), 100, 85);

        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(jpeg),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((100, 50), (frame.PixelWidth, frame.PixelHeight));
    }
}

public class IdleDetectorTests
{
    private sealed class FakeActivity : ISystemActivity
    {
        public TimeSpan SinceInput { get; set; } = TimeSpan.FromMinutes(10);
        public bool Fullscreen { get; set; }
        public int? Gpu { get; set; } = 5;

        public TimeSpan TimeSinceLastInput() => SinceInput;
        public bool IsFullscreenAppFocused() => Fullscreen;
        public int? GpuUtilizationPercent() => Gpu;
    }

    private static IdleDetector Detector(FakeActivity a) =>
        new(a, Opt(new IdleOptions { NoInputMinutes = 5, MaxGpuUtilPercent = 20, SkipWhenFullscreen = true }));

    [Fact]
    public void Idle_WhenAllConditionsHold() => Assert.True(Detector(new FakeActivity()).Check().IsIdle);

    [Fact]
    public void Busy_RecentInput() =>
        Assert.False(Detector(new FakeActivity { SinceInput = TimeSpan.FromMinutes(1) }).Check().IsIdle);

    [Fact]
    public void Busy_Fullscreen() => Assert.Equal("fullscreen app", Detector(new FakeActivity { Fullscreen = true }).Check().Reason);

    [Fact]
    public void Busy_Gpu_UnlessIgnoredMidBatch()
    {
        var d = Detector(new FakeActivity { Gpu = 90 });

        Assert.False(d.Check().IsIdle);
        Assert.True(d.Check(ignoreGpu: true).IsIdle);
    }

    [Fact]
    public void Idle_WhenGpuUnknown() => Assert.True(Detector(new FakeActivity { Gpu = null }).Check().IsIdle);
}

public class VisionQueueTests
{
    [Fact]
    public async Task NewSnips_GoAheadOfBacklog_InArrivalOrder()
    {
        var q = new VisionQueue();
        q.Enqueue(new VisionWorkItem("old1", null));
        q.Enqueue(new VisionWorkItem("new1", Focus("a.exe")));
        q.Enqueue(new VisionWorkItem("old2", null));
        q.Enqueue(new VisionWorkItem("new2", Focus("b.exe")));

        var order = new List<string>();
        for (var i = 0; i < 4; i++)
            order.Add((await q.DequeueAsync(CancellationToken.None)).Path);

        Assert.Equal(["new1", "new2", "old1", "old2"], order);
    }

    [Fact]
    public async Task Requeue_PutsItemBackAtFront()
    {
        var q = new VisionQueue();
        q.Enqueue(new VisionWorkItem("a", null));
        q.Enqueue(new VisionWorkItem("b", null));

        var first = await q.DequeueAsync(CancellationToken.None);
        q.Requeue(first);

        Assert.Equal("a", (await q.DequeueAsync(CancellationToken.None)).Path);
        Assert.Equal("b", (await q.DequeueAsync(CancellationToken.None)).Path);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task WaitForItem_DoesNotConsume()
    {
        var q = new VisionQueue();
        q.Enqueue(new VisionWorkItem("a", null));

        await q.WaitForItemAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, q.Count);
        Assert.Equal("a", (await q.DequeueAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1))).Path);
    }
}
