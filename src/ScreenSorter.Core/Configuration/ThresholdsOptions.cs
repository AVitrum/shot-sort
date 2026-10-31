namespace ScreenSorter.Core.Configuration;

/// <summary>Confidence and image-check thresholds. Bound from <c>Thresholds</c>.</summary>
public sealed class ThresholdsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Thresholds";

    /// <summary>Minimum cosine similarity of a confirmed example to count (milestone 4).</summary>
    public double ExampleSimilarity { get; set; } = 0.90;

    /// <summary>Share of the top-k examples that must agree on a label (milestone 4).</summary>
    public double ExampleAgreement { get; set; } = 0.8;

    /// <summary>Vision answers below this confidence go to review.</summary>
    public double VisionConfidence { get; set; } = 0.70;

    /// <summary>
    /// An image whose grayscale standard deviation (0–255 scale, measured on a downscaled copy)
    /// is below this is treated as black/blank and goes to review.
    /// </summary>
    public double UniformMaxStdDev { get; set; } = 2.5;
}
