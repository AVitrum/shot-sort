namespace ScreenSorter.Core.Models;

/// <summary>Where a screenshot belongs.</summary>
/// <param name="Category">Top-level folder, e.g. <c>Games</c>, <c>Chat</c>, <c>Calls</c>.</param>
/// <param name="SubFolder">Optional second level, e.g. a game title or chat app; <see langword="null"/> for none.</param>
/// <param name="Name">File-name prefix for the renamed file.</param>
/// <param name="Confidence">0..1; below the threshold the file goes to review.</param>
/// <param name="Stage">The stage that produced this result.</param>
public sealed record Classification(
    string Category,
    string? SubFolder,
    string Name,
    double Confidence,
    DecisionStage Stage)
{
    /// <summary>Category name used for files that go to the review folder.</summary>
    public const string ReviewCategory = "_Review";

    /// <summary>Creates a review-folder classification.</summary>
    /// <param name="reviewFolder">Configured review folder name.</param>
    /// <param name="stage">Stage that sent the file to review.</param>
    /// <returns>A classification targeting <paramref name="reviewFolder"/> with zero confidence.</returns>
    public static Classification Review(string reviewFolder, DecisionStage stage = DecisionStage.Unmatched) =>
        new(reviewFolder, null, "Screenshot", 0, stage);
}
