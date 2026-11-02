namespace ScreenSorter.Core.Configuration;

/// <summary>How long personal details are kept. Bound from <c>Privacy</c>.</summary>
public sealed class PrivacyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Privacy";

    /// <summary>
    /// Days a decision keeps the focused window's title and program path. 0 means they are not stored at all.
    /// The rest of the record stays: it is how the app knows it sorted a file.
    /// </summary>
    public int WindowTitleDays { get; set; } = 30;

    /// <summary>Days the app's log files are kept. At least 1.</summary>
    public int LogDays { get; set; } = 14;
}
