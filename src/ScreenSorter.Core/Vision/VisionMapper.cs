using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Vision;

/// <summary>Turns a <see cref="VisionResult"/> into a folder decision.</summary>
public sealed class VisionMapper(IOptions<LmStudioOptions> lmStudio, IOptions<RulesOptions> rules,
    IOptions<ThresholdsOptions> thresholds, IOptions<PathsOptions> paths)
{
    /// <summary>Category key whose answer uses <see cref="VisionResult.GameTitle"/> as sub-folder.</summary>
    public const string GameKey = "Game";

    /// <summary>Category key whose answer uses <see cref="VisionResult.AppName"/> as sub-folder.</summary>
    public const string ChatKey = "Chat";

    /// <summary>Maps the answer; low confidence, unknown categories and games without a title go to review.</summary>
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
            sub = Alias(result.GameTitle.Trim());
        }
        else if (string.Equals(result.Category, ChatKey, StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(result.AppName))
        {
            sub = result.AppName.Trim();
        }

        return new Classification(folder, sub, sub ?? folder, result.Confidence, DecisionStage.Vision);
    }

    private string Alias(string title) =>
        rules.Value.GameAliases.FirstOrDefault(a => string.Equals(a.Key, title, StringComparison.OrdinalIgnoreCase)).Value ?? title;
}
