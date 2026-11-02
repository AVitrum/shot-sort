using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>Durable record of every sorting decision.</summary>
public interface IDecisionLog
{
    /// <summary>Appends a decision.</summary>
    /// <param name="record">The decision to store.</param>
    /// <param name="ct">Cancels the write.</param>
    Task RecordAsync(SortRecord record, CancellationToken ct);

    /// <summary>Returns the most recent decision for a file content hash.</summary>
    /// <param name="fileHash">Lower-case hex SHA-256.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The record, or <see langword="null"/> if the file was never sorted by the app.</returns>
    Task<SortRecord?> FindLatestByHashAsync(string fileHash, CancellationToken ct);

    /// <summary>Returns where each known file was last recorded, so a rescan can skip files that have not moved.</summary>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The latest <see cref="SortRecord.NewPath"/> per file hash, compared case-insensitively.</returns>
    Task<IReadOnlySet<string>> LatestPathsAsync(CancellationToken ct);

    /// <summary>Returns one entry per file, the most recently changed first.</summary>
    /// <param name="limit">Maximum number of files.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The latest decision of each file, with the first one for comparison.</returns>
    Task<IReadOnlyList<FileDecisions>> ListFilesAsync(int limit, CancellationToken ct);

    /// <summary>Returns every decision in the order made, without personal details, for measuring accuracy.</summary>
    /// <param name="ct">Cancels the query.</param>
    Task<IReadOnlyList<DecisionFact>> ListFactsAsync(CancellationToken ct);

    /// <summary>
    /// Erases window titles and program paths recorded before a point in time, overwriting them on disk. Whole records
    /// stay: without them, sorted files would look placed by the user and the app would learn from its own guesses.
    /// </summary>
    /// <param name="before">Details of focus snapshots older than this are erased.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>How many records changed.</returns>
    Task<int> EraseFocusDetailsAsync(DateTimeOffset before, CancellationToken ct);
}

/// <summary>What the log knows about one file.</summary>
/// <param name="Latest">The most recent decision; its <see cref="SortRecord.NewPath"/> is where the file should be.</param>
/// <param name="First">The first decision, whose <see cref="SortRecord.OriginalPath"/> is where the file came from.</param>
public sealed record FileDecisions(SortRecord Latest, SortRecord First);

/// <summary>One decision reduced to what accuracy depends on.</summary>
/// <param name="FileHash">Identifies the file.</param>
/// <param name="Stage">Who decided.</param>
/// <param name="Category">The chosen top-level folder.</param>
/// <param name="SubFolder">The chosen second-level folder, if any.</param>
public sealed record DecisionFact(string FileHash, DecisionStage Stage, string Category, string? SubFolder);
