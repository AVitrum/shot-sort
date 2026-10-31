namespace ScreenSorter.Core.Sorting;

/// <summary>File-system operations the pipeline needs.</summary>
public interface IScreenshotStore
{
    /// <summary>Waits until the file has a stable size and can be opened exclusively.</summary>
    /// <param name="path">The new file.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns><see langword="false"/> if the file vanished or stayed locked past the timeout.</returns>
    Task<bool> WaitUntilReadyAsync(string path, CancellationToken ct);

    /// <summary>Returns the file's creation time, UTC.</summary>
    /// <param name="path">An existing file.</param>
    DateTimeOffset GetCreationTime(string path);

    /// <summary>Computes the lower-case hex SHA-256 of the file.</summary>
    /// <param name="path">An existing file.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<string> ComputeHashAsync(string path, CancellationToken ct);

    /// <summary>Lists the names of the direct sub-folders of <paramref name="folder"/>.</summary>
    /// <param name="folder">A folder that may not exist yet.</param>
    /// <returns>Folder names; empty if <paramref name="folder"/> does not exist.</returns>
    IReadOnlyList<string> ListSubfolders(string folder);

    /// <summary>
    /// Moves a file, creating the target folder. If the target exists, tries <c>_2</c>, <c>_3</c>, ... suffixes.
    /// </summary>
    /// <param name="source">File to move.</param>
    /// <param name="desiredTarget">Preferred target path.</param>
    /// <returns>The path the file was moved to.</returns>
    /// <exception cref="IOException">The move failed.</exception>
    string Move(string source, string desiredTarget);
}
