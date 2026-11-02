namespace ScreenSorter.Core.Configuration;

/// <summary>Confidence and image-check thresholds. Bound from <c>Thresholds</c>.</summary>
public sealed class ThresholdsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Thresholds";

    /// <summary>
    /// Minimum cosine similarity of a confirmed example to count (milestone 4). Measured by holding out 311 confirmed
    /// screenshots: precision stays near 97% down to this value and drops quickly below it.
    /// </summary>
    public double ExampleSimilarity { get; set; } = 0.84;

    /// <summary>Share of the top-k examples that must agree on a label (milestone 4).</summary>
    public double ExampleAgreement { get; set; } = 1.0;

    /// <summary>
    /// How similar examples must be to question an app rule that names another folder. Higher than
    /// <see cref="ExampleSimilarity"/>: a rule knows which window had focus, and a weak match should not outvote that.
    /// </summary>
    public double ExampleConflictSimilarity { get; set; } = 0.90;

    /// <summary>Vision answers below this confidence go to review.</summary>
    public double VisionConfidence { get; set; } = 0.70;

    /// <summary>
    /// An image whose grayscale standard deviation (0–255 scale, measured on a downscaled copy)
    /// is below this is treated as black/blank and goes to review.
    /// </summary>
    public double UniformMaxStdDev { get; set; } = 2.5;
}
