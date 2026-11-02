using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Infrastructure.Vision;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

/// <summary>Runs real processes: small scripts stand in for <c>lms</c>, so the user's LM Studio is never touched.</summary>
public sealed class LmsServerStarterTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Script(string body)
    {
        var path = Path.Combine(_temp.Path, "fake-lms.cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return path;
    }

    // Port 9 (discard) has no listener: the default must not find the user's real LM Studio on 1234 and skip the script.
    private static LmsServerStarter Starter(string tool, string url = "http://localhost:9/v1", bool on = true, int timeout = 30) => new(
        Opt(new LmStudioOptions { CliPath = tool, BaseUrl = url, AutoStartServer = on, StartTimeoutSeconds = timeout }),
        NullLogger<LmsServerStarter>.Instance);

    [Fact]
    public async Task Start_RunsTheToolOnTheConfiguredPort()
    {
        var args = Path.Combine(_temp.Path, "args.txt");
        var starter = Starter(Script($"echo %*> \"{args}\""), "http://localhost:4321/v1");

        Assert.True(await starter.TryStartAsync(CancellationToken.None));

        Assert.Equal("server start --port 4321", File.ReadAllText(args).Trim());
        Assert.False(starter.LastStartFailed);
    }

    [Fact]
    public async Task ServerAlreadyListening_IsLeftAlone()
    {
        var ran = Path.Combine(_temp.Path, "ran.txt");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var starter = Starter(Script($"echo ran> \"{ran}\""), $"http://localhost:{port}/v1");

            Assert.False(await starter.TryStartAsync(CancellationToken.None));

            Assert.False(File.Exists(ran));
            Assert.False(starter.LastStartFailed);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ToolFails_ReportsIt_AndASuccessClearsIt()
    {
        var starter = Starter(Script("exit /b 3"));

        Assert.False(await starter.TryStartAsync(CancellationToken.None));
        Assert.True(starter.LastStartFailed);

        Script("exit /b 0");
        Assert.True(await starter.TryStartAsync(CancellationToken.None));
        Assert.False(starter.LastStartFailed);
    }

    [Fact]
    public async Task ToolHangs_IsStoppedAtTheTimeout()
    {
        var starter = Starter(Script("ping -n 60 127.0.0.1 >nul"), timeout: 1);
        var clock = Stopwatch.StartNew();

        Assert.False(await starter.TryStartAsync(CancellationToken.None));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
        Assert.True(starter.LastStartFailed);
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.CancelAfter(200);
        var starter = Starter(Script("ping -n 60 127.0.0.1 >nul"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter.TryStartAsync(cancelled.Token));
    }

    [Fact]
    public async Task ToolNotInstalled_CannotStart_AndDoesNotThrow()
    {
        var starter = Starter(Path.Combine(_temp.Path, "missing.exe"));

        Assert.False(starter.CanStart);
        Assert.False(await starter.TryStartAsync(CancellationToken.None));
        Assert.False(starter.LastStartFailed);
    }

    [Fact]
    public void SwitchedOff_CannotStart() => Assert.False(Starter(Script("exit /b 0"), on: false).CanStart);

    [Theory]
    [InlineData("http://192.168.1.20:1234/v1", false)]
    [InlineData("not a url", false)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://localhost:1234/v1", true)]
    public void OnlyAServerOnThisMachine_CanBeStarted(string url, bool expected) =>
        Assert.Equal(expected, Starter(Script("exit /b 0"), url).CanStart);
}
