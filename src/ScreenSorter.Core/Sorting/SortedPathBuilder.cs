using System.Text;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>Builds target paths: <c>&lt;Root&gt;/&lt;Category&gt;[/&lt;Sub&gt;]/&lt;Name&gt;_yyyy-MM-dd_HH-mm-ss.ext</c>.</summary>
public static class SortedPathBuilder
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Builds the desired target path; collisions are resolved later with <see cref="WithSuffix"/>.</summary>
    /// <param name="root">Sorted root folder.</param>
    /// <param name="classification">Where the file belongs.</param>
    /// <param name="captureLocal">Local capture time, used in the file name.</param>
    /// <param name="extension">Original extension, including the dot.</param>
    /// <param name="existingSubfolders">
    /// Existing folder names under the category. A sub-folder that matches one of them ignoring case,
    /// spaces and punctuation reuses that spelling, so <c>ELDEN RING</c> lands in <c>Elden Ring</c>.
    /// </param>
    /// <returns>The full target path.</returns>
    public static string Build(string root, Classification classification, DateTime captureLocal,
        string extension, IEnumerable<string> existingSubfolders)
    {
        var folder = Path.Combine(root, Sanitize(classification.Category));
        string? sub = null;
        if (!string.IsNullOrWhiteSpace(classification.SubFolder))
        {
            sub = Sanitize(classification.SubFolder);
            var key = MatchKey(sub);
            sub = existingSubfolders.FirstOrDefault(e => MatchKey(e) == key) ?? sub;
            folder = Path.Combine(folder, sub);
        }

        var name = Sanitize(string.IsNullOrWhiteSpace(classification.Name) ? sub ?? classification.Category : classification.Name);
        return Path.Combine(folder, $"{name}_{captureLocal:yyyy-MM-dd_HH-mm-ss}{extension.ToLowerInvariant()}");
    }

    /// <summary>Inserts a collision suffix before the extension.</summary>
    /// <param name="path">A target path.</param>
    /// <param name="n">Suffix number, starting at 2.</param>
    /// <returns><c>dir/name_n.ext</c>.</returns>
    public static string WithSuffix(string path, int n) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "",
            $"{Path.GetFileNameWithoutExtension(path)}_{n}{Path.GetExtension(path)}");

    /// <summary>Makes a string safe as a single Windows path segment.</summary>
    /// <param name="value">Raw name, e.g. a window or game title.</param>
    /// <returns>A non-empty name without invalid characters, trailing dots or reserved device names.</returns>
    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '™' or '®' or '©')
                continue;
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? ' ' : c);
        }

        var result = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (result.Length > 80)
            result = result[..80].TrimEnd(' ', '.');
        if (result.Length == 0)
            return "Unknown";
        return ReservedDeviceNames.Contains(result) ? result + "_" : result;
    }

    private static string MatchKey(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
