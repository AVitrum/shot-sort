using ScreenSorter.Core.Configuration;

namespace ScreenSorter.Infrastructure.Files;

/// <summary>
/// Limits restarts after the app's services stop on their own. One restart clears a one-off fault; a fault that comes
/// straight back would otherwise restart the app in a loop, so a second one within <see cref="Window"/> is refused.
/// The time of the last restart is kept in a small file beside the user's settings, since it must outlive the process.
/// </summary>
public static class CrashRestartMarker
{
    private const string FileName = "last-crash-restart";

    /// <summary>How long after one restart another one is refused.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Claims a restart: allowed when none happened within <see cref="Window"/>, and then recorded.</summary>
    /// <param name="paths">The app's folder settings; the marker sits beside <see cref="PathsOptions.UserSettingsPath"/>.</param>
    /// <param name="now">The current time.</param>
    /// <returns>
    /// <see langword="true"/> if the app may restart. <see langword="false"/> if it restarted within the window, or if the
    /// marker cannot be read or written: without a record a loop could not be stopped, so no restart then.
    /// </returns>
    public static bool TryClaim(PathsOptions paths, DateTimeOffset now)
    {
        var folder = Path.GetDirectoryName(PathsOptions.Expand(paths.UserSettingsPath));
        if (string.IsNullOrEmpty(folder))
            return false;
        var path = Path.Combine(folder, FileName);
        try
        {
            if (File.Exists(path) && now - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) < Window)
                return false;
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, []);
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
