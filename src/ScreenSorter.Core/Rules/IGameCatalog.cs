namespace ScreenSorter.Core.Rules;

/// <summary>Known game titles, offered to the vision model as the only valid answers.</summary>
public interface IGameCatalog
{
    /// <summary>Returns the known titles, deduplicated, existing <c>Games/*</c> folder spellings first.</summary>
    /// <returns>Titles; empty if nothing is known (the model then answers free text).</returns>
    IReadOnlyList<string> GetTitles();
}
