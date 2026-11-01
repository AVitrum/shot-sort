using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Core.Examples;

/// <summary>What a file's location taught the app.</summary>
public enum CorrectionOutcome
{
    /// <summary>Nothing to learn: the file is where the app last recorded it, or it is outside the category folders.</summary>
    Ignored,

    /// <summary>The folder became a confirmed example of this file.</summary>
    Learned,

    /// <summary>The user moved the file into the review folder; its example was removed.</summary>
    Forgotten,

    /// <summary>The store, model or file was not available; check again later.</summary>
    RetryLater,
}

/// <summary>
/// Compares a file's folder with the decision log and learns from the difference.
/// A file the log does not know counts as placed by the user. A file that changed folder is renamed to match its
/// new folder, in place, unless the user gave it a name of their own.
/// </summary>
public sealed class CorrectionProcessor(
    IScreenshotStore files,
    IDecisionLog log,
    IImageEmbedder embedder,
    IExampleStore examples,
    IOptions<PathsOptions> paths,
    IOptions<CorrectionsOptions> corrections,
    TimeProvider time,
    ILogger<CorrectionProcessor> logger)
{
    /// <summary>
    /// Reads the label from a path relative to the sorted root: the first folder is the category, the second the
    /// sub-folder. Deeper folders still belong to that sub-folder.
    /// </summary>
    /// <param name="root">Sorted root.</param>
    /// <param name="path">A file path.</param>
    /// <returns>The label, or <see langword="null"/> for files outside the root or directly in it.</returns>
    public static (string Category, string? SubFolder)? LabelOf(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == path || relative.StartsWith("..", StringComparison.Ordinal))
            return null;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length switch
        {
            < 2 => null,
            2 => (parts[0], null),
            _ => (parts[0], parts[1]),
        };
    }

    /// <summary>Checks one file. Never throws for per-file problems or unavailable dependencies.</summary>
    /// <param name="path">A file under the sorted root.</param>
    /// <param name="ct">Cancels processing; throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>What was learned.</returns>
    public async Task<CorrectionOutcome> ProcessAsync(string path, CancellationToken ct)
    {
        var label = LabelOf(PathsOptions.Expand(paths.Value.SortedRoot), path);
        if (label is null)
            return CorrectionOutcome.Ignored;

        try
        {
            if (!await files.WaitUntilReadyAsync(path, ct))
                return CorrectionOutcome.Ignored;

            var (category, sub) = label.Value;
            var hash = await files.ComputeHashAsync(path, ct);
            var record = await log.FindLatestByHashAsync(hash, ct);
            if (record is not null && SameLabel(record.Classification, category, sub))
                return CorrectionOutcome.Ignored;

            var isReview = string.Equals(category, paths.Value.ReviewFolder, StringComparison.OrdinalIgnoreCase);
            var name = isReview ? Classification.Review(category).Name : sub ?? category;
            var classification = new Classification(category, sub, name, 1.0, DecisionStage.UserCorrection);
            var now = time.GetUtcNow();
            CorrectionOutcome outcome;
            if (isReview)
            {
                // A file nobody labelled has nothing to forget.
                if (record is null)
                    return CorrectionOutcome.Ignored;
                await examples.DeleteAsync(hash, ct);
                outcome = CorrectionOutcome.Forgotten;
            }
            else
            {
                var vector = await embedder.EmbedAsync(path, ct);
                await examples.UpsertAsync(new Example(hash, category, sub, Example.UserSource, now), vector, ct);
                outcome = CorrectionOutcome.Learned;
            }

            // Logged last: if the store failed above, the rescan finds this file again and retries.
            var finalPath = RenameToMatch(path, name);
            await log.RecordAsync(new SortRecord(record?.NewPath ?? path, finalPath, hash, classification, record?.Focus, now), ct);
            logger.LogInformation("{Outcome} {Path}: was {Old}, now {Category}/{Sub}",
                outcome, finalPath, record is null ? "unknown" : $"{record.Classification.Category}/{record.Classification.SubFolder}", category, sub);
            return outcome;
        }
        catch (ExamplesUnavailableException ex)
        {
            logger.LogInformation("Correction for {Path} waits: {Problem}", path, ex.Message);
            return CorrectionOutcome.RetryLater;
        }
        catch (InvalidDataException ex)
        {
            logger.LogDebug(ex, "{Path} is not a decodable image", path);
            return CorrectionOutcome.Ignored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not check {Path}", path);
            return CorrectionOutcome.RetryLater;
        }
    }

    // A name without a known time format is one the user chose, so it stays. Failing to rename costs only the
    // name: the example is already stored, and retrying would learn it again.
    private string RenameToMatch(string path, string name)
    {
        if (!corrections.Value.RenameMovedFiles || CaptureTime.FromFileName(path) is not { } captured)
            return path;

        var target = Path.Combine(Path.GetDirectoryName(path)!, SortedPathBuilder.FileName(name, captured, Path.GetExtension(path)));
        // A file the user has already moved on gets its own check at its new location. Moving it here would also
        // recreate the folder it just left.
        if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return path;
        try
        {
            return files.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not rename {Path}", path);
            return path;
        }
    }

    private static bool SameLabel(Classification known, string category, string? sub) =>
        string.Equals(known.Category, category, StringComparison.OrdinalIgnoreCase)
        && SortedPathBuilder.MatchKey(known.SubFolder ?? "") == SortedPathBuilder.MatchKey(sub ?? "");
}
