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
}
