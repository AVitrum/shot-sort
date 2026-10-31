using System.Text.RegularExpressions;

namespace ScreenSorter.Infrastructure.Games;

/// <summary>Readers for Steam's VDF/ACF text files (only the fields this app needs).</summary>
internal static partial class SteamFiles
{
    [GeneratedRegex("^\\s*\"(installdir|name)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ManifestField();

    [GeneratedRegex("^\\s*\"path\"\\s+\"([^\"]*)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LibraryPath();

    /// <summary>Returns the <c>steamapps</c> folders of all libraries listed in <c>steamapps\libraryfolders.vdf</c>.</summary>
    /// <param name="steamRoot">Steam install folder.</param>
    /// <returns>Existing <c>steamapps</c> folders, including the one under <paramref name="steamRoot"/>.</returns>
    public static IReadOnlyList<string> SteamAppsFolders(string steamRoot)
    {
        var result = new List<string>();
        var main = Path.Combine(steamRoot, "steamapps");
        var vdf = Path.Combine(main, "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in LibraryPath().Matches(File.ReadAllText(vdf)))
            {
                // VDF escapes backslashes: "D:\\SteamLibrary".
                result.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"));
            }
        }
        if (!result.Any(r => string.Equals(Path.GetFullPath(r), Path.GetFullPath(main), StringComparison.OrdinalIgnoreCase)))
            result.Add(main);
        return result.Where(Directory.Exists).ToList();
    }

    /// <summary>Reads all app manifests in a <c>steamapps</c> folder.</summary>
    /// <param name="steamApps">A <c>steamapps</c> folder.</param>
    /// <returns><c>(InstallDir, Name)</c> per manifest that has a name; unreadable manifests are skipped.</returns>
    public static IEnumerable<(string? InstallDir, string Name)> Manifests(string steamApps)
    {
        foreach (var file in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string? dir = null, name = null;
            // First occurrence wins: AppState's own fields come before nested sections.
            foreach (Match m in ManifestField().Matches(text))
            {
                if (m.Groups[1].Value == "installdir")
                    dir ??= m.Groups[2].Value;
                else
                    name ??= m.Groups[2].Value;
            }
            if (name is not null)
                yield return (dir, name);
        }
    }
}
