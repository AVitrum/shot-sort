using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>One file as the history page shows it.</summary>
/// <param name="Decisions">What the log knows about the file.</param>
/// <param name="Folder">The folder it was last put in, relative to the sorted root; empty for the root itself.</param>
/// <param name="Why">One sentence on how it got there.</param>
/// <param name="FileExists">Whether the file is still where the log says.</param>
/// <param name="CanUndo">Whether the app's move can be undone.</param>
public sealed record HistoryItem(FileDecisions Decisions, string Folder, string Why, bool FileExists, bool CanUndo)
{
    /// <summary>Where the log says the file is.</summary>
    public string Path => Decisions.Latest.NewPath;

    /// <summary>The file's name.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>When the latest decision was made, local time.</summary>
    public DateTime When => Decisions.Latest.DecidedAt.LocalDateTime;

    /// <summary>The stage of the latest decision.</summary>
    public DecisionStage Stage => Decisions.Latest.Classification.Stage;
}

/// <summary>Why a change requested from the history page did or did not happen.</summary>
public enum HistoryChange
{
    /// <summary>The file was moved.</summary>
    Done,

    /// <summary>The file is no longer where the log says.</summary>
    Missing,

    /// <summary>A file with other content is there now.</summary>
    Changed,

    /// <summary>This decision cannot be undone, or the folder is not one the app sorts into.</summary>
    NotAllowed,

    /// <summary>Moving the file failed; see the log.</summary>
    Failed,
}

/// <summary>Lists past decisions, explains them, and lets the user undo or correct them.</summary>
public sealed class DecisionHistory(
    IScreenshotStore files,
    IDecisionLog log,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<DecisionHistory> logger)
{
    private string Root => PathsOptions.Expand(paths.Value.SortedRoot);

    /// <summary>Returns the files with the most recent changes.</summary>
    /// <param name="limit">Maximum number of files.</param>
    /// <param name="ct">Cancels the query.</param>
    public async Task<IReadOnlyList<HistoryItem>> ListAsync(int limit, CancellationToken ct)
    {
        var entries = await log.ListFilesAsync(limit, ct);
        return entries.Select(ToItem).ToList();
    }

    /// <summary>
    /// Moves a file back to where the app found it and leaves it there. The decision is logged before the move: the
    /// file lands in a watched folder, and intake must already see that it has to stay.
    /// </summary>
    /// <param name="item">The file.</param>
    /// <param name="ct">Cancels the hash and the log writes.</param>
    /// <returns>What happened; the file stays where it was unless the result is <see cref="HistoryChange.Done"/>.</returns>
    public async Task<HistoryChange> UndoAsync(HistoryItem item, CancellationToken ct)
    {
        if (!item.CanUndo)
            return HistoryChange.NotAllowed;
        if (!File.Exists(item.Path))
            return HistoryChange.Missing;

        var latest = item.Decisions.Latest;
        var target = item.Decisions.First.OriginalPath;
        try
        {
            if (await files.ComputeHashAsync(item.Path, ct) != latest.FileHash)
                return HistoryChange.Changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read {Path}", item.Path);
            return HistoryChange.Failed;
        }

        await RecordUndoneAsync(latest, target, ct);
        try
        {
            var moved = files.Move(item.Path, target);
            if (!string.Equals(moved, target, StringComparison.OrdinalIgnoreCase))
                await RecordUndoneAsync(latest, moved, ct);
            logger.LogInformation("Undid {Path} -> {Target}", item.Path, moved);
            return HistoryChange.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not undo {Path}", item.Path);
            // The file did not move, so the record must say what is true again.
            await log.RecordAsync(latest with { DecidedAt = time.GetUtcNow() }, CancellationToken.None);
            return HistoryChange.Failed;
        }
    }

    /// <summary>
    /// Moves a file into a category folder. The correction watcher then treats this like a move the user made in
    /// Explorer: it learns the example and renames the file.
    /// </summary>
    /// <param name="item">The file.</param>
    /// <param name="folder">A folder under the sorted root. The root itself is refused, because intake would sort the file again.</param>
    /// <returns>What happened.</returns>
    public HistoryChange MoveTo(HistoryItem item, string folder)
    {
        var target = Path.Combine(folder, item.FileName);
        if (CorrectionProcessor.LabelOf(Root, target) is null)
            return HistoryChange.NotAllowed;
        if (!File.Exists(item.Path))
            return HistoryChange.Missing;

        try
        {
            files.Move(item.Path, target);
            logger.LogInformation("Moved {Path} to {Folder} from the history page", item.Path, folder);
            return HistoryChange.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not move {Path} to {Folder}", item.Path, folder);
            return HistoryChange.Failed;
        }
    }

    private Task RecordUndoneAsync(SortRecord latest, string path, CancellationToken ct) => log.RecordAsync(
        new SortRecord(latest.NewPath, path, latest.FileHash, new Classification("", null, "", 0, DecisionStage.Undone), latest.Focus, time.GetUtcNow()), ct);

    private HistoryItem ToItem(FileDecisions entry)
    {
        var latest = entry.Latest;
        var exists = File.Exists(latest.NewPath);
        var relative = Path.GetRelativePath(Root, Path.GetDirectoryName(latest.NewPath) ?? Root);
        var folder = relative == "." || relative.StartsWith("..", StringComparison.Ordinal) ? "" : relative;
        return new HistoryItem(entry, folder, Explain(entry), exists, exists && CanUndo(entry));
    }

    // Only a move the app made can be undone, and only back to a place the app takes files from: a first location inside
    // a category folder would count as a correction the moment the file arrived.
    private bool CanUndo(FileDecisions entry) =>
        entry.Latest.Classification.Stage is not (DecisionStage.UserCorrection or DecisionStage.Undone)
        && CorrectionProcessor.LabelOf(Root, entry.First.OriginalPath) is null;

    private string Explain(FileDecisions entry)
    {
        var c = entry.Latest.Classification;
        var inReview = string.Equals(c.Category, paths.Value.ReviewFolder, StringComparison.OrdinalIgnoreCase);
        switch (c.Stage)
        {
            case DecisionStage.Rule:
                return entry.Latest.Focus is { ProcessName.Length: > 0 } focus
                    ? $"{focus.ProcessName} had focus, and a rule sends it to this folder."
                    : "A rule sends the focused app to this folder.";
            case DecisionStage.Example:
                return $"It looks like examples you taught it ({c.Confidence:0.00} similar).";
            case DecisionStage.Vision:
                return inReview ? "The vision model was not sure, or named a game that is not on your list." : "The vision model chose this folder.";
            case DecisionStage.ImageCheck:
                return "The image is blank or could not be read.";
            case DecisionStage.Unmatched:
                return "Nothing could place it.";
            case DecisionStage.Undone:
                return "You undid the move. The app leaves it alone until you move it yourself.";
            case DecisionStage.UserCorrection:
                var first = entry.First.Classification;
                return first.Stage == DecisionStage.UserCorrection || SameFolder(first, c)
                    ? "You put it here."
                    : $"You moved it here. The app had chosen {FolderName(first)}.";
            default:
                return "";
        }
    }

    private static bool SameFolder(Classification a, Classification b) =>
        string.Equals(a.Category, b.Category, StringComparison.OrdinalIgnoreCase)
        && SortedPathBuilder.MatchKey(a.SubFolder ?? "") == SortedPathBuilder.MatchKey(b.SubFolder ?? "");

    private static string FolderName(Classification c) => c.SubFolder is null ? c.Category : $"{c.Category}/{c.SubFolder}";
}
