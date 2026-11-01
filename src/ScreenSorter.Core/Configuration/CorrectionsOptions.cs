namespace ScreenSorter.Core.Configuration;

/// <summary>How user corrections are detected. Bound from <c>Corrections</c>.</summary>
public sealed class CorrectionsOptions
{
    /// <summary>
    /// Delay between seeing a file in a category folder and checking it. Must exceed the time between the
    /// app's own move and its log entry, or the app's moves look like the user's.
    /// </summary>
    public int SettleSeconds { get; set; } = 5;

    /// <summary>Interval of the rescan that catches dropped events, moves made while the app was off, and failed retries.</summary>
    public int RescanMinutes { get; set; } = 10;

    /// <summary>Configuration section name.</summary>
    public const string Section = "Corrections";
}
