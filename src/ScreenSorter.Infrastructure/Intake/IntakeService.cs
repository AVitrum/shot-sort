using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Infrastructure.Intake;

/// <summary>
/// Watches the top level of each watch folder, queues new screenshots and runs them through
/// <see cref="SortPipeline"/> one at a time. A periodic rescan catches events the watcher dropped.
/// </summary>
/// <remarks>
/// Only the top level is watched (<c>IncludeSubdirectories = false</c>) because the category folders
/// live inside the watch folder; sorted files must never be picked up again.
/// Deferred files are remembered for this process lifetime so rescans do not retry them.
/// </remarks>
public sealed class IntakeService(
    SortPipeline pipeline,
    SortActivity activity,
    IOptions<PathsOptions> paths,
    IOptions<IntakeOptions> intake,
    ILogger<IntakeService> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly IntakeOptions _intake = intake.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var folders = paths.Value.WatchFolders.Select(PathsOptions.Expand).Where(f => f.Length > 0).ToList();
        foreach (var folder in folders)
            StartWatcher(folder);

        activity.SortNowRequested += OnSortNow;
        try
        {
            Rescan(folders);
            var rescan = RescanLoopAsync(folders, stoppingToken);
            await ProcessLoopAsync(stoppingToken);
            await rescan;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            activity.SortNowRequested -= OnSortNow;
            foreach (var w in _watchers)
                w.Dispose();
        }

        void OnSortNow(object? sender, EventArgs e) => Rescan(folders);
    }

    private void StartWatcher(string folder)
    {
        if (!Directory.Exists(folder))
        {
            logger.LogWarning("Watch folder {Folder} does not exist; skipped", folder);
            return;
        }

        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => Enqueue(e.FullPath);
        watcher.Renamed += (_, e) => Enqueue(e.FullPath);
        watcher.Error += (_, e) => logger.LogWarning(e.GetException(), "Watcher error on {Folder}; the rescan will catch up", folder);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
        logger.LogInformation("Watching {Folder}", folder);
    }

    private async Task RescanLoopAsync(IReadOnlyList<string> folders, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _intake.RescanIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(ct))
            Rescan(folders);
    }

    private void Rescan(IReadOnlyList<string> folders)
    {
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                    Enqueue(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Rescan of {Folder} failed", folder);
            }
        }
    }

    private void Enqueue(string path)
    {
        if (!_intake.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return;
        lock (_gate)
        {
            if (!_known.Add(path))
                return;
        }
        _queue.Writer.TryWrite(path);
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        int sorted = 0, review = 0;
        while (await _queue.Reader.WaitToReadAsync(ct))
        {
            while (_queue.Reader.TryRead(out var path))
            {
                await activity.WaitWhilePausedAsync(ct);
                var outcome = await pipeline.ProcessAsync(path, ct);
                switch (outcome.Kind)
                {
                    case SortOutcomeKind.Sorted: sorted++; break;
                    case SortOutcomeKind.Review: review++; break;
                }

                // Deferred files stay known so rescans skip them; anything else may come back
                // (a file the user drops into the top level again is a new screenshot).
                if (outcome.Kind != SortOutcomeKind.Deferred)
                {
                    lock (_gate)
                        _known.Remove(path);
                }
            }

            if (sorted + review > 0 && !await MoreArrivesSoonAsync(ct))
            {
                activity.ReportBatch(new BatchSummary(sorted, review));
                sorted = review = 0;
            }
        }
    }

    // Screenshots taken in quick succession should produce one notification, not one each.
    private async Task<bool> MoreArrivesSoonAsync(CancellationToken ct)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, _intake.BatchQuietSeconds)));
        try
        {
            return await _queue.Reader.WaitToReadAsync(quiet.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
