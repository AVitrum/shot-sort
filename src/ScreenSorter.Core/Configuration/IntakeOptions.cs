namespace ScreenSorter.Core.Configuration;

/// <summary>How new files are detected and when they count as complete. Bound from <c>Intake</c>.</summary>
public sealed class IntakeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Intake";

    /// <summary>File extensions (with dot, case-insensitive) that are treated as screenshots.</summary>
    public List<string> Extensions { get; set; } = [".png", ".jpg", ".jpeg"];

    /// <summary>Interval of the safety rescan that catches events the file watcher dropped.</summary>
    public int RescanIntervalSeconds { get; set; } = 60;

    /// <summary>Delay between file-size checks while waiting for a file to finish writing.</summary>
    public int StabilityPollMilliseconds { get; set; } = 500;

    /// <summary>Give up on a file that is still being written after this long.</summary>
    public int StabilityTimeoutSeconds { get; set; } = 30;

    /// <summary>After the queue drains, wait this long for more files before sending one batch notification.</summary>
    public int BatchQuietSeconds { get; set; } = 5;
}
