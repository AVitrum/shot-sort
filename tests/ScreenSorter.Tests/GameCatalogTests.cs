using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Infrastructure.Games;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

/// <summary>Uses a fake Steam/Epic layout in a temp folder; <c>SteamPath</c> is set, so the registry is never read.</summary>
public sealed class GameCatalogTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_temp.Path, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Manifest(string steamapps, int id, string name, string dir) =>
        File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{id}.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{name}\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n");

    private GameCatalog Catalog(FakeTime? time = null) => new(
        Opt(new GameCatalogOptions
        {
            SteamPath = Path.Combine(_temp.Path, "Steam"),
            EpicManifestsFolder = Path.Combine(_temp.Path, "Epic"),
            ExcludeTitles = ["Steamworks Common", "Wallpaper Engine"],
            ExtraTitles = ["Wuthering Waves"],
            RefreshMinutes = 10,
        }),
        Opt(new PathsOptions { SortedRoot = Path.Combine(_temp.Path, "Shots") }),
        Opt(DefaultRules()),
        time ?? new FakeTime(T0),
        NullLogger<GameCatalog>.Instance);

    private void Layout()
    {
        var main = Dir("Steam", "steamapps");
        var second = Dir("Lib2", "steamapps");
        File.WriteAllText(Path.Combine(main, "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + Path.Combine(_temp.Path, "Steam").Replace(@"\", @"\\") + "\"\n\t}\n" +
            "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + Path.Combine(_temp.Path, "Lib2").Replace(@"\", @"\\") + "\"\n\t}\n}\n");
        Manifest(main, 228980, "Steamworks Common Redistributables", "Steamworks Shared");
        Manifest(main, 1422450, "Deadlock", "Deadlock");
        Manifest(second, 553850, "HELLDIVERS™ 2", "Helldivers 2");
        Manifest(second, 431960, "Wallpaper Engine", "wallpaper_engine");

        var epic = Dir("Epic");
        File.WriteAllText(Path.Combine(epic, "a.item"), "{\"DisplayName\":\"Genshin Impact\",\"InstallLocation\":\"C:\\\\Games\"}");
        File.WriteAllText(Path.Combine(epic, "broken.item"), "{not json");

        Dir("Shots", "Games", "Helldivers 2");
    }

    [Fact]
    public void GetTitles_MergesSourcesDedupesAndExcludes()
    {
        Layout();

        var titles = Catalog().GetTitles();

        // Folder spelling first, then alias values, extras, Steam (all libraries), Epic.
        Assert.Equal(["Helldivers 2", "Counter-Strike 2", "Wuthering Waves", "Deadlock", "Genshin Impact"], titles);
    }

    [Fact]
    public void GetTitles_LauncherScanIsCached_FoldersAreFresh()
    {
        Layout();
        var time = new FakeTime(T0);
        var catalog = Catalog(time);
        catalog.GetTitles();

        Manifest(Path.Combine(_temp.Path, "Steam", "steamapps"), 570, "Dota 2", "dota 2 beta");
        Dir("Shots", "Games", "Hades");
        var cached = catalog.GetTitles();
        time.Now = T0.AddMinutes(11);
        var refreshed = catalog.GetTitles();

        Assert.Contains("Hades", cached);
        Assert.DoesNotContain("Dota 2", cached);
        Assert.Contains("Dota 2", refreshed);
    }

    [Fact]
    public void GetTitles_NoLaunchers_StillReturnsAliasesAndExtras() =>
        Assert.Equal(["Counter-Strike 2", "Wuthering Waves"], Catalog().GetTitles());
}
