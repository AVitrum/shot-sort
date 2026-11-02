using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>How one stage has done.</summary>
/// <param name="Stage">The stage that made the first decision for these files.</param>
/// <param name="Sorted">Files it placed in a folder.</param>
/// <param name="Review">Files it sent to review.</param>
/// <param name="Changed">Files it placed that the user later moved elsewhere or undid.</param>
public sealed record StageAccuracy(DecisionStage Stage, int Sorted, int Review, int Changed)
{
    /// <summary>Share of placed files the user left where they were, 0–1; <see langword="null"/> if none were placed.</summary>
    public double? KeptShare => Sorted == 0 ? null : (double)(Sorted - Changed) / Sorted;
}

/// <summary>A correction the user made: where the app put files, and where the user wanted them.</summary>
/// <param name="From">The folder the app chose.</param>
/// <param name="To">The folder the user moved them to.</param>
/// <param name="Count">How many files.</param>
public sealed record FolderCorrection(string From, string To, int Count);

/// <summary>
/// Measures the app against what the user did afterwards: a file the user moved counts as a miss. A file nobody looked
/// at counts as right, so these numbers lean optimistic.
/// </summary>
/// <param name="Stages">Per stage, the busiest first.</param>
/// <param name="Corrections">The most common corrections, largest first.</param>
/// <param name="ReviewedByUser">Files sent to review that the user then placed.</param>
public sealed record AccuracyReport(IReadOnlyList<StageAccuracy> Stages, IReadOnlyList<FolderCorrection> Corrections, int ReviewedByUser)
{
    private const int MaxCorrections = 8;

    /// <summary>Builds the report.</summary>
    /// <param name="facts">Every decision, oldest first.</param>
    /// <param name="reviewFolder">Name of the review folder.</param>
    /// <returns>The report; files placed by the user from the start are not counted.</returns>
    public static AccuracyReport Build(IEnumerable<DecisionFact> facts, string reviewFolder)
    {
        var stages = new Dictionary<DecisionStage, (int Sorted, int Review, int Changed)>();
        var corrections = new Dictionary<(string From, string To), int>();
        var reviewed = 0;

        foreach (var file in facts.GroupBy(f => f.FileHash))
        {
            var decisions = file.ToList();
            var first = decisions[0];
            if (first.Stage is DecisionStage.UserCorrection or DecisionStage.Undone)
                continue;

            var counts = stages.GetValueOrDefault(first.Stage);
            var last = decisions.LastOrDefault(d => d.Stage is DecisionStage.UserCorrection or DecisionStage.Undone);
            var moved = last is not null && (last.Stage == DecisionStage.Undone || !SameFolder(first, last));

            if (IsReview(first, reviewFolder))
            {
                counts.Review++;
                if (moved && !IsReview(last!, reviewFolder))
                    reviewed++;
            }
            else
            {
                counts.Sorted++;
                if (moved)
                {
                    counts.Changed++;
                    if (last!.Stage == DecisionStage.UserCorrection)
                    {
                        var key = (Folder(first), Folder(last));
                        corrections[key] = corrections.GetValueOrDefault(key) + 1;
                    }
                }
            }
            stages[first.Stage] = counts;
        }

        return new AccuracyReport(
            stages.Select(s => new StageAccuracy(s.Key, s.Value.Sorted, s.Value.Review, s.Value.Changed))
                .OrderByDescending(s => s.Sorted + s.Review).ToList(),
            corrections.Select(c => new FolderCorrection(c.Key.From, c.Key.To, c.Value))
                .OrderByDescending(c => c.Count).ThenBy(c => c.From, StringComparer.OrdinalIgnoreCase).Take(MaxCorrections).ToList(),
            reviewed);
    }

    private static bool IsReview(DecisionFact fact, string reviewFolder) =>
        string.Equals(fact.Category, reviewFolder, StringComparison.OrdinalIgnoreCase);

    private static bool SameFolder(DecisionFact a, DecisionFact b) =>
        string.Equals(a.Category, b.Category, StringComparison.OrdinalIgnoreCase)
        && SortedPathBuilder.MatchKey(a.SubFolder ?? "") == SortedPathBuilder.MatchKey(b.SubFolder ?? "");

    private static string Folder(DecisionFact fact) => fact.SubFolder is null ? fact.Category : $"{fact.Category}/{fact.SubFolder}";
}
