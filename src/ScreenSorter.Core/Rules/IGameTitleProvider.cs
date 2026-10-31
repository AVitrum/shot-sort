namespace ScreenSorter.Core.Rules;

/// <summary>Looks up a game's display title from launcher metadata (e.g. Steam app manifests).</summary>
public interface IGameTitleProvider
{
    /// <summary>Returns the launcher's title for the game that owns <paramref name="exePath"/>.</summary>
    /// <param name="exePath">Full path of the game exe.</param>
    /// <returns>The title, or <see langword="null"/> if no metadata was found.</returns>
    string? TryGetTitle(string exePath);
}
