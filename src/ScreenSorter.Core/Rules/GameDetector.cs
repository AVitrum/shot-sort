using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Core.Rules;

/// <summary>
/// Recognizes games by their exe path under a known launcher library or, for games installed elsewhere,
/// by their exe name, and resolves their title.
/// </summary>
/// <remarks>
/// Under a library, title order: alias map, launcher metadata (<see cref="IGameTitleProvider"/>), install folder
/// name, window title; the alias map is applied again to the result. Elsewhere: alias of the exe name, or
/// the <see cref="IGameCatalog"/> title with the same <see cref="SortedPathBuilder.MatchKey"/>.
/// </remarks>
public sealed class GameDetector(IOptions<RulesOptions> options, IGameTitleProvider titles, IGameCatalog catalog)
{
    private readonly RulesOptions _options = options.Value;

    /// <summary>Returns the game title if the focused exe is a game.</summary>
    /// <param name="focus">The focused window.</param>
    /// <returns>The title, or <see langword="null"/> when nothing marks the exe as a game.</returns>
    public string? TryDetect(FocusSnapshot focus)
    {
        var path = focus.ExePath;
        if (string.IsNullOrEmpty(path))
            return null;
        if (_options.GameExcludeMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return null;

        var exeName = Path.GetFileNameWithoutExtension(path);
        var folder = InstallFolder(path);
        if (folder is null)
            return Alias(exeName) ?? KnownTitle(exeName);

        var title = Alias(exeName) ?? Alias(folder) ?? titles.TryGetTitle(path) ?? folder;
        if (string.IsNullOrWhiteSpace(title))
            title = focus.WindowTitle;
        return Alias(title) ?? title;
    }

    // Whole-name match only: a partial one would turn any app that shares a word with a game into that game.
    private string? KnownTitle(string exeName)
    {
        var key = SortedPathBuilder.MatchKey(exeName);
        var title = catalog.GetTitles().FirstOrDefault(t => SortedPathBuilder.MatchKey(t) == key);
        return title is null ? null : Alias(title) ?? title;
    }

    private string? InstallFolder(string path)
    {
        foreach (var marker in _options.GameLibraryMarkers)
        {
            var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                continue;
            var rest = path[(i + marker.Length)..];
            var end = rest.IndexOfAny(['\\', '/']);
            // An exe directly under the marker has no install folder of its own.
            if (end > 0)
                return rest[..end];
        }
        return null;
    }

    private string? Alias(string key) =>
        _options.GameAliases.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}
