namespace ScreenSorter.Core.Models;

/// <summary>What happened to one file.</summary>
public enum SortOutcomeKind
{
    /// <summary>Moved into a category folder.</summary>
    Sorted,

    /// <summary>Moved into the review folder.</summary>
    Review,

    /// <summary>
    /// Left in place: no focus history covers its capture time (backlog) and no image-based
    /// stage is available yet.
    /// </summary>
    Deferred,

    /// <summary>Not processed (vanished, locked too long, or failed). Details are logged.</summary>
    Skipped,
}

/// <summary>Result of processing one file.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="SourcePath">The input path.</param>
/// <param name="TargetPath">The new path for <see cref="SortOutcomeKind.Sorted"/> and <see cref="SortOutcomeKind.Review"/>; otherwise <see langword="null"/>.</param>
public sealed record SortOutcome(SortOutcomeKind Kind, string SourcePath, string? TargetPath = null);

/// <summary>Counts for one drained batch, used for the tray notification.</summary>
/// <param name="Sorted">Files moved into category folders.</param>
/// <param name="Review">Files moved into the review folder.</param>
public sealed record BatchSummary(int Sorted, int Review);
