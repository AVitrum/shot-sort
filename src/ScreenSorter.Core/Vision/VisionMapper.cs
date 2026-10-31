using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Core.Vision;

/// <summary>Turns a <see cref="VisionResult"/> into a folder decision.</summary>
public sealed class VisionMapper(IOptions<LmStudioOptions> lmStudio, IOptions<RulesOptions> rules,
    IOptions<ThresholdsOptions> thresholds, IOptions<PathsOptions> paths, IGameCatalog games)
{
    /// <summary>Category key whose answer uses <see cref="VisionResult.GameTitle"/> as sub-folder.</summary>
    public const string GameKey = "Game";

    /// <summary>Category key whose answer uses <see cref="VisionResult.AppName"/> as sub-folder.</summary>
    public const string ChatKey = "Chat";

    /// <summary>
    /// Maps the answer. Low confidence, unknown categories, and games without a title go to review; so do games
    /// whose title is not in <see cref="IGameCatalog"/> (when the catalog is not empty, D-017).
    /// </summary>
    /// <param name="result">The model's answer, or <see langword="null"/> if it was unusable.</param>
    /// <returns>A <see cref="DecisionStage.Vision"/> classification, or a review classification.</returns>
    public Classification Map(VisionResult? result)
    {
        var review = Classification.Review(paths.Value.ReviewFolder, DecisionStage.Vision) with
        {
            Confidence = result?.Confidence ?? 0,
        };
        if (result is null
            || result.Confidence < thresholds.Value.VisionConfidence
            || !lmStudio.Value.Categories.TryGetValue(result.Category, out var folder))
            return review;

        string? sub = null;
        if (string.Equals(result.Category, GameKey, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(result.GameTitle))
                return review;
            sub = KnownTitle(Alias(result.GameTitle.Trim()));
            if (sub is null)
                return review;
        }
        else if (string.Equals(result.Category, ChatKey, StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(result.AppName))
        {
            sub = result.AppName.Trim();
        }

        return new Classification(folder, sub, sub ?? folder, result.Confidence, DecisionStage.Vision);
    }

    // Returns the catalog's spelling, null if the title is unknown, or the title itself when nothing is known.
    private string? KnownTitle(string title)
    {
        var known = games.GetTitles();
        if (known.Count == 0)
            return title;
        var key = SortedPathBuilder.MatchKey(title);
        return known.FirstOrDefault(k => SortedPathBuilder.MatchKey(k) == key);
    }

    private string Alias(string title) =>
        rules.Value.GameAliases.FirstOrDefault(a => string.Equals(a.Key, title, StringComparison.OrdinalIgnoreCase)).Value ?? title;
}
