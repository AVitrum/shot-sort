using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Status;

/// <summary>The words the tray icon shows: its tooltip and the note after a batch.</summary>
public static class TrayText
{
    /// <summary>Longest tooltip the notification area accepts; WinForms throws on longer text.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>Describes the app's state in the tooltip, most important state first.</summary>
    /// <param name="paused">The user paused sorting.</param>
    /// <param name="held">Sorting waits for the first-run guide.</param>
    /// <param name="attentionName">Name of the first service that needs attention, or null.</param>
    /// <returns>At most <see cref="MaxTooltipLength"/> characters.</returns>
    public static string Tooltip(bool paused, bool held, string? attentionName)
    {
        var text = paused ? "ScreenSorter (paused)"
            : held ? "ScreenSorter (waiting for the guide)"
            : attentionName is not null ? $"ScreenSorter ({attentionName} needs attention)"
            : "ScreenSorter";
        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }

    /// <summary>Sums up a drained batch for the tray notification.</summary>
    /// <param name="summary">The batch counts.</param>
    /// <returns>One short line.</returns>
    public static string Summary(BatchSummary summary)
    {
        var sorted = $"Sorted {summary.Sorted} {(summary.Sorted == 1 ? "screenshot" : "screenshots")}";
        return summary.Review switch
        {
            0 => sorted,
            1 => $"{sorted}, 1 needs review",
            _ => $"{sorted}, {summary.Review} need review",
        };
    }
}
