using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenSorter.Core.Sorting;

/// <summary>Reads the capture time that Snipping Tool encodes in its file names.</summary>
public static partial class CaptureTime
{
    [GeneratedRegex(@"^Screenshot (\d{4}-\d{2}-\d{2} \d{6})", RegexOptions.CultureInvariant)]
    private static partial Regex SnippingToolName();

    /// <summary>Parses names like <c>Screenshot 2026-10-31 210412.png</c>.</summary>
    /// <param name="fileName">File name with or without directory.</param>
    /// <returns>The local capture time, or <see langword="null"/> if the name has another format.</returns>
    public static DateTime? FromFileName(string fileName)
    {
        var m = SnippingToolName().Match(Path.GetFileName(fileName));
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
            ? t
            : null;
    }
}
