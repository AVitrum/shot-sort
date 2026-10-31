using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ScreenSorter.Core.Rules;

namespace ScreenSorter.Infrastructure.Games;

/// <summary>
/// Reads game titles from Steam's <c>steamapps\appmanifest_*.acf</c> files by matching the
/// manifest's <c>installdir</c> to the exe's folder under <c>steamapps\common</c>.
/// Results are cached per install folder for the lifetime of the process.
/// </summary>
public sealed partial class SteamGameTitleProvider : IGameTitleProvider
{
    private const string CommonMarker = @"\steamapps\common\";
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^\\s*\"(installdir|name)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ManifestField();

    /// <inheritdoc />
    public string? TryGetTitle(string exePath)
    {
        var i = exePath.IndexOf(CommonMarker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return null;

        var rest = exePath[(i + CommonMarker.Length)..];
        var end = rest.IndexOf('\\');
        if (end <= 0)
            return null;

        var steamapps = exePath[..(i + @"\steamapps".Length)];
        var installDir = rest[..end];
        return _cache.GetOrAdd(steamapps + "|" + installDir, _ => FindTitle(steamapps, installDir));
    }

    private static string? FindTitle(string steamapps, string installDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                string? dir = null, name = null;
                foreach (Match m in ManifestField().Matches(File.ReadAllText(file)))
                {
                    if (m.Groups[1].Value == "installdir")
                        dir ??= m.Groups[2].Value;
                    else
                        name ??= m.Groups[2].Value;
                }
                if (name is not null && string.Equals(dir, installDir, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable library just means no manifest title; detection falls back to the folder name.
        }
        return null;
    }
}
