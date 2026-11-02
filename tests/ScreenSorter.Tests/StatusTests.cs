using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Status;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Status;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class LmStudioProbeTests
{
    private const string Model = "vendor/model";
    private readonly VisionQueue _queue = new();

    private readonly FakeServerStarter _starter = new();

    private LmStudioProbe Probe(HttpMessageHandler handler) => new(new HttpClient(handler), _queue, _starter,
        Opt(new LmStudioOptions { ManagementUrl = "http://lm.test/api/v1", Model = Model }), Opt(new StatusOptions()));

    [Fact]
    public async Task Unreachable_ButStartable_IsNotAProblem()
    {
        _starter.CanStart = true;
        _queue.Enqueue(new VisionWorkItem("a.png", null));

        var check = await Probe(new Refuse()).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Waiting, check.State);
        Assert.False(check.NeedsAttention);
    }

    [Fact]
    public async Task Unreachable_AfterAFailedStart_IsAProblem()
    {
        _starter.CanStart = true;
        _starter.LastStartFailed = true;
        _queue.Enqueue(new VisionWorkItem("a.png", null));

        var check = await Probe(new Refuse()).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Problem, check.State);
        Assert.True(check.NeedsAttention);
    }

    [Fact]
    public async Task ModelPresent_IsOk()
    {
        var check = await Probe(new Reply(HttpStatusCode.OK, $$"""{"models":[{"key":"other"},{"key":"{{Model}}"}]}""")).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Ok, check.State);
        Assert.Contains(Model, check.Detail);
    }

    [Fact]
    public async Task ModelMissing_IsAProblem()
    {
        var check = await Probe(new Reply(HttpStatusCode.OK, """{"models":[{"key":"other"}]}""")).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Problem, check.State);
        Assert.Contains(Model, check.Detail);
    }

    [Fact]
    public async Task ServerWithoutTheEndpoint_CountsAsRunning()
    {
        var check = await Probe(new Reply(HttpStatusCode.NotFound, "")).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Ok, check.State);
    }

    [Fact]
    public async Task UnexpectedBody_CountsAsRunning()
    {
        var check = await Probe(new Reply(HttpStatusCode.OK, "not json")).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Ok, check.State);
    }

    [Fact]
    public async Task Unreachable_IsAProblem_ButNewsOnlyWhenFilesWait()
    {
        var quiet = await Probe(new Refuse()).CheckAsync(CancellationToken.None);
        Assert.Equal(ServiceState.Problem, quiet.State);
        Assert.False(quiet.NeedsAttention);

        _queue.Enqueue(new VisionWorkItem("a.png", null));
        var waiting = await Probe(new Refuse()).CheckAsync(CancellationToken.None);
        Assert.True(waiting.NeedsAttention);
        Assert.Contains("1 screenshot", waiting.Detail);
    }

    [Fact]
    public async Task NoAnswerInTime_IsAProblem()
    {
        var probe = new LmStudioProbe(new HttpClient(new Hang()), _queue, _starter,
            Opt(new LmStudioOptions { ManagementUrl = "http://lm.test/api/v1", Model = Model }), Opt(new StatusOptions { ProbeTimeoutSeconds = 0 }));

        var check = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Problem, check.State);
    }

    [Fact]
    public async Task Cancelling_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(new Hang()).CheckAsync(cancelled.Token));
    }

    private sealed class Reply(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("refused");
    }

    private sealed class Hang : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }
}

