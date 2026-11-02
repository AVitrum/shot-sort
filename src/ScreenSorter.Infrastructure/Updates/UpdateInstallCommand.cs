using System.Diagnostics;

namespace ScreenSorter.Infrastructure.Updates;

/// <summary>Builds the command that replaces this copy with a downloaded version.</summary>
public static class UpdateInstallCommand
{
    /// <summary>
    /// Runs the installer silently over <paramref name="appFolder"/>. The installer starts the new version itself; if it
    /// fails, the command starts the old one again, because an app that vanished silently would leave screenshots unsorted
    /// with nothing telling the user. Start this after releasing the single-instance mutex, or the installer refuses to run.
    /// </summary>
    /// <param name="setup">Path of a verified installer.</param>
    /// <param name="appFolder">The folder of the running copy. Passed on so an update can never land in another install.</param>
    /// <param name="appExe">The running executable, started again if the installer fails.</param>
    /// <returns>A start description; no window is shown.</returns>
    public static ProcessStartInfo Create(string setup, string appFolder, string appExe)
    {
        // Backslash before a closing quote would escape it for the installer's command line.
        var folder = appFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // /s: cmd removes the outer quotes and keeps the rest exactly as written.
            Arguments = $"/d /s /c \"\"{setup}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1 \"/DIR={folder}\" || \"{appExe}\"\"",
        };
    }
}
