using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Vision;

namespace ScreenSorter.Core.Sorting;

/// <summary>
/// Processes screenshots in two stages. <see cref="ProcessAsync"/> (instant): wait for the file,
/// look up focus, reject black images, apply rules and confirmed examples. <see cref="ProcessQueuedAsync"/>
/// (when idle): ask the vision model. Both end in the same move-rename-log step.
/// </summary>
/// <remarks>
/// Files without focus history (backlog) skip the rules and are judged by image content alone; with
/// <see cref="BacklogOptions.Enabled"/> off they are left in place as <see cref="SortOutcomeKind.Deferred"/>.
/// Files that neither rules nor examples can place, or where the two disagree, stay in place and
/// are returned as <see cref="SortOutcomeKind.Queued"/> for the vision queue.
/// </remarks>
public sealed class SortPipeline(
    IScreenshotStore store,
    IFocusHistory focus,
    RulesEngine rules,
    ExampleClassifier examples,
    IImageInspector images,
    IVisionClassifier vision,
    VisionMapper visionMapper,
    IDecisionLog log,
    IOptions<PathsOptions> paths,
    IOptions<ThresholdsOptions> thresholds,
    IOptions<BacklogOptions> backlog,
    TimeProvider time,
    ILogger<SortPipeline> logger)
{
    private readonly PathsOptions _paths = paths.Value;

    /// <summary>Instant stage for a new file. Never throws for per-file I/O problems; those yield <see cref="SortOutcomeKind.Skipped"/>.</summary>
    /// <param name="path">A file at the top level of a watch folder.</param>
    /// <param name="ct">Cancels processing; throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>What happened; <see cref="SortOutcomeKind.Queued"/> outcomes carry the focus snapshot.</returns>
    public async Task<SortOutcome> ProcessAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!await store.WaitUntilReadyAsync(path, ct))
            {
                logger.LogWarning("Skipped {Path}: missing or still locked", path);
                return new SortOutcome(SortOutcomeKind.Skipped, path);
            }

            var created = store.GetCreationTime(path);
            var snapshot = focus.GetFocusAt(created);
            if (snapshot is null && !backlog.Value.Enabled)
            {
                logger.LogDebug("Deferred {Path}: no focus history at {Created}", path, created);
                return new SortOutcome(SortOutcomeKind.Deferred, path);
            }

            if (IsBlank(path))
                return await ActAsync(path, Classification.Review(_paths.ReviewFolder, DecisionStage.ImageCheck), snapshot, ct);

            var rule = snapshot is null ? null : rules.Classify(snapshot);
            var example = await TryExamplesAsync(path, ct);
            if (rule is not null && (example is null || SameFolder(rule, example)))
                return await ActAsync(path, rule, snapshot, ct);
            if (rule is null && example is not null)
                return await ActAsync(path, example, snapshot, ct);

            // Focus is only a hint: a region snip can show a window that was not focused. On conflict the model decides.
            logger.LogInformation("Queued {Path} for the vision model ({Process}; rule {Rule}, examples {Example})",
                path, snapshot?.ProcessName ?? "backlog", rule?.Category, example?.Category);
            return new SortOutcome(SortOutcomeKind.Queued, path, Focus: snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Skipped {Path}", path);
            return new SortOutcome(SortOutcomeKind.Skipped, path);
        }
    }

    /// <summary>Vision stage for a queued file.</summary>
    /// <param name="item">The queued file and its focus hint.</param>
    /// <param name="ct">Cancels processing; throws <see cref="OperationCanceledException"/>.</param>
    /// <returns><see cref="SortOutcomeKind.Sorted"/>, <see cref="SortOutcomeKind.Review"/>, or <see cref="SortOutcomeKind.Skipped"/> if the file is gone.</returns>
    /// <exception cref="VisionUnavailableException">The model is unavailable; the file was not touched and should be retried later.</exception>
    public async Task<SortOutcome> ProcessQueuedAsync(VisionWorkItem item, CancellationToken ct)
    {
        try
        {
            if (!await store.WaitUntilReadyAsync(item.Path, ct))
            {
                logger.LogInformation("Dropped {Path} from the vision queue: file is gone or locked", item.Path);
                return new SortOutcome(SortOutcomeKind.Skipped, item.Path);
            }

            VisionResult? result;
            try
            {
                result = await vision.ClassifyAsync(item.Path, item.Focus, ct);
            }
            catch (InvalidDataException ex)
            {
                logger.LogWarning(ex, "{Path} is not a decodable image", item.Path);
                result = null;
            }
            logger.LogInformation("Vision answer for {Path}: {Result}", item.Path, (object?)result ?? "unusable");
            return await ActAsync(item.Path, visionMapper.Map(result), item.Focus, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Skipped {Path}", item.Path);
            return new SortOutcome(SortOutcomeKind.Skipped, item.Path);
        }
    }

    private async Task<Classification?> TryExamplesAsync(string path, CancellationToken ct)
    {
        try
        {
            return await examples.TryClassifyAsync(path, ct);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "{Path} is not a decodable image", path);
            return null;
        }
    }

    private static bool SameFolder(Classification a, Classification b) =>
        string.Equals(a.Category, b.Category, StringComparison.OrdinalIgnoreCase)
        && SortedPathBuilder.MatchKey(a.SubFolder ?? "") == SortedPathBuilder.MatchKey(b.SubFolder ?? "");

    private bool IsBlank(string path)
    {
        try
        {
            var stdDev = images.MeasureStdDev(path);
            if (stdDev >= thresholds.Value.UniformMaxStdDev)
                return false;
            logger.LogInformation("{Path} looks blank (std-dev {StdDev:0.00})", path, stdDev);
            return true;
        }
        catch (InvalidDataException ex)
        {
            // Undecodable files would be retried on every rescan; review lets the user decide.
            logger.LogWarning(ex, "{Path} is not a decodable image", path);
            return true;
        }
    }

    private async Task<SortOutcome> ActAsync(string path, Classification classification, FocusSnapshot? snapshot, CancellationToken ct)
    {
        var isReview = string.Equals(classification.Category, _paths.ReviewFolder, StringComparison.OrdinalIgnoreCase);
        if (isReview && !string.IsNullOrEmpty(snapshot?.ProcessName))
        {
            // The process name is a useful hint when the user reviews the file.
            classification = classification with { Name = Path.GetFileNameWithoutExtension(snapshot.ProcessName) };
        }

        var root = PathsOptions.Expand(_paths.SortedRoot);
        var created = store.GetCreationTime(path);
        var captureLocal = CaptureTime.FromFileName(path) ?? created.LocalDateTime;
        var target = SortedPathBuilder.Build(root, classification, captureLocal, Path.GetExtension(path),
            store.ListSubfolders(Path.Combine(root, SortedPathBuilder.Sanitize(classification.Category))));

        var hash = await store.ComputeHashAsync(path, ct);
        var moved = store.Move(path, target);
        await log.RecordAsync(new SortRecord(path, moved, hash, classification, snapshot, time.GetUtcNow()), ct);

        logger.LogInformation("Sorted {Path} -> {Target} ({Process}, {Stage}, {Confidence:0.00})",
            path, moved, snapshot?.ProcessName, classification.Stage, classification.Confidence);
        return new SortOutcome(isReview ? SortOutcomeKind.Review : SortOutcomeKind.Sorted, path, moved, snapshot);
    }
}
