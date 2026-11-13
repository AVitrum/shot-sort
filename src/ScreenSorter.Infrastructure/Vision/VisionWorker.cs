using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Vision;

namespace ScreenSorter.Infrastructure.Vision;

/// <summary>
/// Drains <see cref="VisionQueue"/> through <see cref="SortPipeline.ProcessQueuedAsync"/> only while the
/// system is idle. "Sort now" ignores the idle rules until the queue is empty. When LM Studio is
/// unavailable the item is put back, its server is started if possible, and otherwise the worker waits
/// <see cref="LmStudioOptions.RetryMinutes"/>. The model is released whenever work stops: queue drained, idle ended,
/// paused, request failed, or shutdown.
/// </summary>
/// <remarks>
/// The GPU condition is only checked before the first item of a batch: the model's own inference
/// keeps the GPU busy, so checking it between items would stop every batch after one file.
/// </remarks>
public sealed class VisionWorker(
    VisionQueue queue,
    SortPipeline pipeline,
    IVisionClassifier vision,
    IVisionServerStarter serverStarter,
    IdleDetector idle,
    SortActivity activity,
    IOptions<IdleOptions> idleOptions,
    IOptions<LmStudioOptions> lmStudio,
    ILogger<VisionWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _force;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!lmStudio.Value.Enabled)
        {
            logger.LogInformation("The vision model is turned off; unplaced files go to review");
            return;
        }

        activity.SortNowRequested += OnSortNow;
        try
        {
            var status = idle.Check();
            logger.LogInformation("Vision worker started; idle now: {Idle} {Reason}", status.IsIdle, status.Reason);
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            activity.SortNowRequested -= OnSortNow;
            await vision.ReleaseModelAsync(CancellationToken.None);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var inBatch = false;
        var lastReason = "";
        // Once per failure: a server that started but still fails must not turn into a tight loop.
        var startTried = false;
        int sorted = 0, review = 0;

        while (true)
        {
            await queue.WaitForItemAsync(ct);
            if (activity.IsStopped)
            {
                inBatch = false;
                await vision.ReleaseModelAsync(ct);
                await activity.WaitWhilePausedAsync(ct);
            }

            if (!_force)
            {
                var status = idle.Check(ignoreGpu: inBatch);
                if (!status.IsIdle)
                {
                    if (status.Reason != lastReason)
                        logger.LogInformation("{Count} file(s) waiting for idle: {Reason}", queue.Count, status.Reason);
                    lastReason = status.Reason;
                    inBatch = false;
                    // The user is back: the model must not hold VRAM while files wait for the next idle period.
                    await vision.ReleaseModelAsync(ct);
                    await SleepAsync(TimeSpan.FromSeconds(Math.Max(5, idleOptions.Value.CheckIntervalSeconds)), ct);
                    continue;
                }
            }
            lastReason = "";

            var item = await queue.DequeueAsync(ct);
            SortOutcome outcome;
            try
            {
                outcome = await pipeline.ProcessQueuedAsync(item, ct);
            }
            catch (VisionUnavailableException ex)
            {
                queue.Requeue(item);
                inBatch = false;
                if (!startTried)
                {
                    startTried = true;
                    if (await serverStarter.TryStartAsync(ct))
                        continue;
                }

                _force = false;
                logger.LogWarning(ex, "Vision model unavailable; retrying in {Minutes} min", lmStudio.Value.RetryMinutes);
                await vision.ReleaseModelAsync(ct);
                await SleepAsync(TimeSpan.FromMinutes(Math.Max(1, lmStudio.Value.RetryMinutes)), ct);
                startTried = false;
                continue;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // An unhandled exception would stop the host and with it every service. Not MarkCompleted: the file
                // stays known to intake, so it is not queued again until the app restarts.
                logger.LogError(ex, "Unexpected error on {Path}; it stays where it is until the app restarts", item.Path);
                if (queue.Count == 0)
                {
                    inBatch = false;
                    await vision.ReleaseModelAsync(ct);
                }
                continue;
            }

            startTried = false;
            queue.MarkCompleted(item);
            inBatch = true;
            if (outcome.Kind == SortOutcomeKind.Sorted) sorted++;
            if (outcome.Kind == SortOutcomeKind.Review) review++;

            if (queue.Count == 0)
            {
                if (sorted + review > 0)
                    activity.ReportBatch(new BatchSummary(sorted, review));
                sorted = review = 0;
                inBatch = false;
                _force = false;
                await vision.ReleaseModelAsync(ct);
            }
        }
    }

    private void OnSortNow(object? sender, EventArgs e)
    {
        // Only covers what is queued now; later files wait for idle again.
        _force = queue.Count > 0;
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    // Waits for the delay, or less if "Sort now" is requested.
    private async Task SleepAsync(TimeSpan delay, CancellationToken ct) => await _wake.WaitAsync(delay, ct);
}
