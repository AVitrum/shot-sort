using ScreenSorter.Core.Configuration;

namespace ScreenSorter.Infrastructure.Files;

/// <summary>
/// Remembers that the guide was shown, in a small file beside the user's settings. It cannot live in the settings file,
/// because saving from the settings window rewrites that file whole.
/// </summary>
public static class GuideMarker
{
    private const string FileName = "guide-seen";

    /// <summary>Whether the guide was already shown.</summary>
    /// <param name="paths">The app's folder settings; the marker sits beside <see cref="PathsOptions.UserSettingsPath"/>.</param>
    /// <returns><see langword="true"/> if the marker exists, or if no settings path is configured so it could never be written.</returns>
    public static bool IsSet(PathsOptions paths) => MarkerPath(paths) is not { } path || File.Exists(path);

    /// <summary>Records that the guide was shown. Does nothing if no settings path is configured.</summary>
    /// <param name="paths">The app's folder settings.</param>
    /// <exception cref="IOException">The marker could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The marker could not be written.</exception>
    public static void Set(PathsOptions paths)
    {
        if (MarkerPath(paths) is not { } path)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    private static string? MarkerPath(PathsOptions paths)
    {
        var folder = Path.GetDirectoryName(PathsOptions.Expand(paths.UserSettingsPath));
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FileName);
    }
}
