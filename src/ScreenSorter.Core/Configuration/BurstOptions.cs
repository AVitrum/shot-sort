namespace ScreenSorter.Core.Configuration;

/// <summary>Reuse of a model answer for near-identical screenshots taken around the same time. Bound from <c>Burst</c>.</summary>
public sealed class BurstOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Burst";

    /// <summary>Whether a queued file may copy the folder of a near-identical file instead of asking the model.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Greatest distance in capture time, in seconds. Measured on 557 real screenshots: 300 s reuses 15% of answers, and
    /// doubling it adds almost nothing.
    /// </summary>
    public int WindowSeconds { get; set; } = 300;

    /// <summary>
    /// Minimum cosine similarity. Measured on the same screenshots: every file reused at this value stayed where it was
    /// put, and the first wrong copies appear at 0.92.
    /// </summary>
    public double Similarity { get; set; } = 0.94;

    /// <summary>How many model answers are remembered. The oldest is dropped first.</summary>
    public int MaxAnchors { get; set; } = 256;
}
