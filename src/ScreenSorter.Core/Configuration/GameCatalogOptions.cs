namespace ScreenSorter.Core.Configuration;

/// <summary>Where known game titles come from. Bound from <c>GameCatalog</c>.</summary>
public sealed class GameCatalogOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "GameCatalog";

    /// <summary>Steam install folder; empty reads <c>HKCU\Software\Valve\Steam\SteamPath</c>.</summary>
    public string SteamPath { get; set; } = "";

    /// <summary>Folder with Epic Games Launcher <c>*.item</c> manifests; empty disables Epic.</summary>
    public string EpicManifestsFolder { get; set; } = "";

    /// <summary>Launcher entries that are not games; a title is dropped if it starts with any of these (case-insensitive).</summary>
    public List<string> ExcludeTitles { get; set; } = [];

    /// <summary>Titles to always offer, e.g. games from launchers that are not scanned.</summary>
    public List<string> ExtraTitles { get; set; } = [];

    /// <summary>How long launcher scans are cached. Existing game folders are always read fresh.</summary>
    public int RefreshMinutes { get; set; } = 10;
}
