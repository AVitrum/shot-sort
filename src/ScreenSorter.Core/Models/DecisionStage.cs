namespace ScreenSorter.Core.Models;

/// <summary>Which pipeline stage produced a sorting decision.</summary>
public enum DecisionStage
{
    /// <summary>A focus rule (app rule or game detection).</summary>
    Rule,

    /// <summary>No stage could classify the file; it went to the review folder.</summary>
    Unmatched,

    /// <summary>Nearest confirmed examples in Qdrant (milestone 4).</summary>
    Example,

    /// <summary>The LM Studio vision model (milestone 3).</summary>
    Vision,

    /// <summary>The user moved the file to another category.</summary>
    UserCorrection,

    /// <summary>The image is black or near-uniform (failed fullscreen capture); sent to review.</summary>
    ImageCheck,

    /// <summary>The user undid the app's move. The file is left alone until the user moves it themselves.</summary>
    Undone,
}
