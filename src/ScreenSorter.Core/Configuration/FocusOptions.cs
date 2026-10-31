namespace ScreenSorter.Core.Configuration;

/// <summary>Focus-tracking settings. Bound from <c>Focus</c>.</summary>
public sealed class FocusOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Focus";

    /// <summary>Number of focus changes kept in memory.</summary>
    public int HistorySize { get; set; } = 256;

    /// <summary>
    /// Process names (e.g. <c>ScreenClippingHost.exe</c>) that never count as the captured app,
    /// such as the snipping overlay itself.
    /// </summary>
    public List<string> IgnoredProcesses { get; set; } = [];
}
