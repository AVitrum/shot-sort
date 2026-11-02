using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Infrastructure.Corrections;

/// <summary>
/// Watches the category folders under the sorted root recursively and passes files to
/// <see cref="CorrectionProcessor"/> after <see cref="CorrectionsOptions.SettleSeconds"/>. A rescan at start and every
/// <see cref="CorrectionsOptions.RescanMinutes"/> catches dropped events, moves made while the app was off, and
/// corrections that could not be stored earlier, and restores examples missing from the store. Files directly in the
/// root belong to intake and are skipped.
/// </summary>
public sealed class CorrectionWatcher(
    CorrectionProcessor processor,
    ExampleRebuilder rebuilder,
    IDecisionLog log,
    SortActivity activity,
    IOptions<PathsOptions> paths,
    IOptions<IntakeOptions> intake,
    IOptions<CorrectionsOptions> options,
    TimeProvider time,
    ILogger<CorrectionWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.OrdinalIgnoreCase);
    // Files checked this run with nothing to learn. Saves re-hashing them on every rescan; events bypass this.
    private readonly HashSet<string> _settled = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root = PathsOptions.Expand(paths.Value.SortedRoot);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "Sorted root {Root} cannot be used; corrections are not watched", _root);
            return;
        }

        using var watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => OnChanged(e.FullPath);
        watcher.Renamed += (_, e) => OnChanged(e.FullPath);
        watcher.Error += (_, e) => logger.LogWarning(e.GetException(), "Correction watcher error; the rescan will catch up");
        watcher.EnableRaisingEvents = true;

        var rescanEvery = TimeSpan.FromMinutes(Math.Max(1, options.Value.RescanMinutes));
        var nextRescan = time.GetUtcNow();
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await activity.WaitWhilePausedAsync(stoppingToken);
                try
                {
                    if (time.GetUtcNow() >= nextRescan)
                    {
                        await RescanAsync(stoppingToken);
                        nextRescan = time.GetUtcNow() + rescanEvery;
                    }
                    foreach (var path in TakeDue())
                        await CheckAsync(path, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Learning is optional; a broken log or store must not stop the host and with it all sorting.
                    logger.LogError(ex, "Correction pass failed; trying again at the next rescan");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void OnChanged(string path)
    {
        try
        {
            // Moving or renaming a whole folder raises one event for the folder, none for its files.
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    Schedule(file, fromEvent: true);
            }
            else
            {
                Schedule(path, fromEvent: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not enumerate {Path}", path);
        }
    }

    private async Task RescanAsync(CancellationToken ct)
    {
        var known = await log.LatestPathsAsync(ct);
        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!known.Contains(file))
                    Schedule(file, fromEvent: false);
            }
        }

        try
        {
            await rebuilder.RunAsync(ct);
        }
        catch (ExamplesUnavailableException ex)
        {
            logger.LogInformation("Restoring examples waits: {Problem}", ex.Message);
        }
    }

    private void Schedule(string path, bool fromEvent)
    {
        if (!intake.Value.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            || CorrectionProcessor.LabelOf(_root, path) is null)
            return;

        lock (_gate)
        {
            if (fromEvent)
                _settled.Remove(path);
            else if (_settled.Contains(path) || _pending.ContainsKey(path))
                return;
            _pending[path] = time.GetUtcNow() + TimeSpan.FromSeconds(options.Value.SettleSeconds);
        }
    }

    private List<string> TakeDue()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var due = _pending.Where(p => p.Value <= now).Select(p => p.Key).ToList();
            foreach (var path in due)
                _pending.Remove(path);
            return due;
        }
    }

    private async Task CheckAsync(string path, CancellationToken ct)
    {
        // RetryLater stays unmarked, so the next rescan picks the file up again.
        if (await processor.ProcessAsync(path, ct) == CorrectionOutcome.Ignored)
        {
            lock (_gate)
                _settled.Add(path);
        }
    }
}