public class ExampleStoreProbeTests
{
    [Fact]
    public async Task Readable_ReportsTheCount()
    {
        var check = await new ExampleStoreProbe(new Store { Count = 3 }).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Ok, check.State);
        Assert.Contains("3 examples", check.Detail);
    }

    [Fact]
    public async Task Unreadable_IsNews()
    {
        var check = await new ExampleStoreProbe(new Store { Down = true }).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Problem, check.State);
        Assert.True(check.NeedsAttention);
        Assert.Null(check.FixCommand);
    }

    private sealed class Store : IExampleStore
    {
        public long Count { get; set; }
        public bool Down { get; set; }

        public Task<long> CountAsync(CancellationToken ct) => Down ? throw new ExamplesUnavailableException("down") : Task.FromResult(Count);
        public Task<IReadOnlySet<string>> ListFileHashesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task UpsertAsync(Example example, float[] vector, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string fileHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExampleHit>> SearchAsync(float[] vector, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
}

public class SortingProbeTests
{
    private readonly VisionQueue _queue = new();
    private readonly SortActivity _activity = new();

    private SortingProbe Probe(TimeSpan sinceInput) => new(_queue, _activity,
        new IdleDetector(new Activity(sinceInput), Opt(new IdleOptions { NoInputMinutes = 5, SkipWhenFullscreen = false })));

    private Task<ServiceCheck> Check() => Probe(TimeSpan.FromHours(1)).CheckAsync(CancellationToken.None);

    [Fact]
    public async Task NothingWaiting_IsOk() => Assert.Equal(ServiceState.Ok, (await Check()).State);

    [Fact]
    public async Task Hold_SaysWhatItWaitsFor()
    {
        _activity.Hold();

        var check = await Check();

        Assert.Equal(ServiceState.Waiting, check.State);
        Assert.Contains("guide", check.Detail);
    }

    [Fact]
    public async Task Paused_SaysSoAndHowToResume()
    {
        _activity.Pause();

        var check = await Check();

        Assert.Equal(ServiceState.Waiting, check.State);
        Assert.Contains("Resume", check.Fix);
    }

    [Fact]
    public async Task FilesWaitingForIdle_SaysWhy()
    {
        _queue.Enqueue(new VisionWorkItem("a.png", null));
        _queue.Enqueue(new VisionWorkItem("b.png", null));

        var check = await Probe(TimeSpan.FromMinutes(1)).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Waiting, check.State);
        Assert.Contains("2 screenshots", check.Detail);
        Assert.Contains("input", check.Detail);
    }

    [Fact]
    public async Task FilesBeingHandled_IsOk()
    {
        _queue.Enqueue(new VisionWorkItem("a.png", null));

        Assert.Equal(ServiceState.Ok, (await Check()).State);
    }

    private sealed class Activity(TimeSpan sinceInput) : ISystemActivity
    {
        public TimeSpan TimeSinceLastInput() => sinceInput;
        public bool IsFullscreenAppFocused() => false;
        public int? GpuUtilizationPercent() => 0;
    }
}

public class WatchFolderProbeTests
{
    [Fact]
    public async Task ExistingFolder_IsOk()
    {
        using var temp = new TempFolder();

        var check = await new WatchFolderProbe(Opt(new PathsOptions { WatchFolders = [temp.Path] })).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Ok, check.State);
    }

    [Fact]
    public async Task MissingFolder_NeedsAttentionAndIsNamed()
    {
        using var temp = new TempFolder();
        var missing = Path.Combine(temp.Path, "gone");

        var check = await new WatchFolderProbe(Opt(new PathsOptions { WatchFolders = [temp.Path, missing] })).CheckAsync(CancellationToken.None);

        Assert.Equal(ServiceState.Problem, check.State);
        Assert.True(check.NeedsAttention);
        Assert.Contains(missing, check.Detail);
    }
}

public class StatusMonitorTests
{
    private static StatusMonitor Monitor(params IServiceProbe[] probes) =>
        new(probes, Opt(new StatusOptions()), NullLogger<StatusMonitor>.Instance);

    [Fact]
    public async Task KeepsResultsInOrder_EvenWhenAProbeThrows()
    {
        var monitor = Monitor(new Fixed(new ServiceCheck("a", ServiceState.Ok, "")), new Throwing(), new Fixed(new ServiceCheck("c", ServiceState.Ok, "")));

        await monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal(["a", "Throwing", "c"], monitor.Latest.Select(c => c.Name));
        Assert.Equal(ServiceState.Problem, monitor.Latest[1].State);
    }

    [Fact]
    public async Task Changed_OnlyWhenAResultChanges()
    {
        var probe = new Fixed(new ServiceCheck("a", ServiceState.Ok, "one"));
        var monitor = Monitor(probe);
        var changes = 0;
        monitor.Changed += (_, _) => changes++;

        await monitor.RefreshAsync(CancellationToken.None);
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, changes);

        probe.Result = probe.Result with { Detail = "two" };
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task AttentionNeeded_OncePerOutage()
    {
        var probe = new Fixed(new ServiceCheck("a", ServiceState.Problem, "down", NeedsAttention: true));
        var monitor = Monitor(probe);
        var alerts = 0;
        monitor.AttentionNeeded += (_, _) => alerts++;

        await monitor.RefreshAsync(CancellationToken.None);
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, alerts);

        probe.Result = new ServiceCheck("a", ServiceState.Ok, "up");
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, alerts);

        probe.Result = new ServiceCheck("a", ServiceState.Problem, "down", NeedsAttention: true);
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, alerts);
    }

    private sealed class Fixed(ServiceCheck result) : IServiceProbe
    {
        public ServiceCheck Result { get; set; } = result;
        public Task<ServiceCheck> CheckAsync(CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class Throwing : IServiceProbe
    {
        public Task<ServiceCheck> CheckAsync(CancellationToken ct) => throw new InvalidOperationException("broken");
    }
}
