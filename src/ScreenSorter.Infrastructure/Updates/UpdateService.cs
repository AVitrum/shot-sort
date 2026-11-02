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
/// notice: the PC is idle, sorting is not paused or held, and no screenshot waits. A copy that was not installed by the
/// installer, such as a development build, never updates.
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
    private readonly UpdatesOptions _options = options.Value;
    private volatile string? _waitingVersion;

    /// <summary>Raised on a background thread, repeatedly until the app exits, with the path of a verified installer.</summary>
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

    /// <summary>Whether this copy can update itself: switched on, and put here by the installer.</summary>
    public bool IsActive => _options.Enabled && File.Exists(Path.Combine(AppFolder, UninstallerName));

    /// <summary>A downloaded version waiting for a good moment, or <see langword="null"/>.</summary>
    public string? WaitingVersion => _waitingVersion;

    /// <summary>The folder this copy runs from. Overridden by tests.</summary>
    public string AppFolder { get; init; } = AppContext.BaseDirectory;

    /// <summary>Looks once, downloads if newer.</summary>
    /// <param name="ct">Cancels the look.</param>
    /// <returns>The path of a verified installer, or <see langword="null"/> if this copy is up to date.</returns>
    /// <exception cref="UpdateFailedException">The look or the download failed.</exception>
    public async Task<string?> CheckOnceAsync(CancellationToken ct)
    {
        var release = await client.GetLatestAsync(ct);
        if (release is null || release.Version <= CurrentVersion)
            return null;

        logger.LogInformation("Version {New} is available; this is {Current}", release.Version, CurrentVersion);
        var setup = await client.DownloadAsync(release, PathsOptions.Expand(_options.DownloadFolder), ct);
        _waitingVersion = release.Version.ToString(3);
        return setup;
    }

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
            while (true)
            {
                var setup = await TryCheckAsync(stoppingToken);
                if (setup is not null)
                {
                    await OfferUntilExitAsync(setup, stoppingToken);
                    return;
                }
                await Task.Delay(TimeSpan.FromHours(Math.Max(1, _options.CheckIntervalHours)), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<string?> TryCheckAsync(CancellationToken ct)
    {
        try
        {
            return await CheckOnceAsync(ct);
        }
        catch (UpdateFailedException ex)
        {
            logger.LogInformation("No update this time: {Problem}", ex.Message);
            return null;
        }
    }

    private async Task OfferUntilExitAsync(string setup, CancellationToken ct)
    {
        var every = TimeSpan.FromSeconds(Math.Max(1, _options.InstallCheckSeconds));
        while (true)
        {
            if (IsGoodMoment())
                ReadyToInstall?.Invoke(this, setup);
            await Task.Delay(every, time, ct);
        }
    }

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
