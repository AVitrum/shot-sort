namespace ScreenSorter.Core.Configuration;

/// <summary>How existing screenshots are handled. Bound from <c>Backlog</c>.</summary>
public sealed class BacklogOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Backlog";

    /// <summary>
    /// Sort screenshots taken while the app was not running, by image content only. When off they stay where they are.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
