namespace ScreenSorter.Core.Configuration;

/// <summary>Example store settings. Bound from <c>Examples</c>.</summary>
public sealed class ExamplesOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Examples";

    /// <summary>
    /// Names the set of stored vectors. Vectors only compare within one model and preprocessing, so changing either needs
    /// a new name; the examples are then embedded again from the decision log.
    /// </summary>
    public string Collection { get; set; } = "screenshots";

    /// <summary>Number of nearest examples compared for a new screenshot.</summary>
    public int TopK { get; set; } = 5;
}
