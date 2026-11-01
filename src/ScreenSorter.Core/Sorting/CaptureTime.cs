using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenSorter.Core.Sorting;

/// <summary>Reads the capture time encoded in automatically generated screenshot names.</summary>
public static partial class CaptureTime
{
    [GeneratedRegex(@"^Screenshot (\d{4}-\d{2}-\d{2} \d{6})(?: \(\d+\))?(?: - Copy(?: \(\d+\))?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SnippingToolName();

    [GeneratedRegex(@"^.+_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(?:_\d+)?(?: - Copy(?: \(\d+\))?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SortedName();

    /// <summary>
    /// Parses Snipping Tool names (<c>Screenshot 2026-10-31 210412.png</c>, optionally with <c> (2)</c>) and this app's own
    /// (<c>Deadlock_2026-10-31_21-04-12.png</c>, optionally with <c>_2</c>), with or without the <c> - Copy</c> that Windows
    /// adds to a duplicate. Anything else counts as a name the user chose.
    /// </summary>
    /// <param name="fileName">File name with or without directory.</param>
    /// <returns>The local capture time, or <see langword="null"/> if the name has another format.</returns>
    public static DateTime? FromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Parse(SnippingToolName(), stem, "yyyy-MM-dd HHmmss") ?? Parse(SortedName(), stem, "yyyy-MM-dd_HH-mm-ss");
    }

    private static DateTime? Parse(Regex pattern, string stem, string format)
    {
        var m = pattern.Match(stem);
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, format,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
            ? t
            : null;
    }
}
