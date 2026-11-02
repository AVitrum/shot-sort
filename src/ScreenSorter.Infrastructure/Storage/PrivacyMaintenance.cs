using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Infrastructure.Storage;

/// <summary>
/// Applies the retention settings at start and once a day: erases old window titles and program paths from the
/// decision log, and deletes old log files. Failures are logged and retried at the next pass.
/// </summary>
public sealed class PrivacyMaintenance(
    IDecisionLog log,
    IOptions<PrivacyOptions> privacy,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<PrivacyMaintenance> logger) : BackgroundService
{
    private const string LogFilePattern = "screensorter-*.log";
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>Runs one pass.</summary>
    /// <param name="ct">Cancels the pass.</param>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var erased = await log.EraseFocusDetailsAsync(now - TimeSpan.FromDays(Math.Max(0, privacy.Value.WindowTitleDays)), ct);
        if (erased > 0)
            logger.LogInformation("Erased window details from {Count} old decision(s)", erased);

        DeleteOldLogs(now - TimeSpan.FromDays(Math.Max(1, privacy.Value.LogDays)));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Privacy maintenance failed; trying again tomorrow");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void DeleteOldLogs(DateTimeOffset before)
    {
        var folder = PathsOptions.Expand(paths.Value.LogFolder);
        if (!Directory.Exists(folder))
            return;

        foreach (var file in Directory.EnumerateFiles(folder, LogFilePattern))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < before.UtcDateTime)
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not delete old log {File}", file);
            }
        }
    }
}
