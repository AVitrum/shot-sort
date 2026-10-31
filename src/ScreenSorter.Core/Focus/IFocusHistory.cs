using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Focus;

/// <summary>Answers "which app was in focus at time T?".</summary>
public interface IFocusHistory
{
    /// <summary>
    /// Returns the last non-ignored foreground window at or before <paramref name="time"/>.
    /// </summary>
    /// <param name="time">Capture time of the screenshot.</param>
    /// <returns>
    /// The snapshot, or <see langword="null"/> when tracking started after <paramref name="time"/>
    /// (backlog files) or no usable snapshot exists.
    /// </returns>
    FocusSnapshot? GetFocusAt(DateTimeOffset time);
}
