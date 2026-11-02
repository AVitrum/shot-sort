using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Core.Examples;

/// <summary>
/// Restores examples that the decision log knows about and the store does not, by embedding the files again. This covers
/// a new <see cref="ExamplesOptions.Collection"/> and an emptied store. A correction is logged only after it was stored,
/// and forgetting one is logged too, so the latest correction of each file is the truth.
/// </summary>
public sealed class ExampleRebuilder(
    IDecisionLog log,
    IExampleStore store,
    IImageEmbedder embedder,
    IScreenshotStore files,
    IOptions<PathsOptions> paths,
    ILogger<ExampleRebuilder> logger)
{
    /// <summary>Stores what is missing. A file that is gone or has changed cannot be restored and is skipped.</summary>
    /// <param name="ct">Cancels the run; throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>How many examples were restored.</returns>
    /// <exception cref="ExamplesUnavailableException">The model or the store cannot be used yet; call again later.</exception>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var stored = await store.ListFileHashesAsync(ct);
        var restored = 0;
        foreach (var entry in await log.ListFilesAsync(int.MaxValue, ct))
        {
            var latest = entry.Latest;
            if (!IsExample(latest.Classification) || stored.Contains(latest.FileHash) || !File.Exists(latest.NewPath))
                continue;

            try
            {
                if (await files.ComputeHashAsync(latest.NewPath, ct) != latest.FileHash)
                    continue;
                var vector = await embedder.EmbedAsync(latest.NewPath, ct);
                var c = latest.Classification;
                await store.UpsertAsync(new Example(latest.FileHash, c.Category, c.SubFolder, Example.UserSource, latest.DecidedAt), vector, ct);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogDebug(ex, "Could not restore the example for {Path}", latest.NewPath);
            }
        }

        if (restored > 0)
            logger.LogInformation("Restored {Count} example(s) from the decision log", restored);
        return restored;
    }

    private bool IsExample(Classification c) =>
        c.Stage == DecisionStage.UserCorrection
        && !string.Equals(c.Category, paths.Value.ReviewFolder, StringComparison.OrdinalIgnoreCase);
}
