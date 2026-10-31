namespace ScreenSorter.Core.Models;

/// <summary>One entry of the decision log.</summary>
/// <param name="OriginalPath">Where the file was found.</param>
/// <param name="NewPath">Where it was moved.</param>
/// <param name="FileHash">Lower-case hex SHA-256 of the file content.</param>
/// <param name="Classification">The decision.</param>
/// <param name="Focus">The focus snapshot used, if any.</param>
/// <param name="DecidedAt">When the decision was made, UTC.</param>
public sealed record SortRecord(
    string OriginalPath,
    string NewPath,
    string FileHash,
    Classification Classification,
    FocusSnapshot? Focus,
    DateTimeOffset DecidedAt);
