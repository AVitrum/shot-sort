using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Vision;

namespace ScreenSorter.Infrastructure.Updates;

/// <summary>
/// Looks for a newer version, downloads and verifies it, and asks the app to install it at a moment nobody will
/// notice: the PC is idle, sorting is not paused or held, and no screenshot waits. The user can also look by hand
/// (<see cref="CheckNowAsync"/>), even with automatic updates off. A copy that was not installed by the installer, such
/// as a development build, never updates.
/// </summary>
public sealed class UpdateService(
    GitHubUpdateClient client,
    IdleDetector idle,
    VisionQueue queue,
    SortActivity activity,
    IOptions<UpdatesOptions> options,
    TimeProvider time,
    ILogger<UpdateService> logger) : BackgroundService
{
    private const string UninstallerName = "unins000.exe";
    private const string NotInstalledMessage = "This copy was not installed, so it cannot update itself.";
    private readonly UpdatesOptions _options = options.Value;
    // One look at a time: two downloads into the same file would spoil each other.
    private readonly SemaphoreSlim _looking = new(1, 1);
    private volatile Waiting? _waiting;

    /// <summary>
    /// Raised on a background thread, repeatedly until the app exits, with the path of a verified installer, whenever
    /// automatic updates are on and it is a good moment to install it.
    /// </summary>
    public event EventHandler<string>? ReadyToInstall;

    /// <summary>The version this copy is, three parts.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, version.Build);
        }
    }

    /// <summary>Whether this copy was put here by the installer, which it needs to replace itself.</summary>
    public bool IsInstalled => File.Exists(Path.Combine(AppFolder, UninstallerName));

    /// <summary>Whether this copy updates by itself: switched on, and put here by the installer.</summary>
    public bool IsActive => _options.Enabled && IsInstalled;

    /// <summary>A downloaded version waiting to be installed, three parts, or <see langword="null"/>.</summary>
    public string? WaitingVersion => _waiting?.Version;

    /// <summary>The path of the verified installer of <see cref="WaitingVersion"/>, or <see langword="null"/>.</summary>
    public string? WaitingSetup => _waiting?.Setup;

    /// <summary>What is known without asking GitHub: not installed, a version ready, or nothing yet.</summary>
    public UpdateCheckResult Current =>
        !IsInstalled ? new(UpdateCheckOutcome.NotInstalled, NotInstalledMessage)
        : _waiting is { } waiting ? Ready(waiting.Version)
        : new(UpdateCheckOutcome.Unknown, "");

    /// <summary>The folder this copy runs from. Overridden by tests.</summary>
    public string AppFolder { get; init; } = AppContext.BaseDirectory;

    /// <summary>Looks once, downloads if newer. Waits for a look already running.</summary>
    /// <param name="ct">Cancels the look.</param>
    /// <returns>The path of a verified installer, or <see langword="null"/> if this copy is up to date.</returns>
    /// <exception cref="UpdateFailedException">The look or the download failed.</exception>
    public async Task<string?> CheckOnceAsync(CancellationToken ct)
    {
        await _looking.WaitAsync(ct);
        try
        {
            var release = await client.GetLatestAsync(ct);
            if (release is null || release.Version <= CurrentVersion)
                return null;

            var version = release.Version.ToString(3);
            if (_waiting is { } waiting && waiting.Version == version && File.Exists(waiting.Setup))
                return waiting.Setup;

            logger.LogInformation("Version {New} is available; this is {Current}", release.Version, CurrentVersion);
            var setup = await client.DownloadAsync(release, PathsOptions.Expand(_options.DownloadFolder), ct);
            _waiting = new Waiting(version, setup);
            return setup;
        }
        finally
        {
            _looking.Release();
        }
    }

    /// <summary>
    /// Looks once because the user asked, even with automatic updates off, and says what was found. Never throws for a
    /// failed look: the result says why. A copy that was not installed asks nothing.
    /// </summary>
    /// <param name="ct">Cancels the look.</param>
    /// <returns>Up to date, ready with the version downloaded and verified, not installed, or failed with the reason.</returns>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct)
    {
        if (!IsInstalled)
            return new(UpdateCheckOutcome.NotInstalled, NotInstalledMessage);
        try
        {
            return await CheckOnceAsync(ct) is null
                ? new(UpdateCheckOutcome.UpToDate, $"You have the latest version, {CurrentVersion.ToString(3)}.")
                : Ready(_waiting!.Version);
        }
        catch (UpdateFailedException ex)
        {
            logger.LogInformation("Update check asked by the user failed: {Problem}", ex.Message);
            return new(UpdateCheckOutcome.Failed, $"Could not check for updates: {ex.Message.TrimEnd('.')}.");
        }
    }

    private static UpdateCheckResult Ready(string version) =>
        new(UpdateCheckOutcome.Ready, $"Version {version} is ready to install.", version);

    /// <summary>Whether installing now would go unnoticed.</summary>
    /// <returns><see langword="true"/> if the PC is idle, sorting runs and nothing waits.</returns>
    public bool IsGoodMoment() => !activity.IsStopped && queue.Count == 0 && idle.Check().IsIdle;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsActive)
            return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.FirstCheckDelaySeconds)), time, stoppingToken);
            RemoveOldDownloads();
            var lookEvery = TimeSpan.FromHours(Math.Max(1, _options.CheckIntervalHours));
            var offerEvery = TimeSpan.FromSeconds(Math.Max(1, _options.InstallCheckSeconds));
            var nextLook = time.GetUtcNow();
            // Wakes often even with nothing waiting, so a version the user found by hand is offered too.
            while (true)
            {
                if (_waiting is null && time.GetUtcNow() >= nextLook)
                {
                    await TryCheckAsync(stoppingToken);
                    nextLook = time.GetUtcNow() + lookEvery;
                }
                if (_waiting is { } waiting && IsGoodMoment())
                    ReadyToInstall?.Invoke(this, waiting.Setup);
                await Task.Delay(offerEvery, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task TryCheckAsync(CancellationToken ct)
    {
        try
        {
            await CheckOnceAsync(ct);
        }
        catch (UpdateFailedException ex)
        {
            logger.LogInformation("No update this time: {Problem}", ex.Message);
        }
    }

    private sealed record Waiting(string Version, string Setup);

    // Installers of versions already running, or abandoned. The current look downloads again if one is still wanted.
    private void RemoveOldDownloads()
    {
        var folder = PathsOptions.Expand(_options.DownloadFolder);
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, "ScreenSorter-*-setup.exe*"))
        {
            try
            {
                if (!IsNewerInstaller(Path.GetFileName(file)))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not delete old download {File}", file);
            }
        }
    }

    private static bool IsNewerInstaller(string fileName)
    {
        var parts = fileName.Split('-');
        return parts.Length >= 2 && Version.TryParse(parts[1], out var version) && version > CurrentVersion;
    }
}
