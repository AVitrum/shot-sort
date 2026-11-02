using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Core.Examples;

/// <summary>Classifies a screenshot by its nearest confirmed examples.</summary>
public sealed class ExampleClassifier(
    IImageEmbedder embedder,
    IExampleStore store,
    IOptions<ExamplesOptions> options,
    IOptions<ThresholdsOptions> thresholds,
    ILogger<ExampleClassifier> logger)
{
    private volatile string _lastProblem = "";

    /// <summary>
    /// Applies the agreement rule: of the hits at or above <see cref="ThresholdsOptions.ExampleSimilarity"/>, the most
    /// common label must hold at least <see cref="ThresholdsOptions.ExampleAgreement"/> of them.
    /// </summary>
    /// <param name="hits">Nearest examples.</param>
    /// <param name="similarity">Minimum similarity for a hit to count.</param>
    /// <param name="agreement">Minimum share of counted hits for the winning label.</param>
    /// <returns>A <see cref="DecisionStage.Example"/> classification with the winners' mean score as confidence, or <see langword="null"/>.</returns>
    public static Classification? Match(IEnumerable<ExampleHit> hits, double similarity, double agreement)
    {
        var counted = hits.Where(h => h.Score >= similarity).ToList();
        if (counted.Count == 0)
            return null;

        var winners = counted
            .GroupBy(h => (Category: h.Category.ToLowerInvariant(), Sub: SortedPathBuilder.MatchKey(h.SubFolder ?? "")))
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(h => h.Score))
            .First()
            .ToList();
        if ((double)winners.Count / counted.Count < agreement)
            return null;

        var best = winners[0];
        return new Classification(best.Category, best.SubFolder, best.SubFolder ?? best.Category,
            winners.Average(h => h.Score), DecisionStage.Example);
    }

    /// <summary>Looks up a file. Never fails because of the stage's own dependencies; rules and vision take over then.</summary>
    /// <param name="imagePath">The screenshot.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>A classification, or <see langword="null"/> if there is no confident match or the stage is unavailable.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="InvalidDataException">The file is not a decodable image.</exception>
    public async Task<Classification?> TryClassifyAsync(string imagePath, CancellationToken ct)
    {
        try
        {
            // Checked first so the model is not loaded while there is nothing to compare with.
            if (await store.CountAsync(ct) == 0)
                return null;

            var vector = await embedder.EmbedAsync(imagePath, ct);
            var hits = await store.SearchAsync(vector, options.Value.TopK, ct);
            _lastProblem = "";
            var t = thresholds.Value;
            var match = Match(hits, t.ExampleSimilarity, t.ExampleAgreement);
            logger.LogDebug("Examples for {Path}: top {Score:0.000}, match {Match}", imagePath, hits.FirstOrDefault()?.Score, match);
            return match;
        }
        catch (ExamplesUnavailableException ex)
        {
            // The model may be missing for a long time; one line per distinct problem, not one per screenshot.
            if (_lastProblem != ex.Message)
                logger.LogWarning("Example stage unavailable, continuing without it: {Problem}", ex.Message);
            _lastProblem = ex.Message;
            return null;
        }
    }
}
