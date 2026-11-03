using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Status;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Status;
using ScreenSorter.Infrastructure.Updates;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

/// <summary>Answers by URL, like a release host.</summary>
internal sealed class FakeReleases : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Status, byte[] Body)> Routes { get; } = [];
    public List<string> Requested { get; } = [];
    public string? UserAgent { get; private set; }

    public void Add(string url, string body) => Routes[url] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(body));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);
        UserAgent = request.Headers.UserAgent.ToString();
        var (status, body) = Routes.GetValueOrDefault(url, (HttpStatusCode.NotFound, []));
        return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}

internal static class ReleaseFixture
{
    public const string Latest = "https://api.test/repos/o/r/releases/latest";
    public const string SetupUrl = "https://files.test/setup.exe";
    public const string ChecksumUrl = "https://files.test/setup.exe.sha256";
    public const string SignatureUrl = "https://files.test/setup.exe.sha256.sig";

    public static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public static readonly string PublicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    public static GitHubUpdateClient Client(FakeReleases server, string? publicKey = null) =>
        new(new HttpClient(server), Opt(new UpdatesOptions { ApiUrl = "https://api.test/", Repository = "o/r", PublicKey = publicKey ?? PublicKey }));

    public static string Json(string tag, string setupUrl = SetupUrl, string checksumUrl = ChecksumUrl)
    {
        var version = tag.TrimStart('v');
        return $$"""
            {"tag_name":"{{tag}}","assets":[
              {"name":"ScreenSorter-{{version}}-setup.exe","browser_download_url":"{{setupUrl}}"},
              {"name":"ScreenSorter-{{version}}-setup.exe.sha256","browser_download_url":"{{checksumUrl}}"},
              {"name":"ScreenSorter-{{version}}-setup.exe.sha256.sig","browser_download_url":"{{SignatureUrl}}"}]}
            """;
    }

    public static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static string Sign(string text, ECDsa? key = null) =>
        Convert.ToBase64String((key ?? Key).SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));

    /// <summary>Publishes a checksum file and its valid signature.</summary>
    public static void AddChecksum(this FakeReleases server, string checksum)
    {
        server.Add(ChecksumUrl, checksum);
        server.Add(SignatureUrl, Sign(checksum));
    }
}

