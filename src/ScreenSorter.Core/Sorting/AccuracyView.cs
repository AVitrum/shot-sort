using System.Globalization;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>One row of the accuracy table.</summary>
/// <param name="Stage">Readable name of the stage.</param>
/// <param name="Placed">Files the stage put in a folder other than review.</param>
/// <param name="Moved">How many of those the user moved afterwards.</param>
/// <param name="Kept">Share the user kept, as a percentage, or "-" when the stage placed nothing.</param>
/// <param name="Review">Files the stage sent to review.</param>
public sealed record AccuracyRow(string Stage, int Placed, int Moved, string Kept, int Review);

/// <summary>Turns an <see cref="AccuracyReport"/> into the text the accuracy page shows.</summary>
public static class AccuracyView
{
    /// <summary>Builds one row per stage, in the report's order.</summary>
    /// <param name="report">The report to show.</param>
    /// <param name="culture">Formats the kept share; null uses the current culture.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<AccuracyRow> Rows(AccuracyReport report, CultureInfo? culture = null) =>
        report.Stages.Select(s => new AccuracyRow(
            StageName(s.Stage),
            s.Sorted,
            s.Changed,
            s.KeptShare is { } kept ? kept.ToString("P0", culture ?? CultureInfo.CurrentCulture) : "-",
            s.Review)).ToList();

    /// <summary>Names a stage the way the user knows it from the settings and the guide.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>A short name; stages without one keep their enum name.</returns>
    public static string StageName(DecisionStage stage) => stage switch
    {
        DecisionStage.Rule => "App rules",
        DecisionStage.Example => "Your examples",
        DecisionStage.Vision => "Vision model",
        DecisionStage.Burst => "Copied from a similar shot",
        DecisionStage.ImageCheck => "Blank image check",
        _ => stage.ToString(),
    };

    /// <summary>Says how many screenshots the user placed from the review folder.</summary>
    /// <param name="count">The number from <see cref="AccuracyReport.ReviewedByUser"/>.</param>
    /// <returns>One sentence.</returns>
    public static string ReviewedSentence(int count) => count == 1
        ? "You placed 1 screenshot from the review folder."
        : $"You placed {count} screenshots from the review folder.";
}
