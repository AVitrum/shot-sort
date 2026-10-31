using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;

namespace ScreenSorter.Core.Sorting;

/// <summary>
/// Processes one screenshot: wait for it to be written, look up the focused app, classify,
/// move and rename, and log the decision.
/// </summary>
/// <remarks>
/// Milestone 2 has only the rules stage. Files without focus history (backlog) are left in place
/// as <see cref="SortOutcomeKind.Deferred"/> until an image-based stage exists; files whose focused
/// app matches no rule go to the review folder. See D-003.
/// </remarks>
public sealed class SortPipeline(
    IScreenshotStore store,
    IFocusHistory focus,
    RulesEngine rules,
    IDecisionLog log,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<SortPipeline> logger)
{
    private readonly PathsOptions _paths = paths.Value;

    /// <summary>Processes one file. Never throws for per-file I/O problems; those yield <see cref="SortOutcomeKind.Skipped"/>.</summary>
    /// <param name="path">A file at the top level of a watch folder.</param>
    /// <param name="ct">Cancels processing; throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>What happened to the file.</returns>
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
            if (snapshot is null)
            {
                logger.LogDebug("Deferred {Path}: no focus history at {Created}", path, created);
                return new SortOutcome(SortOutcomeKind.Deferred, path);
            }

            var classification = rules.Classify(snapshot) ?? ReviewFor(snapshot);
            var root = PathsOptions.Expand(_paths.SortedRoot);
            var captureLocal = CaptureTime.FromFileName(path) ?? created.LocalDateTime;
            var target = SortedPathBuilder.Build(root, classification, captureLocal, Path.GetExtension(path),
                store.ListSubfolders(Path.Combine(root, SortedPathBuilder.Sanitize(classification.Category))));

            var hash = await store.ComputeHashAsync(path, ct);
            var moved = store.Move(path, target);
            await log.RecordAsync(new SortRecord(path, moved, hash, classification, snapshot, time.GetUtcNow()), ct);

            logger.LogInformation("Sorted {Path} -> {Target} ({Process}, {Stage})",
                path, moved, snapshot.ProcessName, classification.Stage);
            return new SortOutcome(
                classification.Stage == DecisionStage.Unmatched ? SortOutcomeKind.Review : SortOutcomeKind.Sorted,
                path, moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Skipped {Path}", path);
            return new SortOutcome(SortOutcomeKind.Skipped, path);
        }
    }

    // The process name is a useful hint when the user reviews the file.
    private Classification ReviewFor(FocusSnapshot snapshot) =>
        Classification.Review(_paths.ReviewFolder) with
        {
            Name = string.IsNullOrEmpty(snapshot.ProcessName)
                ? "Screenshot"
                : Path.GetFileNameWithoutExtension(snapshot.ProcessName),
        };
}