public sealed class GitHubUpdateClientTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeReleases _server = new();

    public void Dispose() => _temp.Dispose();

    private static UpdateRelease Release() => new(new Version(9, 9, 9), "setup.exe", ReleaseFixture.SetupUrl, ReleaseFixture.ChecksumUrl, ReleaseFixture.SignatureUrl);

    [Fact]
    public async Task Latest_ReadsTheVersionAndAssets_AndIdentifiesItself()
    {
        _server.Add(ReleaseFixture.Latest, ReleaseFixture.Json("v1.2.3"));

        var release = await ReleaseFixture.Client(_server).GetLatestAsync(CancellationToken.None);

        Assert.Equal(new UpdateRelease(new Version(1, 2, 3), "ScreenSorter-1.2.3-setup.exe", ReleaseFixture.SetupUrl, ReleaseFixture.ChecksumUrl, ReleaseFixture.SignatureUrl), release);
        Assert.StartsWith("ScreenSorter", _server.UserAgent);
    }

    [Fact]
    public async Task NothingPublished_IsNotAnError() =>
        Assert.Null(await ReleaseFixture.Client(_server).GetLatestAsync(CancellationToken.None));

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta")]
    [InlineData("latest")]
    public async Task OtherTags_AreIgnored(string tag)
    {
        _server.Add(ReleaseFixture.Latest, ReleaseFixture.Json(tag));

        Assert.Null(await ReleaseFixture.Client(_server).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AssetOverPlainHttp_IsIgnored()
    {
        _server.Add(ReleaseFixture.Latest, ReleaseFixture.Json("v1.2.3", setupUrl: "http://files.test/setup.exe"));

        Assert.Null(await ReleaseFixture.Client(_server).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseWithoutTheChecksum_IsIgnored()
    {
        _server.Add(ReleaseFixture.Latest, """{"tag_name":"v1.2.3","assets":[{"name":"ScreenSorter-1.2.3-setup.exe","browser_download_url":"https://files.test/setup.exe"}]}""");

        Assert.Null(await ReleaseFixture.Client(_server).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseWithoutTheSignature_IsAFailure()
    {
        _server.Add(ReleaseFixture.Latest, """
            {"tag_name":"v1.2.3","assets":[
              {"name":"ScreenSorter-1.2.3-setup.exe","browser_download_url":"https://files.test/setup.exe"},
              {"name":"ScreenSorter-1.2.3-setup.exe.sha256","browser_download_url":"https://files.test/setup.exe.sha256"}]}
            """);

        var failure = await Assert.ThrowsAsync<UpdateFailedException>(() => ReleaseFixture.Client(_server).GetLatestAsync(CancellationToken.None));

        Assert.Contains("not signed", failure.Message);
    }

    private async Task AssertRefused(GitHubUpdateClient client, string expected)
    {
        var folder = Path.Combine(_temp.Path, "updates");

        var failure = await Assert.ThrowsAsync<UpdateFailedException>(() => client.DownloadAsync(Release(), folder, CancellationToken.None));

        Assert.Contains(expected, failure.Message);
        Assert.DoesNotContain(ReleaseFixture.SetupUrl, _server.Requested);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Download_SignedWithAnotherKey_IsRefused()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var checksum = $"{ReleaseFixture.Sha256("evil")}  setup.exe";
        _server.Add(ReleaseFixture.SetupUrl, "evil");
        _server.Add(ReleaseFixture.ChecksumUrl, checksum);
        _server.Add(ReleaseFixture.SignatureUrl, ReleaseFixture.Sign(checksum, attacker));

        await AssertRefused(ReleaseFixture.Client(_server), "signature");
    }

    [Fact]
    public async Task Download_ChecksumChangedAfterSigning_IsRefused()
    {
        _server.Add(ReleaseFixture.SetupUrl, "evil");
        _server.Add(ReleaseFixture.SignatureUrl, ReleaseFixture.Sign($"{ReleaseFixture.Sha256("installer")}  setup.exe"));
        _server.Add(ReleaseFixture.ChecksumUrl, $"{ReleaseFixture.Sha256("evil")}  setup.exe");

        await AssertRefused(ReleaseFixture.Client(_server), "signature");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public async Task Download_UnreadableSignature_IsRefused(string signature)
    {
        _server.Add(ReleaseFixture.SetupUrl, "installer");
        _server.Add(ReleaseFixture.ChecksumUrl, $"{ReleaseFixture.Sha256("installer")}  setup.exe");
        _server.Add(ReleaseFixture.SignatureUrl, signature);

        await AssertRefused(ReleaseFixture.Client(_server), "signature");
    }

    [Fact]
    public async Task Download_OldSignedInstallerUnderANewVersion_IsRefused()
    {
        _server.Add(ReleaseFixture.SetupUrl, "old installer");
        _server.AddChecksum($"{ReleaseFixture.Sha256("old installer")}  ScreenSorter-0.1.0-setup.exe");

        await AssertRefused(ReleaseFixture.Client(_server), "ScreenSorter-0.1.0-setup.exe");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Download_NoKeyConfigured_RefusesWithoutAsking(string key)
    {
        await AssertRefused(ReleaseFixture.Client(_server, key), "no key");

        Assert.Empty(_server.Requested);
    }

    [Fact]
    public async Task Download_UnreadableKeyConfigured_IsRefused()
    {
        _server.Add(ReleaseFixture.SetupUrl, "installer");
        _server.AddChecksum($"{ReleaseFixture.Sha256("installer")}  setup.exe");

        await AssertRefused(ReleaseFixture.Client(_server, "not a key"), "signature");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task BadAnswer_IsAFailure(HttpStatusCode status, string body)
    {
        _server.Routes[ReleaseFixture.Latest] = (status, Encoding.UTF8.GetBytes(body));

        await Assert.ThrowsAsync<UpdateFailedException>(() => ReleaseFixture.Client(_server).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Download_MatchingFile_IsKept()
    {
        _server.Add(ReleaseFixture.SetupUrl, "installer");
        _server.AddChecksum($"{ReleaseFixture.Sha256("installer").ToUpperInvariant()}  setup.exe\r\n");

        var path = await ReleaseFixture.Client(_server).DownloadAsync(Release(), Path.Combine(_temp.Path, "updates"), CancellationToken.None);

        Assert.Equal("installer", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task Download_WrongFile_LeavesNothingBehind()
    {
        _server.Add(ReleaseFixture.SetupUrl, "tampered");
        _server.AddChecksum($"{ReleaseFixture.Sha256("installer")}  setup.exe");
        var folder = Path.Combine(_temp.Path, "updates");

        await Assert.ThrowsAsync<UpdateFailedException>(() => ReleaseFixture.Client(_server).DownloadAsync(Release(), folder, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash setup.exe")]
    public async Task Download_UnreadableChecksum_IsNotInstalled(string checksum)
    {
        _server.Add(ReleaseFixture.SetupUrl, "installer");
        _server.AddChecksum(checksum);

        await Assert.ThrowsAsync<UpdateFailedException>(() => ReleaseFixture.Client(_server).DownloadAsync(Release(), Path.Combine(_temp.Path, "updates"), CancellationToken.None));
    }

    [Fact]
    public async Task Download_FileAlreadyThere_IsNotFetchedAgain()
    {
        _server.Add(ReleaseFixture.SetupUrl, "installer");
        _server.AddChecksum($"{ReleaseFixture.Sha256("installer")}  setup.exe");
        var folder = Path.Combine(_temp.Path, "updates");
        await ReleaseFixture.Client(_server).DownloadAsync(Release(), folder, CancellationToken.None);
        _server.Requested.Clear();

        await ReleaseFixture.Client(_server).DownloadAsync(Release(), folder, CancellationToken.None);

        Assert.Equal([ReleaseFixture.ChecksumUrl, ReleaseFixture.SignatureUrl], _server.Requested);
    }

    [Fact]
    public async Task Download_DamagedFileAlreadyThere_IsReplaced()
    {
        _server.Add(ReleaseFixture.SetupUrl, "installer");
        _server.AddChecksum($"{ReleaseFixture.Sha256("installer")}  setup.exe");
        var folder = Path.Combine(_temp.Path, "updates");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "setup.exe"), "half a download");

        var path = await ReleaseFixture.Client(_server).DownloadAsync(Release(), folder, CancellationToken.None);

        Assert.Equal("installer", File.ReadAllText(path));
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeReleases _server = new();
    private readonly VisionQueue _queue = new();
    private readonly SortActivity _activity = new();
    private readonly Activity _system = new();

    public void Dispose() => _temp.Dispose();

    private UpdateService Service(bool enabled = true) => new(
        ReleaseFixture.Client(_server),
        new IdleDetector(_system, Opt(new IdleOptions { NoInputMinutes = 5, MaxGpuUtilPercent = 100, SkipWhenFullscreen = false })),
        _queue, _activity,
        Opt(new UpdatesOptions { Enabled = enabled, DownloadFolder = Path.Combine(_temp.Path, "updates"), FirstCheckDelaySeconds = 0, InstallCheckSeconds = 1 }),
        TimeProvider.System, NullLogger<UpdateService>.Instance) { AppFolder = _temp.Path };

    private void Publish(string version)
    {
        _server.Add(ReleaseFixture.Latest, ReleaseFixture.Json("v" + version));
        _server.Add(ReleaseFixture.SetupUrl, "installer");
        _server.AddChecksum($"{ReleaseFixture.Sha256("installer")}  ScreenSorter-{version}-setup.exe");
    }

    private void MarkInstalled() => File.WriteAllText(Path.Combine(_temp.Path, "unins000.exe"), "");

    [Fact]
    public async Task NewerVersion_IsDownloaded()
    {
        Publish("99.0.0");
        var service = Service();

        Assert.NotNull(await service.CheckOnceAsync(CancellationToken.None));
        Assert.Equal("99.0.0", service.WaitingVersion);
    }

    [Fact]
    public async Task SameVersion_IsNotDownloaded()
    {
        Publish(UpdateService.CurrentVersion.ToString(3));

        Assert.Null(await Service().CheckOnceAsync(CancellationToken.None));
        Assert.DoesNotContain(ReleaseFixture.SetupUrl, _server.Requested);
    }

    [Fact]
    public async Task OlderVersion_IsNeverInstalled()
    {
        Publish("0.0.1");

        Assert.Null(await Service().CheckOnceAsync(CancellationToken.None));
    }

    [Fact]
    public void CopyWithoutAnUninstaller_DoesNotUpdate() => Assert.False(Service().IsActive);

    [Fact]
    public void InstalledCopy_Updates()
    {
        MarkInstalled();

        Assert.True(Service().IsActive);
    }

    [Fact]
    public void SwitchedOff_DoesNotUpdate()
    {
        MarkInstalled();

        Assert.False(Service(enabled: false).IsActive);
    }

    [Fact]
    public async Task SwitchedOff_SendsNoRequest()
    {
        MarkInstalled();
        var service = Service(enabled: false);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(_server.Requested);
    }

    [Fact]
    public async Task InstalledCopy_OffersTheInstaller_AtAGoodMoment()
    {
        MarkInstalled();
        Publish("99.0.0");
        var service = Service();
        var offered = new TaskCompletionSource<string>();
        service.ReadyToInstall += (_, setup) => offered.TrySetResult(setup);

        await service.StartAsync(CancellationToken.None);
        var setup = await offered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(setup));
    }

    [Fact]
    public async Task UserActive_OffersNothing()
    {
        MarkInstalled();
        Publish("99.0.0");
        _system.SinceInput = TimeSpan.Zero;
        var service = Service();
        var offers = 0;
        service.ReadyToInstall += (_, _) => Interlocked.Increment(ref offers);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(1500);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("99.0.0", service.WaitingVersion);
        Assert.Equal(0, offers);
    }

    [Fact]
    public void GoodMoment_NeedsIdleAndNothingWaitingAndNotPaused()
    {
        var service = Service();
        Assert.True(service.IsGoodMoment());

        _system.SinceInput = TimeSpan.Zero;
        Assert.False(service.IsGoodMoment());
        _system.SinceInput = TimeSpan.FromHours(1);

        _queue.Enqueue(new VisionWorkItem("a.png", null));
        Assert.False(service.IsGoodMoment());
    }

    [Fact]
    public void GoodMoment_NotWhilePaused()
    {
        var service = Service();
        _activity.Pause();

        Assert.False(service.IsGoodMoment());
    }

    [Fact]
    public async Task Offline_KeepsRunning()
    {
        MarkInstalled();
        _server.Routes[ReleaseFixture.Latest] = (HttpStatusCode.InternalServerError, []);
        var service = Service();

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.False(service.ExecuteTask?.IsFaulted ?? false);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Probe_SaysWhatTheUpdaterDoes()
    {
        var options = Opt(new UpdatesOptions { Enabled = true });
        var notInstalled = await new UpdateProbe(Service(), options).CheckAsync(CancellationToken.None);
        Assert.Contains("was not installed", notInstalled.Detail);
        Assert.Equal(ServiceState.Ok, notInstalled.State);

        var off = await new UpdateProbe(Service(enabled: false), Opt(new UpdatesOptions { Enabled = false })).CheckAsync(CancellationToken.None);
        Assert.Contains("off", off.Detail);

        MarkInstalled();
        Publish("99.0.0");
        var service = Service();
        await service.CheckOnceAsync(CancellationToken.None);
        var waiting = await new UpdateProbe(service, options).CheckAsync(CancellationToken.None);
        Assert.Contains("99.0.0", waiting.Detail);
    }

    private sealed class Activity : ISystemActivity
    {
        public TimeSpan SinceInput { get; set; } = TimeSpan.FromHours(1);

        public TimeSpan TimeSinceLastInput() => SinceInput;
        public bool IsFullscreenAppFocused() => false;
        public int? GpuUtilizationPercent() => 0;
    }
}

/// <summary>Runs the real command through <c>cmd</c>, with scripts in place of the installer and the app.</summary>
public sealed class UpdateInstallCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly string _folder;

    public UpdateInstallCommandTests()
    {
        // A space, as in many user names: everything on the command line must stay quoted.
        _folder = Path.Combine(_temp.Path, "with space");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => _temp.Dispose();

    private string Script(string name, string body)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return path;
    }

    private async Task RunAsync(string setup, string app, string appFolder)
    {
        using var process = Process.Start(UpdateInstallCommand.Create(setup, appFolder, app))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
    }

    private string Out(string name) => Path.Combine(_folder, name);

    [Fact]
    public async Task InstallerSucceeds_OldAppIsNotStarted_AndInstallerGetsTheFolder()
    {
        var setup = Script("setup one.cmd", $"echo %*> \"{Out("args.txt")}\"\r\nexit /b 0");
        var app = Script("app.cmd", $"echo started> \"{Out("started.txt")}\"");

        await RunAsync(setup, app, _folder + Path.DirectorySeparatorChar);

        Assert.Equal($"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1 \"/DIR={_folder}\"", File.ReadAllText(Out("args.txt")).Trim());
        Assert.False(File.Exists(Out("started.txt")));
    }

    [Fact]
    public async Task InstallerFails_OldAppIsStartedAgain()
    {
        var setup = Script("setup one.cmd", "exit /b 5");
        var app = Script("app.cmd", $"echo started> \"{Out("started.txt")}\"");

        await RunAsync(setup, app, _folder);

        Assert.True(File.Exists(Out("started.txt")));
    }

    [Fact]
    public void Command_ShowsNoWindow()
    {
        var start = UpdateInstallCommand.Create("s.exe", @"C:\a", @"C:\a\app.exe");

        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
    }
}
