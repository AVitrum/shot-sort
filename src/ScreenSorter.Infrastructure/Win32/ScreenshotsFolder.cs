using System.Runtime.InteropServices;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>
/// Finds the folder Windows saves screenshots to, which is not under the user profile when OneDrive or the user has
/// moved Pictures.
/// </summary>
public static class ScreenshotsFolder
{
    /// <summary>Environment variable that configuration values use for this folder, as <c>%SCREENSHOTS%</c>.</summary>
    public const string EnvironmentVariable = "SCREENSHOTS";

    private const uint KnownFolderDontVerify = 0x4000;
    private static readonly Guid KnownFolderScreenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    /// <summary>Returns the screenshots folder, whether or not it exists yet.</summary>
    /// <returns>The known folder, or <c>Pictures\Screenshots</c> if Windows cannot name it.</returns>
    public static string Locate()
    {
        // Don't-verify: the folder does not exist until the first screenshot, and that is no reason to fail.
        if (SHGetKnownFolderPath(KnownFolderScreenshots, KnownFolderDontVerify, 0, out var buffer) == 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(buffer) is { Length: > 0 } path)
                    return path;
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    /// <summary>Sets <see cref="EnvironmentVariable"/> for this process. Call before configuration paths are expanded.</summary>
    public static void PublishEnvironmentVariable() =>
        Environment.SetEnvironmentVariable(EnvironmentVariable, Locate());

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);
}
