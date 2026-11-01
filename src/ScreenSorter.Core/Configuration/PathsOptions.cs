namespace ScreenSorter.Core.Configuration;

/// <summary>
/// Folder locations, bound from the <c>Paths</c> configuration section.
/// Values may contain environment variables such as <c>%USERPROFILE%</c>; call <see cref="Expand"/> before use.
/// </summary>
public sealed class PathsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Paths";

    /// <summary>Folders whose top level is watched for new screenshots.</summary>
    public List<string> WatchFolders { get; set; } = [];

    /// <summary>Root under which category folders (<c>Games</c>, <c>Chat</c>, ...) are created.</summary>
    public string SortedRoot { get; set; } = "";

    /// <summary>Name of the folder under <see cref="SortedRoot"/> that receives low-confidence files.</summary>
    public string ReviewFolder { get; set; } = "_Review";

    /// <summary>Path of the SQLite decision log.</summary>
    public string DatabasePath { get; set; } = "";

    /// <summary>File where the settings window saves its values; layered over <c>appsettings.json</c>.</summary>
    public string UserSettingsPath { get; set; } = "";

    /// <summary>Folder for the app's log files.</summary>
    public string LogFolder { get; set; } = "";

    /// <summary>Expands environment variables and normalizes a configured path.</summary>
    /// <param name="path">A configured path, possibly containing <c>%VAR%</c> tokens.</param>
    /// <returns>The full path, or an empty string when <paramref name="path"/> is empty.</returns>
    public static string Expand(string path) =>
        string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
}
