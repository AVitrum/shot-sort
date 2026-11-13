using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>Which files the history page lists. The values follow the order of the page's filter list.</summary>
public enum HistoryFilter
{
    /// <summary>Every file.</summary>
    All = 0,

    /// <summary>Files that are still in the review folder.</summary>
    NeedsReview = 1,

    /// <summary>Files whose latest move was made by the app.</summary>
    PlacedByApp = 2,

    /// <summary>Files the user moved or sent back.</summary>
    PlacedByYou = 3,
}

/// <summary>What the history page shows: which items match, and what each change outcome tells the user.</summary>
public static class HistoryView
{
    /// <summary>Lists the items that match a filter and a search text, in their original order.</summary>
    /// <param name="items">The items to choose from.</param>
    /// <param name="filter">Which kind of item to keep.</param>
    /// <param name="search">Text the file name or folder must contain, any case; null or blank keeps all.</param>
    /// <param name="reviewFolder">Name of the review folder, compared without regard to case.</param>
    /// <returns>A new list; <paramref name="items"/> is not changed.</returns>
    public static IReadOnlyList<HistoryItem> Filter(IEnumerable<HistoryItem> items, HistoryFilter filter, string? search, string reviewFolder)
    {
        var text = search?.Trim() ?? "";
        return items.Where(item => Matches(item, filter, reviewFolder)
            && (text.Length == 0
                || item.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.Folder.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Finds the item for a file, so a selection survives reloading the list.</summary>
    /// <param name="items">The reloaded items.</param>
    /// <param name="fileHash">Hash of the file that was selected; null when nothing was.</param>
    /// <returns>The item whose latest decision has that hash, or null.</returns>
    public static HistoryItem? FindSameFile(IEnumerable<HistoryItem> items, string? fileHash) =>
        fileHash is null ? null : items.FirstOrDefault(i => i.Decisions.Latest.FileHash == fileHash);

    /// <summary>Tells the user what came of undoing a move.</summary>
    /// <param name="result">The outcome from <see cref="DecisionHistory.UndoAsync"/>.</param>
    /// <returns>One sentence.</returns>
    public static string UndoMessage(HistoryChange result) => result switch
    {
        HistoryChange.Done => "Moved back. The app leaves it alone until you move it yourself.",
        HistoryChange.Missing => "The file is no longer in this folder.",
        HistoryChange.Changed => "A different file has this name now, so it was left alone.",
        HistoryChange.NotAllowed => "This one cannot be undone.",
        _ => "It could not be moved. It may be open in another app.",
    };

    /// <summary>Tells the user what came of moving a file to a folder they chose.</summary>
    /// <param name="result">The outcome from <see cref="DecisionHistory.MoveTo"/>.</param>
    /// <returns>One sentence.</returns>
    public static string MoveMessage(HistoryChange result) => result switch
    {
        HistoryChange.Done => "Moved. The app is learning from it, the same as when you drag a file.",
        HistoryChange.NotAllowed => "Choose a folder inside the sorted folders, not the top level or a folder elsewhere.",
        HistoryChange.Missing => "The file is no longer in this folder.",
        _ => "It could not be moved. It may be open in another app.",
    };

    private static bool Matches(HistoryItem item, HistoryFilter filter, string reviewFolder) => filter switch
    {
        // Still in review: a file the user already took out of the folder needs nothing more.
        HistoryFilter.NeedsReview => item.FileExists
            && string.Equals(item.Decisions.Latest.Classification.Category, reviewFolder, StringComparison.OrdinalIgnoreCase),
        HistoryFilter.PlacedByApp => item.Stage is not (DecisionStage.UserCorrection or DecisionStage.Undone),
        HistoryFilter.PlacedByYou => item.Stage is DecisionStage.UserCorrection or DecisionStage.Undone,
        _ => true,
    };
}
