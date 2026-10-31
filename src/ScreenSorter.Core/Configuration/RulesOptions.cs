namespace ScreenSorter.Core.Configuration;

/// <summary>Focus-based sorting rules. Bound from <c>Rules</c>.</summary>
public sealed class RulesOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Rules";

    /// <summary>Token usable in <see cref="AppRule.Processes"/> that stands for every entry of <see cref="BrowserProcesses"/>.</summary>
    public const string BrowserToken = "@browser";

    /// <summary>Process names of web browsers.</summary>
    public List<string> BrowserProcesses { get; set; } = [];

    /// <summary>
    /// Path fragments (e.g. <c>\steamapps\common\</c>) that mark an exe as a game.
    /// The path segment right after the marker is the game's install folder.
    /// </summary>
    public List<string> GameLibraryMarkers { get; set; } = [];

    /// <summary>Path fragments that exclude an exe from game detection (launchers living under a library root).</summary>
    public List<string> GameExcludeMarkers { get; set; } = [];

    /// <summary>
    /// Maps an exe name (without extension), install folder or detected title to the preferred game title.
    /// Keys are matched case-insensitively.
    /// </summary>
    public Dictionary<string, string> GameAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordered app rules; the first match wins. Game detection runs before these.</summary>
    public List<AppRule> AppRules { get; set; } = [];
}

/// <summary>One focus rule: which app (and optionally which window title) goes to which folder.</summary>
public sealed class AppRule
{
    /// <summary>Process names this rule applies to; may contain <see cref="RulesOptions.BrowserToken"/>.</summary>
    public List<string> Processes { get; set; } = [];

    /// <summary>
    /// Optional. The rule matches only if the window title contains any of these (case-insensitive).
    /// Empty means any title.
    /// </summary>
    public List<string> TitleContains { get; set; } = [];

    /// <summary>Target folder relative to the sorted root: <c>Category</c> or <c>Category/Sub</c>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Optional file-name prefix; defaults to the last segment of <see cref="Target"/>.</summary>
    public string? Name { get; set; }
}
