namespace ScreenSorter.Core.Models;

/// <summary>The foreground window at a point in time.</summary>
/// <param name="Timestamp">When this window was (or still was) in the foreground, UTC.</param>
/// <param name="ProcessName">Exe file name, e.g. <c>Discord.exe</c>.</param>
/// <param name="ExePath">Full exe path, or empty if the process could not be queried.</param>
/// <param name="WindowTitle">Window title at <paramref name="Timestamp"/>.</param>
/// <param name="WindowHandle">Native window handle, used to re-read the title later.</param>
public sealed record FocusSnapshot(
    DateTimeOffset Timestamp,
    string ProcessName,
    string ExePath,
    string WindowTitle,
    nint WindowHandle = 0);
